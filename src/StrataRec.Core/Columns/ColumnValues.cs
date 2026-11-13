using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Core.Columns;

/// <summary>
/// Row-at-a-time conversion between column buffers and <see cref="ScalarValue"/>. Convenient for cold paths
/// (defaults, request-level values, results, logs, tests); hot loops should use the typed buffers directly.
/// </summary>
public static class ColumnValues
{
    /// <summary>Reads a row as a promoted scalar; missing rows return <see cref="ScalarValue.Missing"/>.</summary>
    public static ScalarValue Get(ColumnBuffer column, int row)
    {
        if (!column.IsValid(row))
        {
            return ScalarValue.Missing;
        }

        return column switch
        {
            ScalarColumn<bool> b => ScalarValue.FromBool(b[row]),
            ScalarColumn<int> i => ScalarValue.FromInt64(i[row]),
            ScalarColumn<long> l => ScalarValue.FromInt64(l[row]),
            ScalarColumn<float> f => ScalarValue.FromFloat64(f[row]),
            ScalarColumn<double> d => ScalarValue.FromFloat64(d[row]),
            DictStringColumn s => ScalarValue.FromString(s.Get(row)),
            TextColumn t => ScalarValue.FromText(t.Get(row)),
            VectorColumn v => ScalarValue.FromVector(v.Row(row).ToArray()),
            ListColumn<long> ll => ScalarValue.FromInt64List(ll.Get(row).ToArray()),
            ListColumn<string> ls => ScalarValue.FromStringList(ls.Get(row).ToArray()),
            _ => throw new NotSupportedException($"Unsupported column {column.GetType().Name}."),
        };
    }

    /// <summary>Writes a scalar into a row, narrowing numerics to the column's storage; missing clears validity.</summary>
    public static void Set(ColumnBuffer column, int row, in ScalarValue value)
    {
        if (value.IsMissing)
        {
            column.SetMissing(row);
            return;
        }

        switch (column)
        {
            case ScalarColumn<bool> b: b.Set(row, value.AsBool()); break;
            case ScalarColumn<int> i: i.Set(row, checked((int)value.AsInt64())); break;
            case ScalarColumn<long> l: l.Set(row, value.AsInt64()); break;
            case ScalarColumn<float> f: f.Set(row, (float)value.AsFloat64()); break;
            case ScalarColumn<double> d: d.Set(row, value.AsFloat64()); break;
            case DictStringColumn s: s.Set(row, value.AsString()); break;
            case TextColumn t: t.Set(row, value.AsString()); break;
            case VectorColumn v: v.Set(row, value.AsVector()); break;
            case ListColumn<long> ll: ll.Set(row, value.AsInt64List()); break;
            case ListColumn<string> ls: ls.Set(row, value.AsStringList()); break;
            default: throw new NotSupportedException($"Unsupported column {column.GetType().Name}.");
        }
    }

    /// <summary>True when the column holds floating-point values that must be checked for NaN/±∞ (design 4.3).</summary>
    public static bool IsFloat(ColumnBuffer column) => column.Type.Kind is StorageKind.Float32 or StorageKind.Float64;

    /// <summary>
    /// Replaces non-finite values on the given rows with <paramref name="defaultValue"/> (or marks them missing when
    /// the default is missing). Returns the number of rows changed (metric <c>column_nonfinite_total</c>).
    /// </summary>
    public static int SanitizeNonFinite(ColumnBuffer column, ReadOnlySpan<int> rows, in ScalarValue defaultValue)
    {
        var changed = 0;
        switch (column)
        {
            case ScalarColumn<double> d:
                foreach (var r in rows)
                {
                    if (d.IsValid(r) && !double.IsFinite(d[r]))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
            case ScalarColumn<float> f:
                foreach (var r in rows)
                {
                    if (f.IsValid(r) && !float.IsFinite(f[r]))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
        }

        return changed;
    }

    private static void Replace(ColumnBuffer column, int row, in ScalarValue defaultValue)
    {
        if (defaultValue.IsMissing)
        {
            column.SetMissing(row);
        }
        else
        {
            Set(column, row, defaultValue);
        }
    }
}
