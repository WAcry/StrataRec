using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Execution;

/// <summary>A returned result row.</summary>
public sealed record RankedItem(EntityKey Id, int Rank, IReadOnlyDictionary<string, ScalarValue> Columns);

/// <summary>
/// A result's items column by column: the ids in rank order, each output column's name and declared type, and its values
/// in the same order. The form a result is built in; <c>RecResult.Items</c> are its rows (<see cref="Rows"/>), a dictionary
/// per item, made the first time they are read.
/// </summary>
/// <remarks>
/// <para>Most callers read <c>Items</c>: a result returns far fewer rows than were recalled, and a dictionary per row is the
/// easy way to read them. A caller that returns many rows and processes them further reads these columns instead, and no
/// row dictionary is made: a value per item and column, in one array per column (5,000 items of 8 columns: about 1 MB,
/// where the rows are 3.4 MB). <c>RecResultJson</c> writes from the columns too.</para>
/// <para>Values are held promoted, as every <see cref="ScalarValue"/> is; <see cref="Types"/> says what each column is
/// declared as, so a host whose own types are narrow reads an <c>int32</c> column with <see cref="ScalarValue.AsInt32"/>
/// and a <c>float32</c> one with <see cref="ScalarValue.AsFloat32"/>, exactly. An output column that is nothing but a
/// reference (<c>col.item.clicks</c>, <c>ctx.user.age</c>) has the type the reference is declared with; a computed one its
/// promoted result type (<see cref="Planning.PlanSchema.DeclaredType"/>).</para>
/// </remarks>
public sealed class ResultColumns
{
    private readonly EntityKey[] _ids;
    private readonly string[] _names;
    private readonly ColumnType[] _types;
    private readonly ScalarValue[][] _values;
    private IReadOnlyList<RankedItem>? _rows;
    private object? _rowsLock;

    /// <param name="ids">The items' ids in rank order: item i has rank i + 1.</param>
    /// <param name="names">The output columns' names, in the output's order.</param>
    /// <param name="types">The output columns' declared types, in the same order.</param>
    /// <param name="values">Each column's values (<c>values[c][i]</c> is item i's value of column c), values of the
    /// column's type held promoted; missing is <see cref="ScalarValue.Missing"/>.</param>
    /// <exception cref="ArgumentException">The arrays do not line up.</exception>
    public ResultColumns(EntityKey[] ids, string[] names, ColumnType[] types, ScalarValue[][] values)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(values);
        if (types.Length != names.Length)
        {
            throw new ArgumentException($"{names.Length} names but {types.Length} types.", nameof(types));
        }

        if (values.Length != names.Length)
        {
            throw new ArgumentException($"{names.Length} names but {values.Length} columns of values.", nameof(values));
        }

        foreach (var column in values)
        {
            if (column is null || column.Length != ids.Length)
            {
                throw new ArgumentException($"Every column needs a value for each of the {ids.Length} items.", nameof(values));
            }
        }

        _ids = ids;
        _names = names;
        _types = types;
        _values = values;
    }

    /// <summary>
    /// The columns of <paramref name="items"/>, for a result made by hand (a test double of the engine): the output's
    /// <paramref name="columns"/> in order with their types, as a pipeline declares them, and the items, each with a value
    /// of every column and of no other; item i has rank i + 1.
    /// </summary>
    /// <remarks>The types are given rather than guessed from the values: a column whose values are all missing has no
    /// type to guess, and a guess would make an int32 column int64.</remarks>
    /// <exception cref="ArgumentException">An item lacks a column or has another one, or a value is not one of its
    /// column's type (missing in a non-nullable column, an int64 in a float64 one, an int32 out of range).</exception>
    public static ResultColumns FromItems(IReadOnlyList<(string Name, ColumnType Type)> columns, IReadOnlyList<RankedItem> items)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(items);
        if (columns.Count == 0 && items.Count == 0)
        {
            return Empty;
        }

        var names = new string[columns.Count];
        var types = new ColumnType[columns.Count];
        var values = new ScalarValue[columns.Count][];
        for (var c = 0; c < names.Length; c++)
        {
            (names[c], types[c]) = columns[c];
            values[c] = new ScalarValue[items.Count];
        }

        var ids = new EntityKey[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            ids[i] = items[i].Id;
            if (items[i].Columns.Count != names.Length)
            {
                throw new ArgumentException($"Item {i} has {items[i].Columns.Count} columns, the output {names.Length}.", nameof(items));
            }

            for (var c = 0; c < names.Length; c++)
            {
                if (!items[i].Columns.TryGetValue(names[c], out var value))
                {
                    throw new ArgumentException($"Item {i} has no column '{names[c]}'.", nameof(items));
                }

                // A value of the type converts to itself: of the type's promoted kind, in its range, of its length.
                if (!value.TryConvert(types[c], out var converted) || !converted.Equals(value))
                {
                    throw new ArgumentException($"Item {i}'s '{names[c]}' is {value}, not a value of {types[c]}.", nameof(items));
                }

                values[c][i] = value;
            }
        }

        return new ResultColumns(ids, names, types, values);
    }

    /// <summary>No items (a failed request, or one whose pipeline returned nothing).</summary>
    public static ResultColumns Empty { get; } = new([], [], [], []);

    /// <summary>The number of items.</summary>
    public int Count => _ids.Length;

    /// <summary>The items' ids in rank order: the item at index i has rank i + 1.</summary>
    public ReadOnlySpan<EntityKey> Ids => _ids;

    /// <summary>The output columns' names, in the output's order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The output columns' declared types, in the order of <see cref="Names"/> (see the remarks).</summary>
    public IReadOnlyList<ColumnType> Types => _types;

    /// <summary>The index of the output column <paramref name="name"/> in <see cref="Names"/> and <see cref="Types"/>;
    /// -1 when the output has no such column.</summary>
    public int IndexOf(string name)
    {
        // A handful of columns: a scan, and nothing to build per result.
        for (var c = 0; c < _names.Length; c++)
        {
            if (string.Equals(_names[c], name, StringComparison.Ordinal))
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>The values of the output column at <paramref name="index"/> of <see cref="Names"/>, one per item.</summary>
    public ReadOnlySpan<ScalarValue> Column(int index) => _values[index];

    /// <summary>The values of the output column <paramref name="name"/>, one per item.</summary>
    /// <exception cref="KeyNotFoundException">The output has no such column.</exception>
    public ReadOnlySpan<ScalarValue> this[string name] =>
        TryGetColumn(name, out var values) ? values : throw new KeyNotFoundException($"The output has no column '{name}'.");

    /// <summary>The values of the output column <paramref name="name"/>; false when the output has no such column.</summary>
    public bool TryGetColumn(string name, out ReadOnlySpan<ScalarValue> values)
    {
        var index = IndexOf(name);
        values = index >= 0 ? _values[index] : default;
        return index >= 0;
    }

    /// <summary>
    /// The items as rows, a dictionary of the output columns each: what a result's <c>Items</c> return. Made once, the
    /// first time they are read, from any thread.
    /// </summary>
    public IReadOnlyList<RankedItem> Rows =>
        // Built under a lock the first time only, so threads reading first together do not each build a dictionary per
        // row (a publish race built them all, kept one); later reads take the published rows without it.
        Volatile.Read(ref _rows) ?? LazyInitializer.EnsureInitialized(ref _rows, ref _rowsLock, BuildRows);

    private IReadOnlyList<RankedItem> BuildRows()
    {
        var items = new RankedItem[_ids.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var columns = new Dictionary<string, ScalarValue>(_names.Length, StringComparer.Ordinal);
            for (var c = 0; c < _names.Length; c++)
            {
                columns[_names[c]] = _values[c][i];
            }

            items[i] = new RankedItem(_ids[i], i + 1, columns);
        }

        return items;
    }
}
