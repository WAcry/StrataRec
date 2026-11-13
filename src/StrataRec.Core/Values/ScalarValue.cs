using System.Globalization;
using StrataRec.Core.Types;

namespace StrataRec.Core.Values;

/// <summary>
/// A single request-level value: a request input, a context feature, a parameter or a constant.
/// <para>
/// Numeric values are stored promoted (int64 / float64). A value with <see cref="IsMissing"/> = true represents
/// a legitimately absent value of a nullable type (design 6.7, layer 3).
/// </para>
/// </summary>
public readonly struct ScalarValue : IEquatable<ScalarValue>
{
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

    public static ScalarValue FromString(string? value) =>
        value is null ? Missing : new(StorageKind.String, 0, value);

    public static ScalarValue FromText(string? value) =>
        value is null ? Missing : new(StorageKind.Text, 0, value);

    public static ScalarValue FromVector(float[]? value) =>
        value is null ? Missing : new(StorageKind.Vector, value.Length, value);

    public static ScalarValue FromInt64List(long[]? value) =>
        value is null ? Missing : new(StorageKind.ListInt64, 0, value);

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

    public float[] AsVector() => Kind == StorageKind.Vector ? (float[])_ref! : throw WrongKind(StorageKind.Vector);

    public long[] AsInt64List() => Kind == StorageKind.ListInt64 ? (long[])_ref! : throw WrongKind(StorageKind.ListInt64);

    public string[] AsStringList() => Kind == StorageKind.ListString ? (string[])_ref! : throw WrongKind(StorageKind.ListString);

    /// <summary>Converts to the given (promoted) type; returns false when the value cannot represent it.</summary>
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
            return kind != StorageKind.Vector || target.Dimension == 0 || AsVector().Length == target.Dimension;
        }

        switch (kind)
        {
            case StorageKind.Float64 when Kind == StorageKind.Int64:
                converted = FromFloat64(_bits);
                return true;
            case StorageKind.Int64 when Kind == StorageKind.Float64:
                var d = AsFloat64();
                const double TwoPow63 = 9223372036854775808.0;
                if (Math.Floor(d) == d && d >= -TwoPow63 && d < TwoPow63)
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
            StorageKind.Vector => AsVector().AsSpan().SequenceEqual(other.AsVector()),
            StorageKind.ListInt64 => AsInt64List().AsSpan().SequenceEqual(other.AsInt64List()),
            StorageKind.ListString => AsStringList().AsSpan().SequenceEqual(other.AsStringList()),
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
        StorageKind.Float64 => AsFloat64().ToString("R", CultureInfo.InvariantCulture),
        StorageKind.String or StorageKind.Text => "'" + AsString() + "'",
        StorageKind.Vector => "vec[" + AsVector().Length.ToString(CultureInfo.InvariantCulture) + "]",
        StorageKind.ListInt64 => "[" + string.Join(", ", AsInt64List()) + "]",
        StorageKind.ListString => "[" + string.Join(", ", AsStringList().Select(s => "'" + s + "'")) + "]",
        _ => "?",
    };

    private InvalidOperationException WrongKind(StorageKind expected) =>
        new($"Value of kind {(IsMissing ? "missing" : Kind.ToString())} cannot be read as {expected}.");
}
