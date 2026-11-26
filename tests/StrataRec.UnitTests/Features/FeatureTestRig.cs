using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Bindings;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Builds a <see cref="FeaturePlan"/> straight from feature definitions (no catalog/pipeline compilation, so derived
/// features can use hand-written <see cref="CompiledExpression"/>s) and creates requests against it.
/// </summary>
internal sealed class FeatureTestRig
{
    public const int UserIdContext = 0;

    /// <summary>
    /// The default source budget and request deadline: a minute, longer than any test's work on any machine, and short
    /// enough that a hung call still fails its test. A test that is about a budget passes its own; one that needs some
    /// calls cut off while others answer cuts them off itself (the caller's token), since a budget is the whole fetch's.
    /// </summary>
    public const int UntimedMs = 60_000;

    public static readonly EntityDefinition Item = EntityDefinition.Of("item", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64));

    public static readonly EntityDefinition User = EntityDefinition.Of("user", EntityKeyPartDefinition.RequestInput("user_id", EntityKeyType.Int64));

    private readonly List<FeatureRuntimeInfo> _features = new();
    private readonly List<ColumnType> _slots = new() { ColumnType.Int64 };
    private readonly Dictionary<string, FeatureSourceBinding> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFeatureSourceConnector> _connectors = new(StringComparer.Ordinal);
    private readonly TestLocalTables _localTables = new();
    private readonly Dictionary<string, ICacheStore> _cacheStores = new(StringComparer.Ordinal);

    public FeatureTestRig()
    {
        Bindings = new BindingRegistry()
            .Add(new MemoryFeatureConnectorFactory())
            .Add(new SnapshotFilesUpdateSourceFactory())
            .Add(new TestConnectorFactory(_connectors))
            .Add(new TestUpdateSourceFactory())
            .Add(new TestCacheStoreFactory(_cacheStores));
    }

    /// <summary>
    /// The binding types the plan's sources may name: the built-in connector and update source, <c>test@1</c>,
    /// <c>test_updates@1</c>, the distributed cache store <c>test_cache@1</c>, and what a test adds.
    /// </summary>
    public BindingRegistry Bindings { get; }
    private int _contextCount = 1; // context id 0 is the request input user_id
    private readonly Dictionary<string, int> _inputs = new(StringComparer.Ordinal) { ["user_id"] = UserIdContext };

    public FeaturePlan Plan { get; private set; } = null!;

    /// <summary>The local tables the plan reads, by source name (one entity per source in these tests).</summary>
    public IReadOnlyDictionary<string, LocalFeatureTable> Tables => _localTables.Tables;

    /// <summary>The clock of the plan's cache tables: it moves only when a test moves it.</summary>
    public ManualClock Clock { get; } = new();

    public static EntityDefinition ForeignKey(string name, string keyFeature) =>
        EntityDefinition.Of(name, EntityKeyPartDefinition.Feature(keyFeature, EntityKeyType.Int64));

    /// <summary>Binds a source to a test connector instance.</summary>
    public FeatureTestRig Source(string name, IFeatureSourceConnector connector, int timeoutMs = UntimedMs, int? maxBatch = null)
    {
        _connectors[name] = connector;
        _sources[name] = new FeatureSourceBinding { Connector = TestConnectorFactory.TypeName, TimeoutMs = timeoutMs, MaxBatch = maxBatch };
        return this;
    }

    /// <summary>Binds a source to the built-in <c>memory@1</c> connector with inline data.</summary>
    public FeatureTestRig MemorySource(string name, string json, int timeoutMs = UntimedMs, int? maxBatch = null)
    {
        using var document = JsonDocument.Parse(json);
        _sources[name] = new FeatureSourceBinding
        {
            Connector = MemoryFeatureConnectorFactory.TypeName,
            TimeoutMs = timeoutMs,
            MaxBatch = maxBatch,
            Settings = new Dictionary<string, JsonElement> { ["data"] = document.RootElement.Clone() },
        };
        return this;
    }

    /// <summary>
    /// Binds a source with mode <c>local</c>. Its table is loaded with <paramref name="load"/> (one complete reload) when
    /// the plan is built; with no <paramref name="load"/> it is never loaded.
    /// </summary>
    public FeatureTestRig LocalSource(string name, Action<IFeatureTableWriter>? load, string maxBytes = "64MiB")
    {
        _sources[name] = new FeatureSourceBinding
        {
            Mode = "local", MaxBytes = maxBytes, Bootstrap = new UpdateSourceBinding { Source = TestUpdateSourceFactory.TypeName },
        };
        _localTables.Loads[name] = load;
        return this;
    }

    /// <summary>
    /// Binds a source with mode <c>cache</c> in front of a test connector, with <paramref name="distributed"/> as its
    /// distributed tier when given. Its table's writer does not run: <see cref="WriteBack"/> applies what reads handed it.
    /// </summary>
    public FeatureTestRig CacheSource(
        string name, IFeatureSourceConnector connector, long ttlMs = 60_000, long staleIfErrorMs = 0, ICacheStore? distributed = null,
        int? distributedTimeoutMs = null, int timeoutMs = UntimedMs, int? maxBatch = null, string maxBytes = "64MiB")
    {
        _connectors[name] = connector;
        if (distributed is not null)
        {
            _cacheStores[name] = distributed;
        }

        _sources[name] = new FeatureSourceBinding
        {
            Mode = "cache",
            Connector = TestConnectorFactory.TypeName,
            TimeoutMs = timeoutMs,
            MaxBatch = maxBatch,
            MaxBytes = maxBytes,
            Cache = new CacheBinding
            {
                TtlMs = ttlMs,
                StaleIfErrorMs = staleIfErrorMs,
                Distributed = distributed is null ? null : new DistributedCacheBinding { Store = TestCacheStoreFactory.TypeName, TimeoutMs = distributedTimeoutMs },
            },
        };
        return this;
    }

    /// <summary>Binds a source with mode <c>cache</c> in front of <c>memory@1</c> with inline data.</summary>
    public FeatureTestRig MemoryCacheSource(string name, string json, long ttlMs = 60_000, ICacheStore? distributed = null)
    {
        MemorySource(name, json);
        var fetched = _sources[name];
        if (distributed is not null)
        {
            _cacheStores[name] = distributed;
        }

        _sources[name] = new FeatureSourceBinding
        {
            Mode = "cache",
            Connector = fetched.Connector,
            TimeoutMs = fetched.TimeoutMs,
            Settings = fetched.Settings,
            MaxBytes = "64MiB",
            Cache = new CacheBinding
            {
                TtlMs = ttlMs,
                Distributed = distributed is null ? null : new DistributedCacheBinding { Store = TestCacheStoreFactory.TypeName },
            },
        };
        return this;
    }

    /// <summary>Applies what reads handed the cache table of <paramref name="source"/>, as its writer would; returns the batches.</summary>
    public int WriteBack(string source)
    {
        var table = Tables[source];
        return table.Cache!.WriteQueued(table.Writer);
    }

    /// <summary>Binds a source as written, for tests of the binding checks.</summary>
    public FeatureTestRig Bind(string name, FeatureSourceBinding binding)
    {
        _sources[name] = binding;
        return this;
    }

    public int Physical(string name, EntityDefinition entity, ColumnType type, string source, ScalarValue? defaultValue = null, string? field = null)
    {
        var hasDefault = defaultValue is not null;
        var finalType = hasDefault ? type.AsNonNullable() : type.AsNullable();
        var definition = new FeatureDefinition
        {
            Name = name,
            Entity = entity,
            Type = finalType,
            Source = source,
            Field = field ?? name[(name.IndexOf('.') + 1)..],
            HasDefault = hasDefault,
            Default = defaultValue ?? ScalarValue.Missing,
            Pointer = "/features/" + name,
        };
        return Add(definition, finalType, null, Array.Empty<int>());
    }

    public int Derived(
        string name, EntityDefinition entity, ColumnType type, CompiledExpression expression, int[] dependencies,
        ScalarValue? defaultValue = null)
    {
        var hasDefault = defaultValue is not null;
        var finalType = hasDefault ? type.AsNonNullable() : type;
        var definition = new FeatureDefinition
        {
            Name = name,
            Entity = entity,
            Type = finalType,
            Expr = expression.Source,
            HasDefault = hasDefault,
            Default = defaultValue ?? ScalarValue.Missing,
            Pointer = "/features/" + name,
        };
        return Add(definition, finalType, expression, dependencies);
    }

    /// <summary>Declares a request input besides user_id, which a key part may read; returns its context id (set its value in
    /// <see cref="TestRequest.Context"/> before the fetch).</summary>
    public int RequestInput(string name)
    {
        _inputs[name] = _contextCount;
        return _contextCount++;
    }

    public int SlotOf(int feature) => _features[feature].Slot;

    public int ContextIdOf(int feature) => _features[feature].ContextId;

    public FeaturePlan Build()
    {
        var diagnostics = TryBuild();
        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Items.Select(d => d.ToString())));
        return Plan;
    }

    /// <summary>Builds the plan and returns the diagnostics instead of asserting there are none.</summary>
    public DiagnosticBag TryBuild(string? baseDirectory = null)
    {
        var diagnostics = new DiagnosticBag();
        Plan = FeaturePlan.Create(
            _features,
            new SourceBindingArtifact { Environment = "test", Sources = _sources },
            new BindingResolver(Bindings, UnsharedInstances.Instance, baseDirectory, "bindings:test", diagnostics),
            input => _inputs.TryGetValue(input, out var id) ? id : -1,
            _localTables,
            clock: Clock);
        return diagnostics;
    }

    /// <param name="capacity">Rows the candidate table can hold, for tests that append rows later.</param>
    public TestRequest NewRequest(
        long[] candidates, long? userId = null, IBufferPool? pool = null, int deadlineMs = UntimedMs, IFeatureCallHook? hook = null, int capacity = 0)
    {
        var memory = new RequestMemory(pool);
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, _slots), Math.Max(Math.Max(1, capacity), candidates.Length), memory);
        if (candidates.Length > 0)
        {
            table.AppendRows(candidates);
        }

        var context = new ScalarValue[_contextCount];
        if (userId is { } user)
        {
            context[UserIdContext] = ScalarValue.FromInt64(user);
        }

        var deadline = Stopwatch.GetTimestamp() + (deadlineMs * Stopwatch.Frequency / 1000);
        var fetcher = new FeatureFetcher(Plan, table, context, new OutputBindings(1, memory), Array.Empty<ScalarValue>(), memory, deadline, hook: hook);
        return new TestRequest(Plan, fetcher, table, memory, context);
    }

    private int Add(FeatureDefinition definition, ColumnType type, CompiledExpression? expression, int[] dependencies)
    {
        var index = _features.Count;
        int slot = -1, contextId = -1;
        if (definition.IsContext)
        {
            contextId = _contextCount++;
        }
        else
        {
            slot = _slots.Count;
            _slots.Add(type);
        }

        _features.Add(new FeatureRuntimeInfo
        {
            Index = index,
            Definition = definition,
            Type = type,
            Slot = slot,
            ContextId = contextId,
            Expression = expression,
            Dependencies = [.. dependencies],
        });
        return index;
    }
}

internal sealed record TestRequest(FeaturePlan Plan, FeatureFetcher Fetcher, CandidateTable Table, RequestMemory Memory, ScalarValue[] Context)
{
    public ValueTask<FeatureFillResult> EnsureAsync(params int[] features) => Fetcher.EnsureAsync(features, CancellationToken.None);

    public bool IsFilled(int feature, int row) =>
        Table.TryGetSlot(Plan.Features[feature].Slot) is not null && Bits.Get(Table.GetFilled(Plan.Features[feature].Slot), row);

    /// <summary>Value of a candidate feature at a physical row; asserts the row is filled first.</summary>
    public ScalarValue Value(int feature, int row)
    {
        Assert.True(IsFilled(feature, row), $"feature {Plan.Features[feature].Definition.Name} is not filled at row {row}");
        return ColumnValues.Get(Table.TryGetSlot(Plan.Features[feature].Slot)!, row);
    }

    public ScalarValue ContextValue(int feature) => Context[Plan.Features[feature].ContextId];
}

/// <summary>Gives each local spec a new table, loaded at once by the rig's load action for the source.</summary>
internal sealed class TestLocalTables : ILocalTableProvider
{
    public Dictionary<string, Action<IFeatureTableWriter>?> Loads { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, LocalFeatureTable> Tables { get; } = new(StringComparer.Ordinal);

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        var table = new LocalFeatureTable(spec.Schema, spec.Options);
        Tables[spec.Schema.Source] = table;
        if (Loads.TryGetValue(spec.Schema.Source, out var load) && load is not null)
        {
            table.Writer.BeginReload();
            load(table.Writer);
            table.Writer.CompleteReload(UpdatePosition.Empty);
        }

        return table;
    }
}

/// <summary>
/// A clock a test moves by hand (cache write times and expiry); starts at 2026-01-01. Its timestamps (what the
/// distributed tier's time is judged by) are the system's until <see cref="HoldTimestamps"/>.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private long _timestamp;
    private long _stepPerRead;
    private int _held;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public long NowMs => GetUtcNow().ToUnixTimeMilliseconds();

    public void AdvanceMs(long milliseconds) => Interlocked.Add(ref _ticks, milliseconds * TimeSpan.TicksPerMillisecond);

    /// <summary>
    /// From now on the timestamps stand still, but for <see cref="AdvanceTimestamps"/> and, if given, a step every time
    /// one is read: a deadline taken from them passes only when the test says.
    /// </summary>
    public void HoldTimestamps(TimeSpan stepPerRead = default)
    {
        Interlocked.Exchange(ref _stepPerRead, Timestamps(stepPerRead));
        Interlocked.Exchange(ref _timestamp, base.GetTimestamp());
        Volatile.Write(ref _held, 1);
    }

    /// <summary>
    /// Moves the held timestamps, back too and below zero: a timestamp counts from when the machine started, so on one
    /// that started minutes ago (a CI runner) ten minutes back is a negative timestamp, and still a held one.
    /// </summary>
    public void AdvanceTimestamps(TimeSpan by) => Interlocked.Add(ref _timestamp, Timestamps(by));

    private long Timestamps(TimeSpan time) => (long)((Int128)time.Ticks * TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>Runs before each timestamp is read: a test's clock that stalls or throws there.</summary>
    public Action? BeforeTimestamp { get; set; }

    public override long GetTimestamp()
    {
        BeforeTimestamp?.Invoke();
        var step = Interlocked.Read(ref _stepPerRead);
        return Volatile.Read(ref _held) == 0 ? base.GetTimestamp() : Interlocked.Add(ref _timestamp, step) - step;
    }
}

/// <summary><c>test_cache@1</c>: serves distributed cache stores registered by source name.</summary>
internal sealed class TestCacheStoreFactory(IReadOnlyDictionary<string, ICacheStore> stores) : IBindingFactory<ICacheStore>
{
    public const string TypeName = "test_cache@1";

    public string Type => TypeName;

    public Type SettingsType => typeof(NoSettings);

    public ICacheStore Create(BindingContext context) => stores[context.Name];
}

/// <summary>
/// An in-memory distributed cache store: records by (keyspace, key), what each call asked, and a hook that can fail or
/// hold a read. Writes can be awaited (<see cref="WhenWrittenAsync"/>), since the tier writes in the background.
/// </summary>
internal sealed class FakeCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, (byte[] Record, TimeSpan TimeToLive)> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<(int Records, TaskCompletionSource Done)> _waiters = new();
    private int _written;

    public string StoreType => TestCacheStoreFactory.TypeName;

    /// <summary>Runs before a read answers: throw to fail it, or wait to hold it.</summary>
    public Func<CancellationToken, ValueTask>? BeforeGet { get; set; }

    /// <summary>The keys of each read, as written to the store (<c>{source}:{entity}:{key}</c>).</summary>
    public ConcurrentQueue<string[]> Gets { get; } = new();

    public int Written => Volatile.Read(ref _written);

    public async ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
    {
        var names = Names(keyspace, keys.Span);
        Gets.Enqueue(names);
        if (BeforeGet is { } before)
        {
            await before(cancellationToken);
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (_records.TryGetValue(names[i], out var record))
            {
                records.Span[i] = record.Record;
            }
        }
    }

    public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries.Span)
        {
            _records[keyspace.Prefix + entry.Key] = (entry.Record.ToArray(), entry.TimeToLive);
        }

        lock (_gate)
        {
            _written += entries.Length;
            _waiters.RemoveAll(w =>
            {
                if (w.Records > _written)
                {
                    return false;
                }

                w.Done.TrySetResult();
                return true;
            });
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Completes once <paramref name="records"/> records have been written in all.</summary>
    public Task WhenWrittenAsync(int records)
    {
        lock (_gate)
        {
            if (_written >= records)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((records, done));
            return done.Task;
        }
    }

    public bool TryGet(string name, out byte[] record, out TimeSpan timeToLive)
    {
        var found = _records.TryGetValue(name, out var entry);
        (record, timeToLive) = entry;
        return found;
    }

    public void Put(string name, byte[] record) => _records[name] = (record, TimeSpan.FromMinutes(1));

    private static string[] Names(CacheKeyspace keyspace, ReadOnlySpan<string> keys)
    {
        var names = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            names[i] = keyspace.Prefix + keys[i];
        }

        return names;
    }
}

/// <summary><c>test_updates@1</c>: an update source with nothing to load (the rig loads its tables directly).</summary>
internal sealed class TestUpdateSourceFactory : IBindingFactory<IFeatureUpdateSource>
{
    public const string TypeName = "test_updates@1";

    public string Type => TypeName;

    public Type SettingsType => typeof(NoSettings);

    public IFeatureUpdateSource Create(BindingContext context) => new MemoryFeatureUpdateSource();
}

/// <summary>Serves connectors registered by source name under the connector type <c>test@1</c>.</summary>
internal sealed class TestConnectorFactory(IReadOnlyDictionary<string, IFeatureSourceConnector> connectors) : IBindingFactory<IFeatureSourceConnector>
{
    public const string TypeName = "test@1";

    public string Type => TypeName;

    public Type SettingsType => typeof(NoSettings);

    public IFeatureSourceConnector Create(BindingContext context) =>
        connectors[context.Name];
}

/// <summary>A connector whose behavior is a delegate; records every call.</summary>
internal sealed class ScriptedConnector(ScriptedConnector.Handler handler) : IFeatureSourceConnector
{
    public delegate ValueTask<FetchStatus> Handler(EntityKeyBatch keys, CompiledFieldBinding[] fields, IFeatureBatchWriter writer, CancellationToken cancellationToken);

    public ConcurrentQueue<RecordedCall> Calls { get; } = new();

    public string ConnectorType => TestConnectorFactory.TypeName;

    /// <summary>The layouts the plan asked about, in order.</summary>
    public ConcurrentQueue<EntityKeyLayout> Layouts { get; } = new();

    /// <summary>Whether a layout is supported; every one by default.</summary>
    public Func<EntityKeyLayout, bool> SupportsLayout { get; init; } = static _ => true;

    public bool Supports(EntityKeyLayout layout)
    {
        Layouts.Enqueue(layout);
        return SupportsLayout(layout);
    }

    public ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        var bindings = fields.ToArray();
        Calls.Enqueue(RecordedCall.Of(keys, bindings.Select(f => f.Field).ToArray(), writer));
        return handler(keys, bindings, writer, cancellationToken);
    }

    /// <summary>Synchronously writes <c>valueOf(key, field)</c> for every key and field (null = leave missing).</summary>
    public static ScriptedConnector Values(Func<long, string, object?> valueOf) => new((keys, fields, writer, _) =>
    {
        WriteAll(keys, fields, writer, valueOf);
        return ValueTask.FromResult(FetchStatus.Ok);
    });

    public static void WriteAll(EntityKeyBatch keys, CompiledFieldBinding[] fields, IFeatureBatchWriter writer, Func<long, string, object?> valueOf)
    {
        for (var k = 0; k < writer.KeyCount; k++)
        {
            var key = keys.Key(k, 0).Int64Value;
            foreach (var field in fields)
            {
                Write(writer, field, k, valueOf(key, field.Field));
            }
        }
    }

    public static void Write(IFeatureBatchWriter writer, CompiledFieldBinding field, int keyIndex, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case double d when field.Type.Kind == StorageKind.Float64:
                writer.GetValues<double>(field.FieldIndex).Span[keyIndex] = d;
                break;
            case double d when field.Type.Kind == StorageKind.Float32:
                writer.GetValues<float>(field.FieldIndex).Span[keyIndex] = (float)d;
                break;
            case long l:
                writer.GetValues<long>(field.FieldIndex).Span[keyIndex] = l;
                break;
            case float[] v:
                writer.SetFloat32List(field.FieldIndex, keyIndex, v);
                return;
            case string s:
                writer.SetString(field.FieldIndex, keyIndex, s);
                return;
            case long[] list:
                writer.SetInt64List(field.FieldIndex, keyIndex, list);
                return;
            default:
                throw new ArgumentException($"Unsupported test value {value} for {field.Type}.");
        }

        writer.SetValid(field.FieldIndex, keyIndex);
    }
}

/// <summary>
/// One connector call: for a one-part key, <see cref="Keys"/> are the candidate keys (empty for a context key, which is
/// <see cref="ContextKey"/>), as calls had them before keys had parts; <see cref="Tuples"/> are every key's parts.
/// </summary>
internal sealed record RecordedCall(EntityKey[] Keys, EntityKey ContextKey, string[] Fields, IFeatureBatchWriter Writer, EntityKey[][] Tuples)
{
    public static RecordedCall Of(in EntityKeyBatch batch, string[] fields, IFeatureBatchWriter writer)
    {
        var tuples = new EntityKey[batch.Count][];
        for (var i = 0; i < tuples.Length; i++)
        {
            tuples[i] = new EntityKey[batch.PartCount];
            for (var p = 0; p < batch.PartCount; p++)
            {
                tuples[i][p] = batch.Key(i, p);
            }
        }

        var context = batch.PartCount == 1 && batch.IsShared(0);
        return new RecordedCall(
            context || batch.PartCount != 1 ? Array.Empty<EntityKey>() : batch.Part(0).ToArray(),
            context ? batch.Key(0, 0) : default,
            fields,
            writer,
            tuples);
    }
}

/// <summary>
/// Buffer pool that behaves like a busy shared pool in every build configuration: rented unmanaged arrays are full of a
/// previous user's bytes, returned ones are overwritten at once (poisoned) unless cleared. Records how arrays come back.
/// </summary>
internal sealed class PoisoningPool : IBufferPool
{
    public const byte Pattern = 0xCD;
    private readonly object _gate = new();

    /// <summary>
    /// Arrays rented and not yet returned. A request gives its scratch buffers back as it goes
    /// (<see cref="RequestMemory.RentScratch{T}"/>), so while something holds its memory only its ledger is out.
    /// </summary>
    public int Outstanding { get; private set; }

    public T[] Rent<T>(int minimumLength)
    {
        var array = new T[Math.Max(1, minimumLength)];
        Fill(array);
        lock (_gate)
        {
            Outstanding++;
        }

        return array;
    }

    public void Return<T>(T[] array, bool clearArray)
    {
        lock (_gate)
        {
            Outstanding--;
        }

        if (clearArray)
        {
            Array.Clear(array);
        }
        else
        {
            Fill(array);
        }
    }

    private static void Fill<T>(T[] array)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            ref var first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array));
            MemoryMarshal.CreateSpan(ref first, array.Length * Unsafe.SizeOf<T>()).Fill(Pattern);
        }
    }
}

/// <summary>Hand-written stand-in for a compiled ColumnExpr (the real compiler is not part of this branch).</summary>
internal sealed class TestExpression(string source, ColumnType resultType, TestExpression.RowEvaluator? rows = null, Func<ScalarValue[], ScalarValue>? scalar = null)
    : CompiledExpression
{
    public delegate void RowEvaluator(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination);

    public override string Source => source;

    public override ColumnType ResultType => resultType;

    public override bool IsRowDependent => rows is not null;

    public override IReadOnlyList<Symbol> Reads => Array.Empty<Symbol>();

    public override string Describe() => source;

    public override ScalarValue EvaluateScalar(in EvalContext context) => scalar!(context.Context);

    public override void Evaluate(in EvalContext context, ReadOnlySpan<int> rows1, ColumnBuffer destination, in ScalarValue outOfRange) =>
        rows!(context, rows1, destination);

    /// <summary><c>col.&lt;source&gt;</c> for an int64 feature in <paramref name="slot"/> (missing stays missing).</summary>
    public static TestExpression CopyInt64(string name, int slot) => new(
        "col." + name,
        ColumnType.Int64.AsNullable(),
        (in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination) =>
        {
            var source = context.Table!.TryGetSlot(slot);
            var target = (ScalarColumn<long>)destination;
            foreach (var row in rows)
            {
                if (source is ScalarColumn<long> s && s.IsValid(row))
                {
                    target.Set(row, s[row]);
                }
                else
                {
                    target.SetMissing(row);
                }
            }
        });

    /// <summary><c>col.entity_id / divisor</c>.</summary>
    public static TestExpression EntityIdDividedBy(long divisor) => new(
        "col.entity_id / " + divisor,
        ColumnType.Int64,
        (in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination) =>
        {
            var ids = context.Table!.Int64Ids;
            foreach (var row in rows)
            {
                ((ScalarColumn<long>)destination).Set(row, ids[row] / divisor);
            }
        });

    /// <summary>A float64 constant for every row (e.g. the result of <c>1.0 / 0.0</c>).</summary>
    public static TestExpression ConstantFloat64(double value, bool nullable = true) => new(
        value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ColumnType.Float64.WithNullable(nullable),
        (in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination) =>
        {
            foreach (var row in rows)
            {
                ((ScalarColumn<double>)destination).Set(row, value);
            }
        });

    /// <summary>Request-level <c>ctx.&lt;feature&gt; * factor</c>.</summary>
    public static TestExpression ContextTimes(int contextId, double factor) => new(
        "ctx.x * " + factor,
        ColumnType.Float64.AsNullable(),
        scalar: context => context[contextId].IsMissing ? ScalarValue.Missing : ScalarValue.FromFloat64(context[contextId].AsFloat64() * factor));
}
