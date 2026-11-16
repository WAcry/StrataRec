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

    /// <summary>
    /// Variable-length <c>list&lt;E&gt;</c> of one element kind (<see cref="ColumnType.Element"/>): <c>bool</c>,
    /// <c>int32</c>, <c>int64</c>, <c>float32</c>, <c>float64</c> or <c>string</c>. A constructor rather than a kind per
    /// element type, so every layer handles lists once and an element type works everywhere a scalar of it does.
    /// </summary>
    List,
}

/// <summary>
/// A storage type plus nullability, e.g. <c>float32?</c>, <c>vec&lt;float32,64&gt;</c>, <c>list&lt;float32&gt;</c>.
/// Expression evaluation works on the <see cref="Promoted"/> form (int32→int64, float32→float64, and the same for a
/// list's elements).
/// </summary>
/// <remarks>A list is never missing element by element: the list as a whole is present or missing (<c>list&lt;E&gt;?</c>),
/// and every element it holds is a value. A sequence with gaps is a list of values plus a list of flags, or a default
/// element written by whoever builds it.</remarks>
public readonly struct ColumnType : IEquatable<ColumnType>
{
    /// <param name="kind">The storage kind.</param>
    /// <param name="nullable">Whether a row may be missing.</param>
    /// <param name="dimension">A vector's dimension; 0 for "any dimension" (expression typing) and for other kinds.</param>
    /// <param name="element">A list's element kind (<see cref="IsListElement"/>); 0 for other kinds.</param>
    public ColumnType(StorageKind kind, bool nullable = false, int dimension = 0, StorageKind element = 0)
    {
        if (kind == StorageKind.Vector && dimension < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        if (kind == StorageKind.List ? !IsListElement(element) : element != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(element), element,
                kind == StorageKind.List ? "A list's elements are bool, int32, int64, float32, float64 or string." : "Only a list has an element kind.");
        }

        Kind = kind;
        Nullable = nullable;
        Dimension = kind == StorageKind.Vector ? dimension : 0;
        Element = element;
    }

    // The one-byte members are declared together, so the struct is 8 bytes rather than 12: it is passed by value
    // everywhere types are compared and checked.
    public StorageKind Kind { get; }

    public bool Nullable { get; }

    /// <summary>The kind of a list's elements; 0 for every other kind.</summary>
    public StorageKind Element { get; }

    /// <summary>Vector dimension; 0 means "any dimension" (only used by expression typing).</summary>
    public int Dimension { get; }

    public bool IsDefault => Kind == 0;

    public bool IsNumeric => Kind is StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64;

    public bool IsInteger => Kind is StorageKind.Int32 or StorageKind.Int64;

    public bool IsFloat => Kind is StorageKind.Float32 or StorageKind.Float64;

    public bool IsList => Kind == StorageKind.List;

    /// <summary>The type of one element of a list (never nullable); throws for other kinds.</summary>
    public ColumnType ElementType => IsList ? new ColumnType(Element) : throw new InvalidOperationException($"{this} is not a list.");

    /// <summary>The kind values are stored as: a list's element kind, otherwise the kind itself. Rules about how a
    /// value is stored (int32 narrowing, float checks) read this, so they hold for a list's elements as for a scalar.</summary>
    public StorageKind ValueKind => IsList ? Element : Kind;

    /// <summary>True for storage that holds int32 values (<c>int32</c>, <c>list&lt;int32&gt;</c>): writing an int64 into it
    /// narrows, and a value outside the int32 range cannot be stored (design 4.3).</summary>
    public bool IsInt32Storage => ValueKind == StorageKind.Int32;

    /// <summary>True for storage that holds floating-point values (<c>float32</c>, <c>float64</c> and lists of them),
    /// whose non-finite values a writer replaces (design 4.3).</summary>
    public bool IsFloatStorage => ValueKind is StorageKind.Float32 or StorageKind.Float64;

    /// <summary>The kinds a list may hold: the fixed-size scalars and <c>string</c>. Not <c>text</c>: a list's strings are
    /// never dictionary-encoded in a request, so the scalar string/text split has nothing to choose between; not vectors
    /// or lists, which would make every layer handle nested variable-length data.</summary>
    public static bool IsListElement(StorageKind kind) =>
        kind is StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64 or StorageKind.String;

    public static ColumnType Bool => new(StorageKind.Bool);

    public static ColumnType Int32 => new(StorageKind.Int32);

    public static ColumnType Int64 => new(StorageKind.Int64);

    public static ColumnType Float32 => new(StorageKind.Float32);

    public static ColumnType Float64 => new(StorageKind.Float64);

    public static ColumnType String => new(StorageKind.String);

    public static ColumnType Text => new(StorageKind.Text);

    public static ColumnType Vector(int dimension) => new(StorageKind.Vector, false, dimension);

    /// <summary><c>list&lt;E&gt;</c> of <paramref name="element"/> (<see cref="IsListElement"/>).</summary>
    public static ColumnType List(StorageKind element, bool nullable = false) => new(StorageKind.List, nullable, 0, element);

    public ColumnType AsNullable() => WithNullable(true);

    public ColumnType AsNonNullable() => WithNullable(false);

    public ColumnType WithNullable(bool nullable) => new(Kind, nullable, Dimension, Element);

    /// <summary>The expression-level type: int32→int64 and float32→float64, for a scalar or a list's elements; other kinds
    /// unchanged.</summary>
    public ColumnType Promoted() => Kind switch
    {
        StorageKind.Int32 => new ColumnType(StorageKind.Int64, Nullable),
        StorageKind.Float32 => new ColumnType(StorageKind.Float64, Nullable),
        StorageKind.List => List(Promote(Element), Nullable),
        _ => this,
    };

    private static StorageKind Promote(StorageKind kind) => kind switch
    {
        StorageKind.Int32 => StorageKind.Int64,
        StorageKind.Float32 => StorageKind.Float64,
        _ => kind,
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

        if (TryParseScalar(s, out var kind))
        {
            type = new(kind, nullable);
            error = null;
            return true;
        }

        if (s.StartsWith("list<", StringComparison.Ordinal) && s.EndsWith('>'))
        {
            var inner = s[5..^1].Trim();
            if (TryParseScalar(inner, out var element) && IsListElement(element))
            {
                type = List(element, nullable);
                error = null;
                return true;
            }

            error = $"Unknown type '{text}': " + ListElementError(inner);
            return false;
        }

        if (s.StartsWith("vec<", StringComparison.Ordinal) && s.EndsWith('>'))
        {
            var inner = s[4..^1].Split(',');
            if (inner.Length == 2
                && inner[0].Trim() == "float32"
                && int.TryParse(inner[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dim)
                && dim > 0)
            {
                type = new(StorageKind.Vector, nullable, dim);
                error = null;
                return true;
            }
        }

        error = $"Unknown type '{text}'. Expected one of: bool, int32, int64, float32, float64, string, text, vec<float32,N>, " +
                "list<E> (E: bool, int32, int64, float32, float64 or string), optionally followed by '?'.";
        return false;
    }

    /// <summary>Why <paramref name="element"/> is not a list's element type, and what to write instead.</summary>
    private static string ListElementError(string element)
    {
        if (element.EndsWith('?'))
        {
            return "a list's elements are never missing; a list that may be missing is written list<E>?, e.g. "
                   + $"'list<{element[..^1].TrimEnd()}>?'.";
        }

        return element switch
        {
            "text" => "a list's elements are plain strings of any length, never dictionary-encoded in a request: write 'list<string>'.",
            _ when element.StartsWith("list<", StringComparison.Ordinal) || element.StartsWith("vec<", StringComparison.Ordinal) =>
                "a list's elements are scalars (bool, int32, int64, float32, float64 or string); lists do not nest.",
            _ => "a list's elements are bool, int32, int64, float32, float64 or string.",
        };
    }

    private static bool TryParseScalar(string s, out StorageKind kind)
    {
        kind = s switch
        {
            "bool" => StorageKind.Bool,
            "int32" => StorageKind.Int32,
            "int64" => StorageKind.Int64,
            "float32" => StorageKind.Float32,
            "float64" => StorageKind.Float64,
            "string" => StorageKind.String,
            "text" => StorageKind.Text,
            _ => 0,
        };
        return kind != 0;
    }

    private static string ScalarName(StorageKind kind) => kind switch
    {
        StorageKind.Bool => "bool",
        StorageKind.Int32 => "int32",
        StorageKind.Int64 => "int64",
        StorageKind.Float32 => "float32",
        StorageKind.Float64 => "float64",
        StorageKind.String => "string",
        StorageKind.Text => "text",
        _ => "<unknown>",
    };

    public override string ToString()
    {
        var core = Kind switch
        {
            StorageKind.Vector => Dimension > 0 ? $"vec<float32,{Dimension}>" : "vec",
            StorageKind.List => "list<" + ScalarName(Element) + ">",
            _ => ScalarName(Kind),
        };
        return Nullable ? core + "?" : core;
    }

    public bool Equals(ColumnType other) =>
        Kind == other.Kind && Nullable == other.Nullable && Dimension == other.Dimension && Element == other.Element;

    public override bool Equals(object? obj) => obj is ColumnType other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Nullable, Dimension, Element);

    public static bool operator ==(ColumnType left, ColumnType right) => left.Equals(right);

    public static bool operator !=(ColumnType left, ColumnType right) => !left.Equals(right);
}
