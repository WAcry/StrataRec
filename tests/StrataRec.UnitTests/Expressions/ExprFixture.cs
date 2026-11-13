using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Expressions.Runtime;
using StrataRec.Expressions.Vectorized;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// Builds a scope, a candidate table with bound columns, and request values for expression tests.
/// Columns are read through <see cref="OutputBindings"/> unless declared as a fixed-slot version (<c>@node</c>).
/// </summary>
internal sealed class ExprFixture
{
    private readonly List<(string Name, ColumnType Type, string? VersionOf)> _columns = new();
    private readonly List<(string Name, ColumnType Type, ScalarValue Value)> _context = new();
    private readonly List<(string Name, ColumnType Type, ScalarValue Value)> _params = new();
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private ScalarValue[] _contextValues = Array.Empty<ScalarValue>();
    private ScalarValue[] _paramValues = Array.Empty<ScalarValue>();

    private readonly IBufferPool? _pool;

    /// <param name="capacity">Physical rows of the table.</param>
    /// <param name="pool">Where the table's arrays come from; one full of old data shows rows read before anything wrote them.</param>
    public ExprFixture(int capacity = 64, IBufferPool? pool = null)
    {
        Capacity = capacity;
        _pool = pool;
        Scope = new DictionaryScope();
        Scope.Add(new Symbol(SymbolKind.Column, "col.entity_id", ColumnType.Int64, 0, TableSchema.EntityIdSlot));
    }

    public int Capacity { get; }

    public DictionaryScope Scope { get; }

    public RequestMemory Memory { get; private set; } = null!;

    public CandidateTable Table { get; private set; } = null!;

    public OutputBindings Bindings { get; private set; } = null!;

    public ExpressionCounters Counters { get; } = new();

    public EvalContext Context => new(Table, Bindings, _contextValues, _paramValues, Counters);

    /// <summary>Request-level context without a table.</summary>
    public EvalContext RequestContext => new(null, null, _contextValues, _paramValues, Counters);

    public ExprFixture Col(string name, string type)
    {
        _columns.Add((name, ColumnType.Parse(type), null));
        return this;
    }

    /// <summary>Declares <c>base@node</c> as a fixed-slot version of the column <c>base</c> (its own physical slot).</summary>
    public ExprFixture Version(string name, string type)
    {
        _columns.Add((name, ColumnType.Parse(type), name[..name.IndexOf('@', StringComparison.Ordinal)]));
        return this;
    }

    public ExprFixture Ctx(string name, string type, ScalarValue value = default)
    {
        _context.Add((name, ColumnType.Parse(type), value));
        Scope.Add(new Symbol(SymbolKind.Context, name, ColumnType.Parse(type), _context.Count - 1));
        _contextValues = Values(_context);
        return this;
    }

    public ExprFixture Param(string name, string type, ScalarValue value = default)
    {
        _params.Add((name, ColumnType.Parse(type), value));
        Scope.Add(new Symbol(SymbolKind.Param, name, ColumnType.Parse(type), _params.Count - 1));
        _paramValues = Values(_params);
        return this;
    }

    public ExprFixture Lookup(ILookupTable table)
    {
        Scope.AddTable(table);
        return this;
    }

    public void SetCtx(string name, ScalarValue value) => _contextValues[_context.FindIndex(c => c.Name == name)] = value;

    public void SetParam(string name, ScalarValue value) => _paramValues[_params.FindIndex(c => c.Name == name)] = value;

    /// <summary>Creates the table with <paramref name="rows"/> rows (entity ids 1000, 1001, ...) and binds every column.</summary>
    public ExprFixture Build(int rows)
    {
        var slotTypes = new List<ColumnType> { ColumnType.Int64 };
        foreach (var c in _columns)
        {
            _slots[c.Name] = slotTypes.Count;
            slotTypes.Add(c.Type);
        }

        Memory = new RequestMemory(_pool);
        Table = new CandidateTable(new TableSchema(EntityKeyType.Int64, slotTypes), Capacity, Memory);
        Bindings = new OutputBindings(_columns.Count + 1, Memory);
        var logical = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in _columns)
        {
            if (c.VersionOf is null)
            {
                logical[c.Name] = logical.Count + 1;
            }
        }

        foreach (var c in _columns)
        {
            var slot = _slots[c.Name];
            if (c.VersionOf is null)
            {
                Scope.Add(new Symbol(SymbolKind.Column, c.Name, c.Type, logical[c.Name]));
                Bindings.Commit(logical[c.Name], slot, producerNode: 0);
            }
            else
            {
                Scope.Add(new Symbol(SymbolKind.Column, c.Name, c.Type, logical.GetValueOrDefault(c.VersionOf, 0), FixedSlot: slot));
            }
        }

        var ids = new long[rows];
        for (var i = 0; i < rows; i++)
        {
            ids[i] = 1000 + i;
        }

        Table.AppendRows(ids);
        return this;
    }

    public int SlotOf(string name) => _slots[name];

    public T Buffer<T>(string name)
        where T : ColumnBuffer => (T)Table.GetOrCreateSlot(_slots[name]);

    public ScalarColumn<T> Scalar<T>(string name)
        where T : unmanaged => Buffer<ScalarColumn<T>>(name);

    /// <summary>Fills a scalar column with one value per row (null = missing).</summary>
    public void Fill<T>(string name, params T?[] values)
        where T : unmanaged
    {
        var col = (ScalarColumn<T>)Table.GetOrCreateSlot(_slots[name]);
        for (var r = 0; r < values.Length; r++)
        {
            if (values[r] is { } v)
            {
                col.Set(r, v);
            }
            else
            {
                col.SetMissing(r);
            }
        }
    }

    public void FillStrings(string name, params string?[] values)
    {
        var col = Table.GetOrCreateSlot(_slots[name]);
        for (var r = 0; r < values.Length; r++)
        {
            switch (col)
            {
                case DictStringColumn d when values[r] is { } s: d.Set(r, s); break;
                case TextColumn t when values[r] is { } s: t.Set(r, s); break;
                default: col.SetMissing(r); break;
            }
        }
    }

    public CompiledExpression Compile(string source, CompileOptions? options = null) =>
        ExpressionCompiler.Compile(source, Scope, options ?? CompileOptions.Any);

    public IReadOnlyList<ExpressionDiagnostic> Errors(string source, CompileOptions? options = null)
    {
        Assert.False(ExpressionCompiler.TryCompile(source, Scope, options ?? CompileOptions.Any, out _, out var diagnostics), $"'{source}' compiled unexpectedly");
        return diagnostics;
    }

    /// <summary>Evaluates over every live row into a fresh column of the given type; returns that column.</summary>
    public ColumnBuffer EvaluateAll(CompiledExpression expression, string destinationType, ReadOnlySpan<int> rows = default)
    {
        var destination = ColumnBuffer.Create(ColumnType.Parse(destinationType), Capacity, Memory);
        expression.Evaluate(Context, rows.IsEmpty ? Table.Rows.Order : rows, destination);
        return destination;
    }

    /// <summary>Evaluates a row-level expression and returns the per-row results as scalar values (missing → Missing).</summary>
    public ScalarValue[] Rows(string source, params int[] rows)
    {
        var expression = Compile(source);
        var type = expression.ResultType.AsNullable();
        var destination = ColumnBuffer.Create(type, Capacity, Memory);
        var list = rows.Length == 0 ? Table.Rows.Order.ToArray() : rows;
        expression.Evaluate(Context, list, destination);
        var result = new ScalarValue[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            result[i] = Read(destination, list[i]);
        }

        return result;
    }

    /// <summary>Evaluates an expression for one row through the vectorized backend.</summary>
    public ScalarValue Row(string source, int row) => Rows(source, row)[0];

    /// <summary>Evaluates a request-level expression.</summary>
    public ScalarValue Scalar(string source) => Compile(source).EvaluateScalar(Context);

    /// <summary>Reference-interpreter result for one row (unoptimized IR).</summary>
    public ScalarValue Reference(CompiledExpression expression, int row) =>
        ReferenceInterpreter.Evaluate(((VectorizedExpression)expression).Bound, Context, row);

    public static ScalarValue Read(ColumnBuffer column, int row)
    {
        if (!column.IsValid(row))
        {
            return ScalarValue.Missing;
        }

        return column switch
        {
            ScalarColumn<double> c => ScalarValue.FromFloat64(c[row]),
            ScalarColumn<float> c => ScalarValue.FromFloat64(c[row]),
            ScalarColumn<long> c => ScalarValue.FromInt64(c[row]),
            ScalarColumn<int> c => ScalarValue.FromInt64(c[row]),
            ScalarColumn<bool> c => ScalarValue.FromBool(c[row]),
            DictStringColumn c => ScalarValue.FromString(c.Get(row)),
            TextColumn c => ScalarValue.FromText(c.Get(row)),
            VectorColumn c => ScalarValue.FromVector(c.Row(row).ToArray()),
            ListColumn<long> c => ScalarValue.FromInt64List(c.Get(row).ToArray()),
            ListColumn<int> c => ScalarValue.FromInt64List(c.Get(row).ToArray().Select(v => (long)v).ToArray()),
            ListColumn<string> c => ScalarValue.FromStringList(c.Get(row).ToArray()),
            _ => throw new InvalidOperationException(),
        };
    }

    private static ScalarValue[] Values(List<(string Name, ColumnType Type, ScalarValue Value)> items)
    {
        var values = new ScalarValue[items.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = items[i].Value;
        }

        return values;
    }
}

/// <summary>A dictionary-backed lookup table for tests.</summary>
internal sealed class TestLookupTable(string name, ColumnType keyType, ColumnType valueType, Dictionary<ScalarValue, ScalarValue> entries) : ILookupTable
{
    public string Name { get; } = name;

    public ColumnType KeyType { get; } = keyType;

    public ColumnType ValueType { get; } = valueType;

    public bool TryGet(in ScalarValue key, out ScalarValue value) => entries.TryGetValue(key, out value);
}
