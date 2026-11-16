using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// <remarks>Lists are copied, their elements promoted as a scalar's are: a <c>list&lt;int32&gt;</c> row is widened
    /// into a <c>long[]</c>, a <c>list&lt;float32&gt;</c> row into a <c>double[]</c> that prints as floats.</remarks>
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
            ScalarColumn<float> f => ScalarValue.FromFloat32(f[row]),
            ScalarColumn<double> d => ScalarValue.FromFloat64(d[row]),
            DictStringColumn s => ScalarValue.FromString(s.Get(row)),
            TextColumn t => ScalarValue.FromText(t.Get(row)),
            VectorColumn v => ScalarValue.FromVector(v.Row(row).ToArray()),
            ListColumn list => GetList(list, row),
            _ => throw new NotSupportedException($"Unsupported column {column.GetType().Name}."),
        };
    }

    /// <summary>A list row as a promoted value (see <see cref="Get"/>); the row must be present.</summary>
    public static ScalarValue GetList(ListColumn column, int row) => column switch
    {
        ListColumn<bool> b => ListValue(b.Get(row)),
        ListColumn<int> i => ListValue(i.Get(row)),
        ListColumn<long> l => ListValue(l.Get(row)),
        ListColumn<float> f => ListValue(f.Get(row)),
        ListColumn<double> d => ListValue(d.Get(row)),
        ListColumn<string> s => ListValue(s.Get(row)),
        _ => throw new NotSupportedException($"Unsupported column {column.GetType().Name}."),
    };

    /// <summary>
    /// A list value of elements stored as <typeparamref name="T"/> (<c>bool</c>, <c>int</c>, <c>long</c>, <c>float</c>,
    /// <c>double</c> or <c>string</c>): copied and promoted, as a column row is read (int32 widened, float32 kept printable
    /// as floats). The one conversion from list storage to a value, for every reader of stored lists.
    /// </summary>
    public static ScalarValue ListValue<T>(ReadOnlySpan<T> elements)
    {
        // typeof(T) tests are constants once the method is compiled for T.
        if (typeof(T) == typeof(bool))
        {
            return ScalarValue.FromBoolList(As<T, bool>(elements).ToArray());
        }

        if (typeof(T) == typeof(int))
        {
            return ScalarValue.FromInt64List(Int32Range.ToInt64Array(As<T, int>(elements)));
        }

        if (typeof(T) == typeof(long))
        {
            return ScalarValue.FromInt64List(As<T, long>(elements).ToArray());
        }

        if (typeof(T) == typeof(float))
        {
            return ScalarValue.FromFloat32List(As<T, float>(elements));
        }

        if (typeof(T) == typeof(double))
        {
            return ScalarValue.FromFloat64List(As<T, double>(elements).ToArray());
        }

        if (typeof(T) == typeof(string))
        {
            return ScalarValue.FromStringList(As<T, string>(elements).ToArray());
        }

        throw new NotSupportedException($"Lists do not store {typeof(T).Name} elements.");
    }

    /// <summary>The storage kind of list elements held as <typeparamref name="T"/>; 0 for a type no list stores.</summary>
    public static StorageKind ElementKind<T>() =>
        typeof(T) == typeof(bool) ? StorageKind.Bool
        : typeof(T) == typeof(int) ? StorageKind.Int32
        : typeof(T) == typeof(long) ? StorageKind.Int64
        : typeof(T) == typeof(float) ? StorageKind.Float32
        : typeof(T) == typeof(double) ? StorageKind.Float64
        : typeof(T) == typeof(string) ? StorageKind.String
        : 0;

    /// <summary>The same span seen as <typeparamref name="TTo"/>, which is <typeparamref name="TFrom"/> (callers test it).</summary>
    private static ReadOnlySpan<TTo> As<TFrom, TTo>(ReadOnlySpan<TFrom> span) =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(span)), span.Length);

    /// <summary>Writes a scalar into a row, narrowing numerics to the column's storage; missing clears validity.</summary>
    /// <remarks>For trusted values only (validated defaults, engine-produced values): an int64 that does not fit an
    /// <c>int32</c> or <c>list&lt;int32&gt;</c> column throws <see cref="OverflowException"/> rather than wrapping. Writers of
    /// data apply the out-of-range rule instead (<see cref="TrySet"/>, design 4.3).</remarks>
    public static void Set(ColumnBuffer column, int row, in ScalarValue value)
    {
        if (!TrySet(column, row, value))
        {
            throw new OverflowException(
                $"{value} does not fit the {column.Type} column: int32 storage holds [{int.MinValue}, {int.MaxValue}].");
        }
    }

    /// <summary>
    /// Like <see cref="Set"/>, but returns false and leaves the row untouched when the value does not fit int32
    /// storage: an <c>int32</c> column, or a <c>list&lt;int32&gt;</c> column with any element outside the range. Never wraps.
    /// </summary>
    public static bool TrySet(ColumnBuffer column, int row, in ScalarValue value)
    {
        if (value.IsMissing)
        {
            column.SetMissing(row);
            return true;
        }

        switch (column)
        {
            case ScalarColumn<bool> b: b.Set(row, value.AsBool()); break;
            case ScalarColumn<int> i:
                var wide = value.AsInt64();
                if (!Int32Range.Fits(wide))
                {
                    return false;
                }

                i.Set(row, (int)wide);
                break;
            case ScalarColumn<long> l: l.Set(row, value.AsInt64()); break;
            case ScalarColumn<float> f: f.Set(row, (float)value.AsFloat64()); break;
            case ScalarColumn<double> d: d.Set(row, value.AsFloat64()); break;
            case DictStringColumn s: s.Set(row, value.AsString()); break;
            case TextColumn t: t.Set(row, value.AsString()); break;
            case VectorColumn v: v.Set(row, value.AsVector()); break;
            case ListColumn list: return TrySetList(list, row, value);
            default: throw new NotSupportedException($"Unsupported column {column.GetType().Name}.");
        }

        return true;
    }

    /// <summary>Writes a present list value (promoted elements) into a list row, narrowing to the column's element
    /// storage; false, leaving the row untouched, for an element outside int32 storage.</summary>
    public static bool TrySetList(ListColumn column, int row, in ScalarValue value)
    {
        switch (column)
        {
            case ListColumn<bool> b: b.Set(row, value.AsBoolList()); break;
            case ListColumn<int> i:
                var longs = value.AsInt64List();
                if (!Int32Range.AllFit(longs))
                {
                    return false;
                }

                Int32Range.Narrow(longs, i.SetUninitialized(row, longs.Length));
                break;
            case ListColumn<long> l: l.Set(row, value.AsInt64List()); break;
            case ListColumn<float> f:
                var doubles = value.AsFloat64List();
                NarrowFloats(doubles, f.SetUninitialized(row, doubles.Length));
                break;
            case ListColumn<double> d: d.Set(row, value.AsFloat64List()); break;
            case ListColumn<string> s: s.Set(row, value.AsStringList()); break;
            default: throw new NotSupportedException($"Unsupported column {column.GetType().Name}.");
        }

        return true;
    }

    /// <summary>Copies float64 values into float32 storage, as a float32 column's write does (beyond the range is an
    /// infinity, which the non-finite rule then handles).</summary>
    public static void NarrowFloats(ReadOnlySpan<double> source, Span<float> destination)
    {
        destination = destination[..source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = (float)source[i];
        }
    }

    /// <summary>
    /// True when a present (promoted) value can be stored as <paramref name="type"/>. False only for an int64 outside
    /// the int32 range bound for <c>int32</c> storage, or an int64 list with such an element bound for
    /// <c>list&lt;int32&gt;</c>; missing values and every other kind fit.
    /// </summary>
    public static bool FitsStorage(in ScalarValue value, ColumnType type)
    {
        if (!type.IsInt32Storage)
        {
            return true;
        }

        return value.Kind switch
        {
            StorageKind.Int64 => Int32Range.Fits(value.AsInt64()),
            StorageKind.List when value.ListElement == StorageKind.Int64 => Int32Range.AllFit(value.AsInt64List()),
            _ => true,
        };
    }

    /// <summary>True for a value the non-finite rule replaces (design 4.3): a float that is NaN or ±∞, or a float list
    /// holding one.</summary>
    public static bool IsNonFinite(in ScalarValue value) => value.Kind switch
    {
        StorageKind.Float64 => !double.IsFinite(value.AsFloat64()),
        StorageKind.List when value.ListElement == StorageKind.Float64 => !AllFinite(value.AsFloat64List()),
        _ => false,
    };

    /// <summary>True when the column holds floating-point values that must be checked for NaN/±∞ (design 4.3): a float
    /// column or a list of floats.</summary>
    public static bool IsFloat(ColumnBuffer column) => column.Type.IsFloatStorage;

    /// <summary>
    /// Replaces non-finite values on the given rows with <paramref name="defaultValue"/> (or marks them missing when
    /// the default is missing). Returns the number of rows changed (metric <c>column_nonfinite_total</c>). A list row with
    /// one non-finite element is replaced as a whole, as a list with an element out of int32 range is: a list with an
    /// element taken out is wrong data.
    /// </summary>
    /// <remarks>A row's validity is read only when its value is not finite, which is rare: the common path is one scan of
    /// the values.</remarks>
    public static int SanitizeNonFinite(ColumnBuffer column, ReadOnlySpan<int> rows, in ScalarValue defaultValue)
    {
        var changed = 0;
        switch (column)
        {
            case ScalarColumn<double> d:
                foreach (var r in rows)
                {
                    if (!double.IsFinite(d[r]) && d.IsValid(r))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
            case ScalarColumn<float> f:
                foreach (var r in rows)
                {
                    if (!float.IsFinite(f[r]) && f.IsValid(r))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
            case ListColumn<double> d:
                foreach (var r in rows)
                {
                    if (!AllFinite(d.Get(r)) && d.IsValid(r))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
            case ListColumn<float> f:
                foreach (var r in rows)
                {
                    if (!AllFinite(f.Get(r)) && f.IsValid(r))
                    {
                        Replace(column, r, defaultValue);
                        changed++;
                    }
                }

                break;
        }

        return changed;
    }

    /// <summary>True when every element is finite.</summary>
    public static bool AllFinite(ReadOnlySpan<double> values)
    {
        foreach (var v in values)
        {
            if (!double.IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc cref="AllFinite(ReadOnlySpan{double})"/>
    public static bool AllFinite(ReadOnlySpan<float> values)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v))
            {
                return false;
            }
        }

        return true;
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
