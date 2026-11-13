using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace StrataRec.Core.Types;

/// <summary>Physical storage kind of a column or value (design 4.3).</summary>
public enum StorageKind : byte
{
    Bool = 1,
    Int32,
    Int64,
    Float32,
    Float64,
    /// <summary>Dictionary-encoded low-cardinality string.</summary>
    String,
    /// <summary>Long text (prompts, generated reasons).</summary>
    Text,
    /// <summary>Fixed-length <c>vec&lt;float32,N&gt;</c>.</summary>
    Vector,
    /// <summary>Variable-length <c>list&lt;int64&gt;</c>.</summary>
    ListInt64,
    /// <summary>Variable-length <c>list&lt;string&gt;</c>.</summary>
    ListString,
}

/// <summary>
/// A storage type plus nullability, e.g. <c>float32?</c>, <c>vec&lt;float32,64&gt;</c>, <c>list&lt;int64&gt;</c>.
/// Expression evaluation works on the <see cref="Promoted"/> form (int32→int64, float32→float64).
/// </summary>
public readonly struct ColumnType : IEquatable<ColumnType>
{
    public ColumnType(StorageKind kind, bool nullable = false, int dimension = 0)
    {
        if (kind == StorageKind.Vector && dimension < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        Kind = kind;
        Nullable = nullable;
        Dimension = kind == StorageKind.Vector ? dimension : 0;
    }

    public StorageKind Kind { get; }

    public bool Nullable { get; }

    /// <summary>Vector dimension; 0 means "any dimension" (only used by expression typing).</summary>
    public int Dimension { get; }

    public bool IsDefault => Kind == 0;

    public bool IsNumeric => Kind is StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64;

    public bool IsInteger => Kind is StorageKind.Int32 or StorageKind.Int64;

    public bool IsFloat => Kind is StorageKind.Float32 or StorageKind.Float64;

    public static ColumnType Bool => new(StorageKind.Bool);

    public static ColumnType Int32 => new(StorageKind.Int32);

    public static ColumnType Int64 => new(StorageKind.Int64);

    public static ColumnType Float32 => new(StorageKind.Float32);

    public static ColumnType Float64 => new(StorageKind.Float64);

    public static ColumnType String => new(StorageKind.String);

    public static ColumnType Text => new(StorageKind.Text);

    public static ColumnType ListInt64 => new(StorageKind.ListInt64);

    public static ColumnType ListString => new(StorageKind.ListString);

    public static ColumnType Vector(int dimension) => new(StorageKind.Vector, false, dimension);

    public ColumnType AsNullable() => new(Kind, true, Dimension);

    public ColumnType AsNonNullable() => new(Kind, false, Dimension);

    public ColumnType WithNullable(bool nullable) => new(Kind, nullable, Dimension);

    /// <summary>The expression-level type: int32→int64, float32→float64; other kinds unchanged.</summary>
    public ColumnType Promoted() => Kind switch
    {
        StorageKind.Int32 => new ColumnType(StorageKind.Int64, Nullable),
        StorageKind.Float32 => new ColumnType(StorageKind.Float64, Nullable),
        _ => this,
    };

    /// <summary>Parses the configuration spelling of a type, e.g. <c>"float32?"</c> or <c>"vec&lt;float32,64&gt;"</c>.</summary>
    public static ColumnType Parse(string text)
    {
        if (TryParse(text, out var type, out var error))
        {
            return type;
        }

        throw new FormatException(error);
    }

    public static bool TryParse(string? text, out ColumnType type, [NotNullWhen(false)] out string? error)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Type must not be empty.";
            return false;
        }

        var s = text.Trim();
        var nullable = s.EndsWith('?');
        if (nullable)
        {
            s = s[..^1].TrimEnd();
        }

        switch (s)
        {
            case "bool": type = new(StorageKind.Bool, nullable); break;
            case "int32": type = new(StorageKind.Int32, nullable); break;
            case "int64": type = new(StorageKind.Int64, nullable); break;
            case "float32": type = new(StorageKind.Float32, nullable); break;
            case "float64": type = new(StorageKind.Float64, nullable); break;
            case "string": type = new(StorageKind.String, nullable); break;
            case "text": type = new(StorageKind.Text, nullable); break;
            case "list<int64>": type = new(StorageKind.ListInt64, nullable); break;
            case "list<string>": type = new(StorageKind.ListString, nullable); break;
            default:
                if (s.StartsWith("vec<", StringComparison.Ordinal) && s.EndsWith('>'))
                {
                    var inner = s[4..^1].Split(',');
                    if (inner.Length == 2
                        && inner[0].Trim() == "float32"
                        && int.TryParse(inner[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dim)
                        && dim > 0)
                    {
                        type = new(StorageKind.Vector, nullable, dim);
                        break;
                    }
                }

                error = $"Unknown type '{text}'. Expected one of: bool, int32, int64, float32, float64, string, text, " +
                        "vec<float32,N>, list<int64>, list<string>, optionally followed by '?'.";
                return false;
        }

        error = null;
        return true;
    }

    public override string ToString()
    {
        var core = Kind switch
        {
            StorageKind.Bool => "bool",
            StorageKind.Int32 => "int32",
            StorageKind.Int64 => "int64",
            StorageKind.Float32 => "float32",
            StorageKind.Float64 => "float64",
            StorageKind.String => "string",
            StorageKind.Text => "text",
            StorageKind.Vector => Dimension > 0 ? $"vec<float32,{Dimension}>" : "vec",
            StorageKind.ListInt64 => "list<int64>",
            StorageKind.ListString => "list<string>",
            _ => "<unknown>",
        };
        return Nullable ? core + "?" : core;
    }

    public bool Equals(ColumnType other) => Kind == other.Kind && Nullable == other.Nullable && Dimension == other.Dimension;

    public override bool Equals(object? obj) => obj is ColumnType other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Nullable, Dimension);

    public static bool operator ==(ColumnType left, ColumnType right) => left.Equals(right);

    public static bool operator !=(ColumnType left, ColumnType right) => !left.Equals(right);
}
