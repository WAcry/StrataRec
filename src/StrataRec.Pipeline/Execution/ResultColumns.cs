using StrataRec.Core.Keys;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Execution;

/// <summary>A returned result row.</summary>
public sealed record RankedItem(EntityKey Id, int Rank, IReadOnlyDictionary<string, ScalarValue> Columns);

/// <summary>
/// A result's items column by column: the ids in rank order, and each output column's values in the same order. The
/// form a result is built in; <c>RecResult.Items</c> are its rows (<see cref="Rows"/>), a dictionary per item, made the
/// first time they are read.
/// </summary>
/// <remarks>
/// Most callers read <c>Items</c>: a result returns far fewer rows than were recalled, and a dictionary per row is the
/// easy way to read them. A caller that returns many rows and processes them further reads these columns instead, and no
/// row dictionary is made: a value per item and column, in one array per column (5,000 items of 8 columns: about 1 MB,
/// where the rows are 3.4 MB). <c>RecResultJson</c> writes from the columns too.
/// </remarks>
public sealed class ResultColumns
{
    private readonly EntityKey[] _ids;
    private readonly string[] _names;
    private readonly ScalarValue[][] _values;
    private IReadOnlyList<RankedItem>? _rows;
    private object? _rowsLock;

    /// <param name="ids">The items' ids in rank order: item i has rank i + 1.</param>
    /// <param name="names">The output columns' names, in the output's order.</param>
    /// <param name="values">Each column's values (<c>values[c][i]</c> is item i's value of column c); missing is
    /// <see cref="ScalarValue.Missing"/>.</param>
    /// <exception cref="ArgumentException">The arrays do not line up.</exception>
    public ResultColumns(EntityKey[] ids, string[] names, ScalarValue[][] values)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(values);
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
        _values = values;
    }

    /// <summary>
    /// The columns of <paramref name="items"/>, for a result made by hand (a test double of the engine): each item names
    /// the same output columns; the first item's order is the columns' order, and item i has rank i + 1.
    /// </summary>
    /// <exception cref="ArgumentException">An item lacks a column the first one has, or has one it lacks.</exception>
    public static ResultColumns FromItems(IReadOnlyList<RankedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Empty;
        }

        var names = new List<string>(items[0].Columns.Keys).ToArray();
        var ids = new EntityKey[items.Count];
        var values = new ScalarValue[names.Length][];
        for (var c = 0; c < names.Length; c++)
        {
            values[c] = new ScalarValue[items.Count];
        }

        for (var i = 0; i < items.Count; i++)
        {
            ids[i] = items[i].Id;
            if (items[i].Columns.Count != names.Length)
            {
                throw new ArgumentException($"Item {i} has {items[i].Columns.Count} columns, the first one {names.Length}.", nameof(items));
            }

            for (var c = 0; c < names.Length; c++)
            {
                values[c][i] = items[i].Columns.TryGetValue(names[c], out var value)
                    ? value
                    : throw new ArgumentException($"Item {i} has no column '{names[c]}', which the first one has.", nameof(items));
            }
        }

        return new ResultColumns(ids, names, values);
    }

    /// <summary>No items (a failed request, or one whose pipeline returned nothing).</summary>
    public static ResultColumns Empty { get; } = new([], [], []);

    /// <summary>The number of items.</summary>
    public int Count => _ids.Length;

    /// <summary>The items' ids in rank order: the item at index i has rank i + 1.</summary>
    public ReadOnlySpan<EntityKey> Ids => _ids;

    /// <summary>The output columns' names, in the output's order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The values of the output column at <paramref name="index"/> of <see cref="Names"/>, one per item.</summary>
    public ReadOnlySpan<ScalarValue> Column(int index) => _values[index];

    /// <summary>The values of the output column <paramref name="name"/>, one per item.</summary>
    /// <exception cref="KeyNotFoundException">The output has no such column.</exception>
    public ReadOnlySpan<ScalarValue> this[string name] =>
        TryGetColumn(name, out var values) ? values : throw new KeyNotFoundException($"The output has no column '{name}'.");

    /// <summary>The values of the output column <paramref name="name"/>; false when the output has no such column.</summary>
    public bool TryGetColumn(string name, out ReadOnlySpan<ScalarValue> values)
    {
        // A handful of columns: a scan, and nothing to build per result.
        for (var c = 0; c < _names.Length; c++)
        {
            if (string.Equals(_names[c], name, StringComparison.Ordinal))
            {
                values = _values[c];
                return true;
            }
        }

        values = default;
        return false;
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
