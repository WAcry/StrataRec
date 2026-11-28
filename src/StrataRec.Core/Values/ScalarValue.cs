using System.Globalization;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;

namespace StrataRec.Core.Values;

/// <summary>
/// A single request-level value: a request input, a context feature, a parameter or a constant.
/// <para>
/// Numeric values are stored promoted (int64 / float64), and so are a list's elements: a <c>list&lt;int32&gt;</c> value
/// is a <c>long[]</c>, a <c>list&lt;float32&gt;</c> one a <c>double[]</c>, exactly as an <c>int32</c> value is an
/// int64. The kinds <see cref="StorageKind.Int32"/> and <see cref="StorageKind.Float32"/> therefore never occur here,
/// as a value or as a list's <see cref="ListElement"/>; storage narrowing happens when a value is written into a
/// column, which checks the int32 range. A value with <see cref="IsMissing"/> = true represents a legitimately absent
/// value of a nullable type.
/// </para>
/// <para>
/// A float64 value read from float32 storage (<see cref="FromFloat32"/>) remembers it, only so that its text is the float's
/// own: <c>0.1</c>, not <c>0.10000000149011612</c>, the double that float32 0.1 is. People read results, explanations and
/// replay reports in that text; the value, equality and hash are the double's, so a float32 value equals the same number
/// from float64 storage and comparisons never depend on where a value came from.
/// </para>
/// </summary>
public readonly struct ScalarValue : IEquatable<ScalarValue>
{
    /// <summary>The reference of a float64 value that came from float32 storage; no other value has it.</summary>
    private static readonly object SinglePrecision = new();

    /// <summary>In a list's bits, above its element kind: the elements came from float32 storage (see the type's remarks).</summary>
    private const long SinglePrecisionList = 0x100;

    private readonly long _bits;
    private readonly object? _ref;

    private ScalarValue(StorageKind kind, long bits, object? reference)
    {
        Kind = kind;
        _bits = bits;
        _ref = reference;
    }

    /// <summary>Storage kind; 0 when missing.</summary>
    public StorageKind Kind { get; }

    public bool IsMissing => Kind == 0;

    public static ScalarValue Missing => default;

    public static ScalarValue FromBool(bool value) => new(StorageKind.Bool, value ? 1 : 0, null);

    public static ScalarValue FromInt64(long value) => new(StorageKind.Int64, value, null);

    public static ScalarValue FromFloat64(double value) => new(StorageKind.Float64, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>A float32 value, held promoted like every float, that prints as the float it is (see the type's remarks).</summary>
    public static ScalarValue FromFloat32(float value) => new(StorageKind.Float64, BitConverter.DoubleToInt64Bits(value), SinglePrecision);

    /// <summary>True for a value made by <see cref="FromFloat32"/>: its double is exactly a float32 and prints as one.</summary>
    public bool IsFloat32 => Kind == StorageKind.Float64 && ReferenceEquals(_ref, SinglePrecision);

    public static ScalarValue FromString(string? value) =>
        value is null ? Missing : new(StorageKind.String, 0, value);

    /// <summary>A list value over <paramref name="value"/>, which it keeps without copying: do not change the array afterwards.</summary>
    /// <remarks>A value is immutable, and one value may be shared by every request (a plan's default or parameter, an
    /// expression's constant), so the list accessors are read-only spans: no reader can change what another reads. Copy
    /// one (<c>ToArray()</c>) to sort or edit it. The array is the value's from then on, and a value outlives the request
    /// that made it (in its result, in a cache, in the result of a call that another request takes): never an array
    /// rented from a pool or from the request's memory (<c>RequestMemory.Rent</c>), which goes back and is used again. A value has no fixed length of its own: whether its length is the one a
    /// <c>list&lt;E,N&gt;</c> holds is checked where it is converted or stored (<see cref="TryConvert"/>).</remarks>
    public static ScalarValue FromBoolList(bool[]? value) => List(StorageKind.Bool, value);

    /// <inheritdoc cref="FromBoolList"/>
    public static ScalarValue FromInt64List(long[]? value) => List(StorageKind.Int64, value);

    /// <inheritdoc cref="FromBoolList"/>
    public static ScalarValue FromFloat64List(double[]? value) => List(StorageKind.Float64, value);

    /// <summary>A <c>list&lt;float32&gt;</c> value, held promoted like every float, whose elements print as the floats they
    /// are (see the type's remarks). Copies the elements.</summary>
    public static ScalarValue FromFloat32List(ReadOnlySpan<float> value)
    {
        var doubles = new double[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            doubles[i] = value[i];
        }

        return new(StorageKind.List, (long)StorageKind.Float64 | SinglePrecisionList, doubles);
    }

    /// <inheritdoc cref="FromBoolList"/>
    public static ScalarValue FromStringList(string[]? value) => List(StorageKind.String, value);

    private static ScalarValue List(StorageKind element, Array? value) =>
        value is null ? Missing : new(StorageKind.List, (long)element, value);

    /// <summary>The kind of a list's elements, promoted: bool, int64, float64 or string; 0 for every other value.</summary>
    public StorageKind ListElement => Kind == StorageKind.List ? (StorageKind)(_bits & 0xFF) : 0;

    /// <summary>True for a list made by <see cref="FromFloat32List"/>: its elements are exactly float32s and print as such.</summary>
    public bool IsFloat32List => Kind == StorageKind.List && (_bits & SinglePrecisionList) != 0;

    /// <summary>The number of elements of a list, whatever their kind.</summary>
    public int ListLength => Kind == StorageKind.List ? ((Array)_ref!).Length : throw WrongKind(StorageKind.List);

    public bool AsBool() => Kind == StorageKind.Bool ? _bits != 0 : throw WrongKind(StorageKind.Bool);

    public long AsInt64() => Kind == StorageKind.Int64 ? _bits : throw WrongKind(StorageKind.Int64);

    /// <summary>Returns the numeric value as float64 (int64 values are converted).</summary>
    public double AsFloat64() => Kind switch
    {
        StorageKind.Float64 => BitConverter.Int64BitsToDouble(_bits),
        StorageKind.Int64 => _bits,
        _ => throw WrongKind(StorageKind.Float64),
    };

    /// <summary>
    /// The value of an <c>int32</c> field as the int32 it is, for a reader that knows the declared type (a result column, a
    /// log context field): values are held promoted, and a value of an int32 type always fits.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not an int64 in the int32 range: it is not a value of an
    /// int32 type, and narrowing it would wrap.</exception>
    public int AsInt32() => Kind == StorageKind.Int64 && Int32Range.Fits(_bits)
        ? (int)_bits
        : throw new InvalidOperationException(Kind == StorageKind.Int64
            ? $"{_bits} is outside the int32 range: it is not a value of an int32 type."
            : $"Value of kind {Describe()} cannot be read as Int32.");

    /// <summary>
    /// The value of a <c>float32</c> field as a float, for a reader that knows the declared type: the float nearest the
    /// double (an int64 value is converted, as <see cref="AsFloat64"/> converts it). Exact for every value of a float32
    /// field: one read from float32 storage holds a widened float, and one converted to a float32 type (a request input,
    /// a default, a parameter) was rounded to the nearest float (<see cref="TryConvert"/>). Any other double is rounded as
    /// storing it in a float32 column would round it.
    /// </summary>
    public float AsFloat32() => (float)AsFloat64();

    public string AsString() => Kind == StorageKind.String ? (string)_ref! : throw WrongKind(StorageKind.String);

    /// <summary>The list's elements, read-only (see <see cref="FromBoolList"/>).</summary>
    public ReadOnlySpan<bool> AsBoolList() => ListOf<bool>(StorageKind.Bool);

    /// <inheritdoc cref="AsBoolList"/>
    public ReadOnlySpan<long> AsInt64List() => ListOf<long>(StorageKind.Int64);

    /// <inheritdoc cref="AsBoolList"/>
    public ReadOnlySpan<double> AsFloat64List() => ListOf<double>(StorageKind.Float64);

    /// <inheritdoc cref="AsBoolList"/>
    public ReadOnlySpan<string> AsStringList() => ListOf<string>(StorageKind.String);

    private T[] ListOf<T>(StorageKind element) =>
        ListElement == element ? (T[])_ref! : throw new InvalidOperationException(
            $"Value of kind {Describe()} cannot be read as a list of {element}.");

    /// <summary>Converts to the given type's promoted form; returns false when the value cannot represent it, including an
    /// integer (or list element) outside the int32 range for <c>int32</c> and <c>list&lt;int32&gt;</c> targets, and a finite
    /// float beyond the float32 range for <c>float32</c> and <c>list&lt;float32&gt;</c> ones (<see cref="Float32Range"/>), and
    /// a list of another length than a <c>list&lt;E,N&gt;</c> target's. A list converts element by element, as a scalar of
    /// its element type would.</summary>
    /// <remarks>A number converted to <c>float32</c> (or to <c>list&lt;float32&gt;</c> elements) is rounded to the nearest
    /// float, as storing it in a float32 column rounds it: a value of a float32 field is a float32 value wherever it is
    /// read, so an expression computes with the same number a model input, a log or a result of that field gets
    /// (<c>0.1</c> given for a float32 request input is <c>0.1f</c> everywhere).</remarks>
    public bool TryConvert(ColumnType target, out ScalarValue converted)
    {
        converted = this;
        if (IsMissing)
        {
            return target.Nullable;
        }

        if (target.IsList || Kind == StorageKind.List)
        {
            return target.IsList && Kind == StorageKind.List && (target.FixedLength == 0 || ListLength == target.FixedLength)
                   && TryConvertList(target, out converted);
        }

        var kind = target.Promoted().Kind;
        if (kind == Kind)
        {
            if (target.Kind == StorageKind.Float64 && IsFloat32)
            {
                converted = FromFloat64(AsFloat64());
            }

            switch (target.Kind)
            {
                case StorageKind.Int32:
                    return Int32Range.Fits(_bits);
                case StorageKind.Float32:
                    if (!Float32Range.Fits(AsFloat64()))
                    {
                        return false;
                    }

                    converted = IsFloat32 ? this : FromFloat32((float)AsFloat64());
                    return true;
                default:
                    return true;
            }
        }

        switch (kind)
        {
            case StorageKind.Float64 when Kind == StorageKind.Int64:
                // Through the double, as an integer reaches a float32 column (promoted, then narrowed), and as a replay
                // reads the recorded integer back: a direct long-to-float conversion can round to the other float.
                converted = target.Kind == StorageKind.Float32 ? FromFloat32((float)(double)_bits) : FromFloat64(_bits);
                return true;
            case StorageKind.Int64 when Kind == StorageKind.Float64:
                if (TryInteger(AsFloat64(), target.Kind == StorageKind.Int32, out var integer))
                {
                    converted = FromInt64(integer);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private bool TryConvertList(ColumnType target, out ScalarValue converted)
    {
        converted = this;
        var element = target.Promoted().Element;
        var int32 = target.Element == StorageKind.Int32;
        if (element == ListElement)
        {
            if (target.Element == StorageKind.Float64 && IsFloat32List)
            {
                converted = FromFloat64List((double[])_ref!);
            }

            switch (target.Element)
            {
                case StorageKind.Int32:
                    return Int32Range.AllFit(AsInt64List());
                case StorageKind.String:
                    foreach (var text in AsStringList())
                    {
                        if (text is null)
                        {
                            return false;
                        }
                    }

                    return true;
                case StorageKind.Float32:
                    if (!Float32Range.AllFit(AsFloat64List()))
                    {
                        return false;
                    }

                    if (!IsFloat32List)
                    {
                        var elements = AsFloat64List();
                        var exact = true;
                        foreach (var e in elements)
                        {
                            exact &= ((double)(float)e).Equals(e);
                        }

                        var rounded = exact ? (double[])_ref! : new double[elements.Length];
                        for (var i = 0; !exact && i < elements.Length; i++)
                        {
                            rounded[i] = (float)elements[i];
                        }

                        converted = new(StorageKind.List, (long)StorageKind.Float64 | SinglePrecisionList, rounded);
                    }

                    return true;
                default:
                    return true;
            }
        }

        switch (element)
        {
            case StorageKind.Float64 when ListElement == StorageKind.Int64:
                var longs = AsInt64List();
                var doubles = new double[longs.Length];
                var single = target.Element == StorageKind.Float32;
                for (var i = 0; i < longs.Length; i++)
                {
                    double d = longs[i];
                    doubles[i] = single ? (double)(float)d : d;
                }

                converted = single ? new(StorageKind.List, (long)StorageKind.Float64 | SinglePrecisionList, doubles) : FromFloat64List(doubles);
                return true;
            case StorageKind.Int64 when ListElement == StorageKind.Float64:
                var source = AsFloat64List();
                var integers = new long[source.Length];
                for (var i = 0; i < source.Length; i++)
                {
                    if (!TryInteger(source[i], int32, out integers[i]))
                    {
                        return false;
                    }
                }

                converted = FromInt64List(integers);
                return true;
            default:
                return false;
        }
    }

    private static bool TryInteger(double d, bool int32, out long integer)
    {
        const double TwoPow63 = 9223372036854775808.0;
        integer = 0;
        if (Math.Floor(d) != d || d < -TwoPow63 || d >= TwoPow63 || (int32 && !Int32Range.Fits((long)d)))
        {
            return false;
        }

        integer = (long)d;
        return true;
    }

    public bool Equals(ScalarValue other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            0 => true,
            StorageKind.Bool or StorageKind.Int64 => _bits == other._bits,
            StorageKind.Float64 => AsFloat64().Equals(other.AsFloat64()),
            StorageKind.String => string.Equals((string)_ref!, (string)other._ref!, StringComparison.Ordinal),
            StorageKind.List => ListEquals(other),
            _ => false,
        };
    }

    private bool ListEquals(in ScalarValue other)
    {
        if (ListElement != other.ListElement)
        {
            return false;
        }

        switch (ListElement)
        {
            case StorageKind.Bool:
                return AsBoolList().SequenceEqual(other.AsBoolList());
            case StorageKind.Int64:
                return AsInt64List().SequenceEqual(other.AsInt64List());
            case StorageKind.Float64:
                // double.Equals, as a float64 value compares: NaN equals NaN, so a value equals itself.
                ReadOnlySpan<double> a = AsFloat64List(), b = other.AsFloat64List();
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (var i = 0; i < a.Length; i++)
                {
                    if (!a[i].Equals(b[i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                ReadOnlySpan<string> x = AsStringList(), y = other.AsStringList();
                if (x.Length != y.Length)
                {
                    return false;
                }

                for (var i = 0; i < x.Length; i++)
                {
                    if (!string.Equals(x[i], y[i], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
        }
    }

    public override bool Equals(object? obj) => obj is ScalarValue other && Equals(other);

    public override int GetHashCode()
    {
        switch (Kind)
        {
            case StorageKind.Float64:
                return HashCode.Combine(Kind, AsFloat64().GetHashCode());
            case StorageKind.String:
                return HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode((string)_ref!));
            case StorageKind.List:
                var hl = new HashCode();
                hl.Add(Kind);
                hl.Add(ListElement);
                switch (ListElement)
                {
                    case StorageKind.Bool:
                        foreach (var v in AsBoolList())
                        {
                            hl.Add(v);
                        }

                        break;
                    case StorageKind.Int64:
                        foreach (var v in AsInt64List())
                        {
                            hl.Add(v);
                        }

                        break;
                    case StorageKind.Float64:
                        foreach (var v in AsFloat64List())
                        {
                            hl.Add(v);
                        }

                        break;
                    default:
                        foreach (var v in AsStringList())
                        {
                            hl.Add(v, StringComparer.Ordinal);
                        }

                        break;
                }

                return hl.ToHashCode();
            default:
                return HashCode.Combine(Kind, _bits);
        }
    }

    public static bool operator ==(ScalarValue left, ScalarValue right) => left.Equals(right);

    public static bool operator !=(ScalarValue left, ScalarValue right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        0 => "null",
        StorageKind.Bool => AsBool() ? "true" : "false",
        StorageKind.Int64 => _bits.ToString(CultureInfo.InvariantCulture),
        StorageKind.Float64 when IsFloat32 => ((float)AsFloat64()).ToString("R", CultureInfo.InvariantCulture),
        StorageKind.Float64 => AsFloat64().ToString("R", CultureInfo.InvariantCulture),
        StorageKind.String => "'" + AsString() + "'",
        StorageKind.List => ListText(),
        _ => "?",
    };

    private string ListText()
    {
        var text = new System.Text.StringBuilder("[");
        for (var i = 0; i < ListLength; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            switch (ListElement)
            {
                case StorageKind.Bool:
                    text.Append(AsBoolList()[i] ? "true" : "false");
                    break;
                case StorageKind.Int64:
                    text.Append(AsInt64List()[i].ToString(CultureInfo.InvariantCulture));
                    break;
                case StorageKind.Float64 when IsFloat32List:
                    text.Append(((float)AsFloat64List()[i]).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case StorageKind.Float64:
                    text.Append(AsFloat64List()[i].ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append('\'').Append(AsStringList()[i]).Append('\'');
                    break;
            }
        }

        return text.Append(']').ToString();
    }

    private string Describe() => IsMissing ? "missing" : Kind == StorageKind.List ? $"list of {ListElement}" : Kind.ToString();

    private InvalidOperationException WrongKind(StorageKind expected) =>
        new($"Value of kind {Describe()} cannot be read as {expected}.");
}
