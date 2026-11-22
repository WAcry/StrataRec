using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Execution;

/// <summary>One value of a result's <see cref="ResultValues"/>: its name, declared type and value (missing when none).</summary>
public readonly record struct ResultValue(string Name, ColumnType Type, ScalarValue Value);

/// <summary>
/// A result's request-level values (<c>output.values</c>), in the output's order: what the request's nodes computed for the
/// caller once, beside the items (a count of the candidates, a decision a node made), each with its declared type.
/// </summary>
/// <remarks>Values are held promoted, as every <see cref="ScalarValue"/> is; <see cref="ResultValue.Type"/> says what each is
/// declared as, as <see cref="ResultColumns.Types"/> does for the items' columns: a value that is nothing but a reference
/// (<c>req.candidates.count</c>, <c>ctx.request.surface</c>) has the type the reference is declared with, a computed one its
/// promoted result type.</remarks>
public sealed class ResultValues
{
    private readonly ResultValue[] _values;

    /// <param name="values">The values in the output's order, each name once.</param>
    /// <exception cref="ArgumentException">A name is given twice.</exception>
    public ResultValues(ResultValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(values[i].Name, values[j].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Value '{values[i].Name}' is given twice.", nameof(values));
                }
            }
        }

        _values = values;
    }

    /// <summary>No values: a pipeline without <c>output.values</c>, or a failed request.</summary>
    public static ResultValues Empty { get; } = new([]);

    public int Count => _values.Length;

    /// <summary>The values in the output's order.</summary>
    public ReadOnlySpan<ResultValue> All => _values;

    public ResultValue this[int index] => _values[index];

    /// <summary>The value named <paramref name="name"/>, as <c>result.Values["recalled"].AsInt64()</c> reads it.</summary>
    /// <exception cref="KeyNotFoundException">The output has no such value.</exception>
    public ScalarValue this[string name] =>
        TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"The output has no value '{name}'.");

    /// <summary>The value named <paramref name="name"/>; false when the output has no such value (a missing value is found).</summary>
    public bool TryGetValue(string name, out ScalarValue value)
    {
        var index = IndexOf(name);
        value = index >= 0 ? _values[index].Value : ScalarValue.Missing;
        return index >= 0;
    }

    /// <summary>The value named <paramref name="name"/> with its declared type; false when the output has no such value.</summary>
    public bool TryGetEntry(string name, out ResultValue entry)
    {
        var index = IndexOf(name);
        entry = index >= 0 ? _values[index] : default;
        return index >= 0;
    }

    private int IndexOf(string name)
    {
        // A handful of values: a scan, and nothing to build per result.
        for (var i = 0; i < _values.Length; i++)
        {
            if (string.Equals(_values[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
