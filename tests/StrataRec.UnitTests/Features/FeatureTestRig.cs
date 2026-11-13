using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Builds a <see cref="FeaturePlan"/> straight from feature definitions (no catalog/pipeline compilation, so derived
/// features can use hand-written <see cref="CompiledExpression"/>s) and creates requests against it.
/// </summary>
internal sealed class FeatureTestRig
{
    public const int UserIdContext = 0;

    public static readonly EntityDefinition Item = new()
    {
        Name = "item", Kind = EntityKind.Candidate, CandidateKeyColumn = "entity_id", CandidateKeyType = EntityKeyType.Int64,
    };

    public static readonly EntityDefinition User = new()
    {
        Name = "user", Kind = EntityKind.Context, ContextKeyInput = "user_id", ContextKeyType = EntityKeyType.Int64,
    };

    private readonly List<FeatureRuntimeInfo> _features = new();
    private readonly List<ColumnType> _slots = new() { ColumnType.Int64 };
    private readonly Dictionary<string, FeatureSourceBinding> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFeatureSourceConnector> _connectors = new(StringComparer.Ordinal);
    private int _contextCount = 1; // context id 0 is the request input user_id

    public FeaturePlan Plan { get; private set; } = null!;

    public static EntityDefinition ForeignKey(string name, string keyFeature) => new()
    {
        Name = name, Kind = EntityKind.ForeignKey, CandidateKeyColumn = keyFeature, CandidateKeyType = EntityKeyType.Int64,
    };

    /// <summary>Binds a source to a test connector instance.</summary>
    public FeatureTestRig Source(string name, IFeatureSourceConnector connector, int timeoutMs = 5000, int? maxBatch = null)
    {
        _connectors[name] = connector;
        _sources[name] = new FeatureSourceBinding { Connector = TestConnectorFactory.Type, TimeoutMs = timeoutMs, MaxBatch = maxBatch };
        return this;
    }

    /// <summary>Binds a source to the built-in <c>memory@1</c> connector with inline data.</summary>
    public FeatureTestRig MemorySource(string name, string json, int timeoutMs = 5000, int? maxBatch = null)
    {
        using var document = JsonDocument.Parse(json);
        _sources[name] = new FeatureSourceBinding
        {
            Connector = MemoryFeatureConnectorFactory.Type,
            TimeoutMs = timeoutMs,
            MaxBatch = maxBatch,
            Settings = new Dictionary<string, JsonElement> { ["data"] = document.RootElement.Clone() },
        };
        return this;
    }

    public int Physical(string name, EntityDefinition entity, ColumnType type, string source, ScalarValue? defaultValue = null)
    {
        var hasDefault = defaultValue is not null;
        var finalType = hasDefault ? type.AsNonNullable() : type.AsNullable();
        var definition = new FeatureDefinition
        {
            Name = name,
            Entity = entity,
            Type = finalType,
            Source = source,
            Field = name[(name.IndexOf('.') + 1)..],
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

    public int SlotOf(int feature) => _features[feature].Slot;

    public int ContextIdOf(int feature) => _features[feature].ContextId;

    public FeaturePlan Build()
    {
        var registry = FeatureConnectorRegistry.CreateDefault().Add(new TestConnectorFactory(_connectors));
        var diagnostics = new DiagnosticBag();
        Plan = FeaturePlan.Create(
            _features,
            new SourceBindingArtifact { Environment = "test", Sources = _sources },
            registry,
            input => input == "user_id" ? UserIdContext : -1,
            diagnostics,
            baseDirectory: null);
        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Items.Select(d => d.ToString())));
        return Plan;
    }

    public TestRequest NewRequest(long[] candidates, long? userId = null, IBufferPool? pool = null, int deadlineMs = 10_000, IFeatureCallHook? hook = null)
    {
        var memory = new RequestMemory(pool);
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, _slots), Math.Max(1, candidates.Length), memory);
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
            Dependencies = dependencies,
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

/// <summary>Serves connectors registered by source name under the connector type <c>test@1</c>.</summary>
internal sealed class TestConnectorFactory(IReadOnlyDictionary<string, IFeatureSourceConnector> connectors) : IFeatureConnectorFactory
{
    public const string Type = "test@1";

    public string ConnectorType => Type;

    public IFeatureSourceConnector Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory) =>
        connectors[sourceName];
}

/// <summary>A connector whose behavior is a delegate; records every call.</summary>
internal sealed class ScriptedConnector(ScriptedConnector.Handler handler) : IFeatureSourceConnector
{
    public delegate ValueTask<FetchStatus> Handler(EntityKeyBatch keys, CompiledFieldBinding[] fields, IFeatureBatchWriter writer, CancellationToken cancellationToken);

    public ConcurrentQueue<RecordedCall> Calls { get; } = new();

    public string ConnectorType => TestConnectorFactory.Type;

    public bool Supports(KeyShape shape) => true;

    public ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        var bindings = fields.ToArray();
        Calls.Enqueue(new RecordedCall(keys.CandidateKeys.ToArray(), keys.ContextKey, bindings.Select(f => f.Field).ToArray(), writer));
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
            var key = keys.Shape == KeyShape.Single ? keys.ContextKey.Int64Value : keys.CandidateKeys.Span[k].Int64Value;
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
                v.CopyTo(writer.GetValues<float>(field.FieldIndex).Span.Slice(keyIndex * v.Length, v.Length));
                break;
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

internal sealed record RecordedCall(EntityKey[] Keys, EntityKey ContextKey, string[] Fields, IFeatureBatchWriter Writer);

/// <summary>
/// Buffer pool that behaves like a busy shared pool in every build configuration: rented unmanaged arrays are full of a
/// previous user's bytes, returned ones are overwritten at once (poisoned) unless cleared. Records how arrays come back.
/// </summary>
internal sealed class PoisoningPool : IBufferPool
{
    public const byte Pattern = 0xCD;
    private readonly object _gate = new();

    public int Returned { get; private set; }

    public T[] Rent<T>(int minimumLength)
    {
        var array = new T[Math.Max(1, minimumLength)];
        Fill(array);
        return array;
    }

    public void Return<T>(T[] array, bool clearArray)
    {
        lock (_gate)
        {
            Returned++;
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
