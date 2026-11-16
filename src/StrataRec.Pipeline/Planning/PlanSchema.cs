using System.Diagnostics.CodeAnalysis;
using StrataRec.Core.Keys;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.Pipeline.Planning;

public enum ColumnKind : byte
{
    EntityId = 1,

    /// <summary>A catalog feature (physical or derived) with a single fixed slot.</summary>
    Feature,

    /// <summary>A node-produced column: one slot per producer, resolved through output bindings (design 4.6).</summary>
    Output,
}

/// <summary>A logical column of a plan (design 4.2).</summary>
public sealed class LogicalColumn
{
    public required int Id { get; init; }

    /// <summary>Name without the <c>col.</c> prefix, e.g. <c>score.final</c>, <c>item.clicks_7d</c>.</summary>
    public required string Name { get; init; }

    public required ColumnType Type { get; init; }

    public required ColumnKind Kind { get; init; }

    public bool Flow { get; init; }

    public bool HasDefault { get; init; }

    public ScalarValue Default { get; init; }

    /// <summary>Slot for <see cref="ColumnKind.EntityId"/> and <see cref="ColumnKind.Feature"/>; -1 for outputs.</summary>
    public int FixedSlot { get; init; } = -1;

    /// <summary>Feature index in the plan's <c>FeaturePlan</c>, or -1.</summary>
    public int FeatureIndex { get; init; } = -1;

    /// <summary>True when declared implicitly by an operator descriptor rather than in <c>columns</c>.</summary>
    public bool Implicit { get; init; }

    /// <summary>Where it is declared, e.g. <c>/columns/score.final</c>.</summary>
    public required string Pointer { get; init; }
}

public enum ContextFieldKind : byte
{
    RequestInput = 1,
    Builtin,
    Feature,
}

/// <summary>A request-level value addressable as <c>ctx.&lt;name&gt;</c> (design 7.2).</summary>
public sealed class ContextField
{
    public required int Id { get; init; }

    /// <summary>Name without the <c>ctx.</c> prefix, e.g. <c>request.user_id</c>, <c>user.tenure_days</c>.</summary>
    public required string Name { get; init; }

    public required ColumnType Type { get; init; }

    /// <summary>
    /// For request inputs, the type as declared in <c>request_schema</c>. <see cref="Type"/> is its promoted form, which
    /// expressions see; incoming values are validated against the declared type, so an <c>int32</c> or
    /// <c>list&lt;int32&gt;</c> input outside the int32 range is rejected rather than passed through.
    /// </summary>
    public ColumnType InputType
    {
        get => _inputType ?? Type;
        init => _inputType = value;
    }

    private readonly ColumnType? _inputType;

    public required ContextFieldKind Kind { get; init; }

    public int FeatureIndex { get; init; } = -1;
}

/// <summary>A flattened parameter leaf (design 11.8), e.g. <c>w_click</c> or <c>recall_quotas.cf</c>.</summary>
public sealed class ParamLeaf
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Promoted, non-nullable type: int64, float64, bool or string.</summary>
    public required ColumnType Type { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public IReadOnlyList<ScalarValue>? Enum { get; init; }

    /// <summary>Baseline value; missing for leaves introduced by an experiment arm.</summary>
    public ScalarValue Baseline { get; internal set; }

    /// <summary>For leaves of a map parameter: the map's name (e.g. <c>recall_quotas</c>).</summary>
    public string? MapName { get; init; }

    public required string Pointer { get; init; }

    /// <summary>Checks a value against type, range and enum; returns an error message or null.</summary>
    public string? Validate(in ScalarValue value)
    {
        if (value.IsMissing)
        {
            return "value is missing";
        }

        if (!value.TryConvert(Type, out var converted))
        {
            return $"{value} is not a valid {Type}";
        }

        if (Type.IsNumeric)
        {
            var d = converted.AsFloat64();
            if (Min is { } min && d < min)
            {
                return $"{value} is below the minimum {min}";
            }

            if (Max is { } max && d > max)
            {
                return $"{value} is above the maximum {max}";
            }
        }

        if (Enum is not null && !ContainsValue(Enum, converted))
        {
            return $"{value} is not one of [{string.Join(", ", Enum)}]";
        }

        return null;
    }

    private static bool ContainsValue(IReadOnlyList<ScalarValue> values, in ScalarValue value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Equals(value))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Adds a catalog feature to a plan schema the first time a name resolves to it (design 6.3): a plan holds only the
/// features it reads, so what a request sizes by feature, slot or context field does not grow with a shared catalog.
/// </summary>
internal interface IFeatureRegistrar
{
    /// <summary>
    /// Registers the catalog feature written as <paramref name="qualifiedName"/> (<c>col.item.clicks_7d</c>,
    /// <c>ctx.user.tenure_days</c>) with what it needs, unless it is registered already; false when the catalog has no
    /// such feature in that namespace, or it cannot be registered (an error reported at its declaration).
    /// </summary>
    bool TryRegister(string qualifiedName);

    /// <summary>
    /// Adds the catalog features not registered yet that a reference would register, for "did you mean" suggestions:
    /// qualified (<c>col.</c>, <c>ctx.</c>), or only the candidate features, without <c>col.</c>.
    /// </summary>
    void AddUnregisteredNames(List<string> names, bool candidateColumnsOnly);

    /// <summary>
    /// Notes that something read <paramref name="name"/> (without <c>col.</c> or <c>ctx.</c>): a registered name may
    /// also have been registered unread, as a member of a <c>cache</c> table, so a read is not the same as a registration.
    /// </summary>
    void NoteRead(string name, bool context);
}

/// <summary>
/// The name → id/slot layout of a compiled plan (design 4.2): logical columns, physical slots, context fields and
/// parameter leaves. Built once per plan; all requests share it.
/// </summary>
/// <remarks>
/// <para>It is filled while the plan compiles, by the plan builder and the operators' compile context (so its mutators
/// are internal); afterwards it is read-only like the rest of the plan.</para>
/// <para>Catalog features are added on first reference (<see cref="IFeatureRegistrar"/>): while the plan compiles, a
/// lookup of a feature not added yet adds it, so every way of reading one (expressions, operator configs, taps, the
/// output) brings in exactly what is read. Once the feature plan is built the registrar is detached, and the schema of a
/// plan that serves answers only for what it holds.</para>
/// </remarks>
public sealed class PlanSchema
{
    private readonly Dictionary<string, LogicalColumn> _columns = new(StringComparer.Ordinal);
    private readonly List<LogicalColumn> _columnList = new();
    private readonly List<ColumnType> _slotTypes = new();
    private readonly Dictionary<(string Node, int Logical), int> _nodeSlots = new();
    private readonly Dictionary<int, int> _rowSourceSlots = new();
    private readonly Dictionary<string, ContextField> _context = new(StringComparer.Ordinal);
    private readonly List<ContextField> _contextList = new();
    private readonly Dictionary<string, ParamLeaf> _params = new(StringComparer.Ordinal);
    private readonly List<ParamLeaf> _paramList = new();
    private readonly Dictionary<string, ILookupTable> _tables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unusable = new(StringComparer.Ordinal);
    private IFeatureRegistrar? _features;

    public PlanSchema(EntityKeyType keyType)
    {
        KeyType = keyType;
        var keyColumnType = keyType == EntityKeyType.Int64 ? ColumnType.Int64 : ColumnType.String;
        _slotTypes.Add(keyColumnType);
        AddColumn(new LogicalColumn
        {
            Id = 0,
            Name = "entity_id",
            Type = keyColumnType,
            Kind = ColumnKind.EntityId,
            FixedSlot = TableSchema.EntityIdSlot,
            Pointer = "/entity_key_type",
        });
        AddContext("request.id", ColumnType.String, ContextFieldKind.Builtin);
        AddContext("request.seed", ColumnType.Int64, ContextFieldKind.Builtin);
        AddContext("request.time_ms", ColumnType.Int64, ContextFieldKind.Builtin);
    }

    public const int RequestIdContext = 0;
    public const int RequestSeedContext = 1;
    public const int RequestTimeContext = 2;

    public EntityKeyType KeyType { get; }

    public IReadOnlyList<LogicalColumn> Columns => _columnList;

    public IReadOnlyList<ContextField> ContextFields => _contextList;

    public IReadOnlyList<ParamLeaf> Params => _paramList;

    public int SlotCount => _slotTypes.Count;

    public TableSchema CreateTableSchema() => new(KeyType, _slotTypes);

    /// <summary>The column named <paramref name="name"/> (without <c>col.</c>). While the plan compiles, looking up a
    /// catalog feature registers it.</summary>
    public bool TryGetColumn(string name, [NotNullWhen(true)] out LogicalColumn? column)
    {
        if (!_columns.TryGetValue(name, out column)
            && !(_features is not null && _features.TryRegister("col." + name) && _columns.TryGetValue(name, out column)))
        {
            return false;
        }

        _features?.NoteRead(name, context: false);
        return true;
    }

    /// <summary>The context field named <paramref name="name"/> (without <c>ctx.</c>). While the plan compiles, looking
    /// up a catalog feature registers it.</summary>
    public bool TryGetContext(string name, [NotNullWhen(true)] out ContextField? field)
    {
        if (!_context.TryGetValue(name, out field)
            && !(_features is not null && _features.TryRegister("ctx." + name) && _context.TryGetValue(name, out field)))
        {
            return false;
        }

        _features?.NoteRead(name, context: true);
        return true;
    }

    public bool TryGetParam(string name, [NotNullWhen(true)] out ParamLeaf? leaf) => _params.TryGetValue(name, out leaf);

    /// <summary>
    /// Records a name whose declaration has an error, reported at the declaration (a column, parameter, request input or
    /// feature of an unknown type): it is left out of the schema, and every use of it fails without a second error, so one
    /// mistake is reported once (<see cref="IExpressionScope.IsUnusable"/>).
    /// </summary>
    /// <param name="qualifiedName">The name as expressions write it: <c>col.score.x</c>, <c>ctx.request.user_id</c>,
    /// <c>param.coarse_k</c> (for a map parameter, every leaf <c>param.quotas.*</c> too).</param>
    internal void MarkUnusable(string qualifiedName) => _unusable.Add(qualifiedName);

    /// <summary>True for a name recorded with <see cref="MarkUnusable"/>, a version of such a column (<c>col.x@node</c>),
    /// or a leaf of such a map parameter.</summary>
    public bool IsUnusable(string qualifiedName)
    {
        if (_unusable.Count == 0)
        {
            return false;
        }

        var name = qualifiedName.IndexOf('@', StringComparison.Ordinal) is var at and >= 0 ? qualifiedName[..at] : qualifiedName;
        if (_unusable.Contains(name))
        {
            return true;
        }

        // param.<map>.<key>: the map's declaration decides.
        var dot = name.StartsWith("param.", StringComparison.Ordinal) ? name.LastIndexOf('.') : -1;
        return dot > "param.".Length && _unusable.Contains(name[..dot]);
    }

    /// <summary>
    /// The candidate column names a reference may name (without <c>col.</c>), for suggestions: the schema's, and while
    /// the plan compiles the catalog features not registered yet.
    /// </summary>
    internal List<string> KnownColumnNames()
    {
        var names = new List<string>(_columnList.Count);
        foreach (var c in _columnList)
        {
            names.Add(c.Name);
        }

        _features?.AddUnregisteredNames(names, candidateColumnsOnly: true);
        return names;
    }

    /// <summary>Whether a column of this name is in the schema now, without registering a catalog feature.</summary>
    internal bool HoldsColumn(string name) => _columns.ContainsKey(name);

    /// <summary>Whether a context field of this name is in the schema now, without registering a catalog feature.</summary>
    internal bool HoldsContext(string name) => _context.ContainsKey(name);

    /// <summary>From now on, looking up a catalog feature registers it (<see cref="IFeatureRegistrar"/>).</summary>
    internal void AttachFeatures(IFeatureRegistrar registrar) => _features = registrar;

    /// <summary>Stops registering catalog features: the plan's features are final once its feature plan is built.</summary>
    internal void DetachFeatures() => _features = null;

    /// <summary>
    /// A new schema with this one's request-level names (the built-ins, and the request inputs as declared) and the names
    /// marked unusable: what the catalog's derived features may read besides features and <c>col.entity_id</c>, for
    /// compiling them apart from the plan (<see cref="PlanBuilder"/>).
    /// </summary>
    internal PlanSchema CopyRequestFields()
    {
        var copy = new PlanSchema(KeyType);
        foreach (var field in _contextList)
        {
            if (field.Kind == ContextFieldKind.RequestInput)
            {
                copy.AddRequestInput(field.Name["request.".Length..], field.InputType);
            }
        }

        copy._unusable.UnionWith(_unusable);
        return copy;
    }

    public List<ParamLeaf> MapLeaves(string mapName)
    {
        var leaves = new List<ParamLeaf>();
        foreach (var p in _paramList)
        {
            if (p.MapName == mapName)
            {
                leaves.Add(p);
            }
        }

        return leaves;
    }

    /// <param name="declared">The type as declared; expressions see its promoted form.</param>
    internal ContextField AddRequestInput(string name, ColumnType declared) =>
        AddContext("request." + name, declared.Promoted(), ContextFieldKind.RequestInput, inputType: declared);

    internal ContextField AddContextFeature(string featureName, ColumnType type, int featureIndex) =>
        AddContext(featureName, type, ContextFieldKind.Feature, featureIndex);

    internal LogicalColumn AddFeatureColumn(string featureName, ColumnType type, int featureIndex, string pointer)
    {
        var slot = _slotTypes.Count;
        _slotTypes.Add(type);
        return AddColumn(new LogicalColumn
        {
            Id = _columnList.Count,
            Name = featureName,
            Type = type,
            Kind = ColumnKind.Feature,
            FixedSlot = slot,
            FeatureIndex = featureIndex,
            Pointer = pointer,
        });
    }

    internal LogicalColumn DeclareOutputColumn(string name, ColumnType type, bool flow, bool hasDefault, ScalarValue defaultValue, string pointer, bool isImplicit = false) =>
        AddColumn(new LogicalColumn
        {
            Id = _columnList.Count,
            Name = name,
            Type = type,
            Kind = ColumnKind.Output,
            Flow = flow,
            HasDefault = hasDefault,
            Default = defaultValue,
            Pointer = pointer,
            Implicit = isImplicit,
        });

    internal ParamLeaf AddParamLeaf(string name, ColumnType type, double? min, double? max, IReadOnlyList<ScalarValue>? enumValues, ScalarValue baseline, string pointer, string? mapName = null)
    {
        var leaf = new ParamLeaf
        {
            Id = _paramList.Count,
            Name = name,
            Type = type,
            Min = min,
            Max = max,
            Enum = enumValues,
            Baseline = baseline,
            MapName = mapName,
            Pointer = pointer,
        };
        _params.Add(name, leaf);
        _paramList.Add(leaf);
        return leaf;
    }

    internal void AddLookupTable(ILookupTable table) => _tables[table.Name] = table;

    /// <summary>The slot holding <paramref name="node"/>'s version of an output column (allocated on first use).</summary>
    internal int GetNodeSlot(string node, LogicalColumn column)
    {
        if (!_nodeSlots.TryGetValue((node, column.Id), out var slot))
        {
            slot = _slotTypes.Count;
            _slotTypes.Add(column.Type);
            _nodeSlots[(node, column.Id)] = slot;
        }

        return slot;
    }

    /// <summary>
    /// The shared slot written by row sources: each row source writes the rows it appends (design 4.6), so all of them
    /// share one slot per column.
    /// </summary>
    internal int GetRowSourceSlot(LogicalColumn column)
    {
        if (!_rowSourceSlots.TryGetValue(column.Id, out var slot))
        {
            slot = _slotTypes.Count;
            _slotTypes.Add(column.Type);
            _rowSourceSlots[column.Id] = slot;
        }

        return slot;
    }

    /// <summary>
    /// Creates an expression scope over this schema, for the plan builder: resolving a node's version of a column
    /// (<c>col.x@node</c>) allocates its slot, which changes the schema (so not for a plan that serves).
    /// </summary>
    internal IExpressionScope CreateScope() => new Scope(this);

    private LogicalColumn AddColumn(LogicalColumn column)
    {
        _columns.Add(column.Name, column);
        _columnList.Add(column);
        return column;
    }

    private ContextField AddContext(string name, ColumnType type, ContextFieldKind kind, int featureIndex = -1, ColumnType? inputType = null)
    {
        var field = new ContextField
        {
            Id = _contextList.Count, Name = name, Type = type, InputType = inputType ?? type, Kind = kind, FeatureIndex = featureIndex,
        };
        _context.Add(name, field);
        _contextList.Add(field);
        return field;
    }

    private sealed class Scope(PlanSchema schema) : IExpressionScope
    {
        public IEnumerable<string> KnownNames
        {
            get
            {
                var names = new List<string>(schema._columnList.Count + schema._contextList.Count + schema._paramList.Count);
                foreach (var c in schema._columnList)
                {
                    names.Add("col." + c.Name);
                }

                foreach (var c in schema._contextList)
                {
                    names.Add("ctx." + c.Name);
                }

                foreach (var p in schema._paramList)
                {
                    names.Add("param." + p.Name);
                }

                schema._features?.AddUnregisteredNames(names, candidateColumnsOnly: false);
                return names;
            }
        }

        public bool TryResolve(string name, out Symbol symbol)
        {
            symbol = default;
            if (name.StartsWith("col.", StringComparison.Ordinal))
            {
                var rest = name[4..];
                var at = rest.IndexOf('@', StringComparison.Ordinal);
                if (at >= 0)
                {
                    var column = rest[..at];
                    var node = rest[(at + 1)..];
                    if (node.Length == 0 || !schema._columns.TryGetValue(column, out var versioned) || versioned.Kind != ColumnKind.Output)
                    {
                        return false;
                    }

                    var slot = schema.GetNodeSlot(node, versioned);
                    symbol = new Symbol(SymbolKind.Column, name, versioned.Type.AsNullable(), versioned.Id, slot);
                    return true;
                }

                if (!schema.TryGetColumn(rest, out var col))
                {
                    return false;
                }

                symbol = new Symbol(SymbolKind.Column, name, col.Type, col.Id, col.FixedSlot);
                return true;
            }

            if (name.StartsWith("ctx.", StringComparison.Ordinal) && schema.TryGetContext(name[4..], out var ctx))
            {
                symbol = new Symbol(SymbolKind.Context, name, ctx.Type, ctx.Id);
                return true;
            }

            if (name.StartsWith("param.", StringComparison.Ordinal) && schema._params.TryGetValue(name[6..], out var p))
            {
                symbol = new Symbol(SymbolKind.Param, name, p.Type, p.Id);
                return true;
            }

            return false;
        }

        public bool TryGetLookupTable(string name, out ILookupTable table) => schema._tables.TryGetValue(name, out table!);

        public bool IsUnusable(string name) => schema.IsUnusable(name);
    }
}
