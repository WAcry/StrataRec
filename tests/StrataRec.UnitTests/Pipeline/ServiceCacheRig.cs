using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Features;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// One service, <c>m</c>, with a cache (IO step 3): its in-process table, written back by hand
/// (<see cref="Apply"/>, what the table's writer does in the background), an optional distributed tier, a clock the test
/// moves, and the service itself (<see cref="CountingService"/>). Calls go through <see cref="OperatorContext.CallAsync"/>
/// as an operator's do.
/// </summary>
internal sealed class ServiceCacheRig
{
    private RequestMemory _memory = new();

    /// <param name="cached">False binds the service without its cache: the same calls, for comparison.</param>
    /// <param name="identity">What the binding answers with (<see cref="ServiceCache.AnswerIdentity"/>): rigs sharing a store share records only with the same one.</param>
    public ServiceCacheRig(
        long ttlMs = 60_000, long staleIfErrorMs = 0, ICacheStore? store = null, int? timeoutMs = null, int distributedTimeoutMs = 10_000, bool cached = true,
        string identity = "m@1")
    {
        var options = new LocalFeatureTableOptions
        {
            MaxBytes = 1 << 20,
            Cache = new CacheTableOptions { TtlMs = ttlMs, StaleIfErrorMs = staleIfErrorMs, Clock = Clock },
        };
        Table = new LocalFeatureTable(ServiceCache.Schema("m"), options);
        Cache = new ServiceCache("m", Table, store is null ? null : new DistributedCacheTier(store), distributedTimeoutMs, identity);
        Bound = new BoundService("m", Service, IoRoles.Model, timeoutMs, cached ? Cache : null);
        Unbound = new BoundService("m", Service, IoRoles.Model, timeoutMs, null);
        _timeoutMs = timeoutMs;
    }

    private readonly int? _timeoutMs;

    public ManualClock Clock { get; } = new();

    public CountingService Service { get; } = new();

    public LocalFeatureTable Table { get; }

    public ServiceCache Cache { get; }

    public BoundService Bound { get; }

    /// <summary>The same service without its cache, for comparison.</summary>
    public BoundService Unbound { get; }

    /// <summary><paramref name="client"/> as <c>m</c>, with this rig's cache.</summary>
    public BoundService With(IServiceClient client) => new("m", client, IoRoles.Model, _timeoutMs, Cache);

    /// <summary>
    /// Each call is a request of its own: its memory is given back when the next call starts, as the engine gives it back
    /// at the request's end (a response is read before the next call). Otherwise every call's buffers stay rented.
    /// </summary>
    public bool ReleaseEachCall { get; init; }

    /// <summary>The context of the last call (its degraded reason).</summary>
    public OperatorContext? Last { get; private set; }

    /// <summary>Applies what calls handed the local tier; returns the batches.</summary>
    public int Apply() => Table.Cache!.WriteQueued(Table.Writer);

    /// <summary>A call about items <paramref name="ids"/> with input <c>x</c> (one float each), reading <paramref name="outputs"/>.</summary>
    public static ServiceCall Items(long[] ids, float[] x, params string[] outputs) => new()
    {
        Service = "m",
        BatchSize = ids.Length,
        Items = ids.Select(EntityKey.FromInt64).ToArray(),
        Inputs = new[] { NamedInput.FromFloat32("x", new[] { ids.Length }, x) },
        Outputs = outputs.Length == 0 ? new[] { "p" } : outputs,
    };

    /// <summary>A call about no items with input <c>q</c>, asking for <paramref name="limit"/> items and reading <paramref name="outputs"/>.</summary>
    public static ServiceCall Query(float q, int limit, params string[] outputs) => new()
    {
        Service = "m",
        Inputs = new[] { NamedInput.FromFloat32("q", Array.Empty<int>(), new[] { q }) },
        Limit = limit,
        Outputs = outputs,
    };

    public async Task<(ServiceStatus Status, ServiceResponse Response)> CallAsync(ServiceCall call)
    {
        if (ReleaseEachCall)
        {
            _memory.Release();
            _memory = new RequestMemory();
        }

        var context = new OperatorContext(new RequestState
        {
            Plan = null!,
            RequestId = "r",
            Inputs = new RequestInputs(),
            Seed = 1,
            Memory = _memory,
            Table = null!,
            Bindings = null!,
            Context = [],
            Params = [],
            Features = null!,
            Counters = null!,
            DeadlineTimestamp = long.MaxValue,
            Arms = default,
            Variants = [],
        }, "n", Effects.ColumnWriter, new AttemptBudget(CancellationToken.None, long.MaxValue));
        Last = context;
        var response = new ServiceResponse(_memory, call.Limit);
        var status = await context.CallAsync(Bound, call, response);
        return (status, response);
    }

    public static float[] Floats(ServiceResponse response, string name) =>
        response.TryGetValues(name, out var values) ? values.Float32.ToArray() : throw new KeyNotFoundException(name);

    public static long[] Ids(ServiceResponse response) => response.Keys.ToArray().Select(k => k.Int64Value).ToArray();
}

/// <summary>
/// <c>m</c>: about items, output <c>p</c> is <c>x × 10 + Offset</c> (<see cref="Width"/> values an item, the w-th plus
/// w), <c>q</c> is <c>[id, 2 id]</c> (int64) and, when <see cref="WriteText"/>, text <c>note</c>; about no items, it
/// adds its first <see cref="Have"/> items up to the limit and writes text <c>answer</c> and <c>p</c> (<c>[Offset,
/// Offset + 1]</c>) when read. Every call is kept.
/// </summary>
internal sealed class CountingService : IServiceClient
{
    private readonly List<Sent> _calls = new();

    public float Offset { get; set; }

    public int Width { get; set; } = 1;

    public bool Fail { get; set; }

    public bool WriteText { get; set; }

    public bool Truncated { get; set; }

    public long[] Have { get; set; } = [1, 2, 3, 4, 5];

    public List<Sent> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToList();
            }
        }
    }

    public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        var items = call.Items.ToArray().Select(k => k.Int64Value).ToArray();
        var x = call.TryGetInput("x", out var input) ? input.Float32.ToArray() : [];
        lock (_calls)
        {
            _calls.Add(new Sent(call.BatchSize, items, x, call.Limit));
        }

        if (Fail)
        {
            return ValueTask.FromResult(ServiceStatus.Failed("m is down"));
        }

        var outputs = call.Outputs.ToArray();
        if (items.Length > 0)
        {
            var p = response.GetValues<float>("p", items.Length * Width).Span;
            for (var i = 0; i < items.Length; i++)
            {
                for (var w = 0; w < Width; w++)
                {
                    p[(i * Width) + w] = (x[i] * 10) + Offset + w;
                }
            }

            if (outputs.Contains("q"))
            {
                var q = response.GetValues<long>("q", items.Length * 2).Span;
                for (var i = 0; i < items.Length; i++)
                {
                    (q[2 * i], q[(2 * i) + 1]) = (items[i], 2 * items[i]);
                }
            }

            if (WriteText)
            {
                response.SetText("note", "n");
            }
        }
        else
        {
            for (var i = 0; i < Math.Min(Have.Length, call.Limit); i++)
            {
                response.AddItem(Have[i], 1.0 / (i + 1));
            }

            if (outputs.Contains("answer"))
            {
                response.SetText("answer", "a" + Offset);
            }

            if (outputs.Contains("p"))
            {
                var p = response.GetValues<float>("p", 2).Span;
                (p[0], p[1]) = (Offset, Offset + 1);
            }
        }

        return ValueTask.FromResult(ServiceStatus.Ok with { Truncated = Truncated });
    }

    internal sealed record Sent(int Batch, long[] Items, float[] X, int Limit);
}
