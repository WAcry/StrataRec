using System.Globalization;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;

namespace StrataRec.Core.Values;

/// <summary>
/// A single request-level value: a request input, a context feature, a parameter or a constant.
/// <para>
/// Numeric values are stored promoted (int64 / float64), and so are integer lists: a <c>list&lt;int32&gt;</c> value is a
/// <c>long[]</c> of kind <see cref="StorageKind.ListInt64"/>, exactly as an <c>int32</c> value is an int64. The kinds
/// <see cref="StorageKind.Int32"/>, <see cref="StorageKind.Float32"/> and <see cref="StorageKind.ListInt32"/> therefore
/// never occur here; storage narrowing happens when a value is written into a column, which checks the int32 range
/// (design 4.3). A value with <see cref="IsMissing"/> = true represents a legitimately absent value of a nullable type
/// (design 6.7, layer 3).
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

    public static ScalarValue FromText(string? value) =>
        value is null ? Missing : new(StorageKind.Text, 0, value);

    /// <summary>A vector value over <paramref name="value"/>, which it keeps without copying: do not change the array afterwards.</summary>
    /// <remarks>A value is immutable, and one value may be shared by every request (a plan's default or parameter, an
    /// expression's constant), so the list and vector accessors are read-only spans: no reader can change what another
    /// reads. Copy one (<c>ToArray()</c>) to sort or edit it.</remarks>
    public static ScalarValue FromVector(float[]? value) =>
        value is null ? Missing : new(StorageKind.Vector, value.Length, value);

    /// <inheritdoc cref="FromVector"/>
    public static ScalarValue FromInt64List(long[]? value) =>
        value is null ? Missing : new(StorageKind.ListInt64, 0, value);

    /// <inheritdoc cref="FromVector"/>
    public static ScalarValue FromStringList(string[]? value) =>
        value is null ? Missing : new(StorageKind.ListString, 0, value);

    public bool AsBool() => Kind == StorageKind.Bool ? _bits != 0 : throw WrongKind(StorageKind.Bool);

    public long AsInt64() => Kind == StorageKind.Int64 ? _bits : throw WrongKind(StorageKind.Int64);

    /// <summary>Returns the numeric value as float64 (int64 values are converted).</summary>
    public double AsFloat64() => Kind switch
    {
        StorageKind.Float64 => BitConverter.Int64BitsToDouble(_bits),
        StorageKind.Int64 => _bits,
        _ => throw WrongKind(StorageKind.Float64),
    };

    public string AsString() => Kind is StorageKind.String or StorageKind.Text ? (string)_ref! : throw WrongKind(StorageKind.String);

    /// <summary>The vector's components, read-only (see <see cref="FromVector"/>).</summary>
    public ReadOnlySpan<float> AsVector() => Kind == StorageKind.Vector ? (float[])_ref! : throw WrongKind(StorageKind.Vector);

    /// <summary>The list's elements, read-only (see <see cref="FromVector"/>).</summary>
    public ReadOnlySpan<long> AsInt64List() => Kind == StorageKind.ListInt64 ? (long[])_ref! : throw WrongKind(StorageKind.ListInt64);

    /// <inheritdoc cref="AsInt64List"/>
    public ReadOnlySpan<string> AsStringList() => Kind == StorageKind.ListString ? (string[])_ref! : throw WrongKind(StorageKind.ListString);

    /// <summary>Converts to the given type's promoted form; returns false when the value cannot represent it, including an
    /// integer (or list element) outside the int32 range for <c>int32</c> and <c>list&lt;int32&gt;</c> targets.</summary>
    public bool TryConvert(ColumnType target, out ScalarValue converted)
    {
        converted = this;
        if (IsMissing)
        {
            return target.Nullable;
        }

        var kind = target.Promoted().Kind;
        if (kind == Kind)
        {
            if (target.Kind == StorageKind.Float64 && IsFloat32)
            {
                converted = FromFloat64(AsFloat64()); // a float64 value prints as the double it is
            }

            return target.Kind switch
            {
                StorageKind.Vector => target.Dimension == 0 || AsVector().Length == target.Dimension,
                StorageKind.Int32 => Int32Range.Fits(_bits),
                StorageKind.ListInt32 => Int32Range.AllFit(AsInt64List()),
                _ => true,
            };
        }

        switch (kind)
        {
            case StorageKind.Float64 when Kind == StorageKind.Int64:
                converted = FromFloat64(_bits);
                return true;
            case StorageKind.Int64 when Kind == StorageKind.Float64:
                var d = AsFloat64();
                const double TwoPow63 = 9223372036854775808.0;
                if (Math.Floor(d) == d && d >= -TwoPow63 && d < TwoPow63 && (target.Kind != StorageKind.Int32 || Int32Range.Fits((long)d)))
                {
                    converted = FromInt64((long)d);
                    return true;
                }

                return false;
            case StorageKind.String when Kind == StorageKind.Text:
            case StorageKind.Text when Kind == StorageKind.String:
                converted = new ScalarValue(kind, 0, _ref);
                return true;
            default:
                return false;
        }
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
            StorageKind.String or StorageKind.Text => string.Equals((string)_ref!, (string)other._ref!, StringComparison.Ordinal),
            StorageKind.Vector => AsVector().SequenceEqual(other.AsVector()),
            StorageKind.ListInt64 => AsInt64List().SequenceEqual(other.AsInt64List()),
            StorageKind.ListString => AsStringList().SequenceEqual(other.AsStringList()),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is ScalarValue other && Equals(other);

    public override int GetHashCode()
    {
        switch (Kind)
        {
            case StorageKind.Float64:
                return HashCode.Combine(Kind, AsFloat64().GetHashCode());
            case StorageKind.String or StorageKind.Text:
                return HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode((string)_ref!));
            case StorageKind.Vector:
                var h = new HashCode();
                h.Add(Kind);
                foreach (var f in AsVector())
                {
                    h.Add(f);
                }

                return h.ToHashCode();
            case StorageKind.ListInt64:
                var hl = new HashCode();
                hl.Add(Kind);
                foreach (var v in AsInt64List())
                {
                    hl.Add(v);
                }

                return hl.ToHashCode();
            case StorageKind.ListString:
                var hs = new HashCode();
                hs.Add(Kind);
                foreach (var v in AsStringList())
                {
                    hs.Add(v, StringComparer.Ordinal);
                }

                return hs.ToHashCode();
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
        StorageKind.String or StorageKind.Text => "'" + AsString() + "'",
        StorageKind.Vector => "vec[" + AsVector().Length.ToString(CultureInfo.InvariantCulture) + "]",
        StorageKind.ListInt64 => "[" + string.Join(", ", (long[])_ref!) + "]",
        StorageKind.ListString => "['" + string.Join("', '", (string[])_ref!) + "']",
        _ => "?",
    };

    private InvalidOperationException WrongKind(StorageKind expected) =>
        new($"Value of kind {(IsMissing ? "missing" : Kind.ToString())} cannot be read as {expected}.");
}
