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

    /// <summary>
    /// <c>list&lt;E&gt;</c> of one element kind (<see cref="ColumnType.Element"/>): <c>bool</c>, <c>int32</c>, <c>int64</c>,
    /// <c>float32</c>, <c>float64</c> or <c>string</c>, of any length or of a fixed one (<c>list&lt;E,N&gt;</c>,
    /// <see cref="ColumnType.FixedLength"/>). A constructor rather than a kind per element type, so every layer handles lists
    /// once and an element type works everywhere a scalar of it does. A vector (an embedding) is a fixed-length float list:
    /// its rows are contiguous spans like every list's, so the same SIMD kernels apply, and no layer needs a path of its own.
    /// </summary>
    List,
}

/// <summary>
/// A storage type plus nullability, e.g. <c>float32?</c>, <c>list&lt;float32&gt;</c>, <c>list&lt;float32,64&gt;</c>.
/// Expression evaluation works on the <see cref="Promoted"/> form (int32→int64, float32→float64, and the same for a
/// list's elements).
/// </summary>
/// <remarks>
/// <para>A list is never missing element by element: the list as a whole is present or missing (<c>list&lt;E&gt;?</c>),
/// and every element it holds is a value. A sequence with gaps is a list of values plus a list of flags, or a default
/// element written by whoever builds it.</para>
/// <para>A list may declare its length (<c>list&lt;float32,64&gt;</c>, <see cref="FixedLength"/>): every value of the type
/// then holds exactly that many elements, which is what a model input's tensor shape, <c>dot</c> between two embeddings and
/// an MMR similarity need to know before a request runs. The storage is the same as a list of any length's; writers check
/// the length instead, and a value of another length is replaced as a whole, like a list with an element outside int32
/// storage (design 4.3).</para>
/// </remarks>
public readonly struct ColumnType : IEquatable<ColumnType>
{
    /// <param name="kind">The storage kind.</param>
    /// <param name="nullable">Whether a row may be missing.</param>
    /// <param name="element">A list's element kind (<see cref="IsListElement"/>); 0 for other kinds.</param>
    /// <param name="fixedLength">A list's length when the type fixes it (at least 1); 0 for a list of any length and for other kinds.</param>
    public ColumnType(StorageKind kind, bool nullable = false, StorageKind element = 0, int fixedLength = 0)
    {
        if (kind == StorageKind.List ? !IsListElement(element) : element != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(element), element,
                kind == StorageKind.List ? "A list's elements are bool, int32, int64, float32, float64 or string." : "Only a list has an element kind.");
        }

        if (fixedLength < 0 || (fixedLength != 0 && kind != StorageKind.List))
        {
            throw new ArgumentOutOfRangeException(nameof(fixedLength), fixedLength,
                kind == StorageKind.List ? "A list's fixed length is at least 1 (0 for any length)." : "Only a list has a length.");
        }

        Kind = kind;
        Nullable = nullable;
        Element = element;
        FixedLength = fixedLength;
    }

    // The one-byte members are declared together, so the struct is 8 bytes rather than 12: it is passed by value
    // everywhere types are compared and checked.
    public StorageKind Kind { get; }

    public bool Nullable { get; }

    /// <summary>The kind of a list's elements; 0 for every other kind.</summary>
    public StorageKind Element { get; }

    /// <summary>The number of elements every value of a <c>list&lt;E,N&gt;</c> holds (N); 0 for a list of any length and
    /// for every other kind.</summary>
    public int FixedLength { get; }

    public bool IsDefault => Kind == 0;

    public bool IsNumeric => Kind is StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64;

    public bool IsInteger => Kind is StorageKind.Int32 or StorageKind.Int64;

    public bool IsFloat => Kind is StorageKind.Float32 or StorageKind.Float64;

    public bool IsList => Kind == StorageKind.List;

    /// <summary>True for a list whose type fixes its length (<c>list&lt;E,N&gt;</c>).</summary>
    public bool IsFixedLength => FixedLength > 0;

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
    /// never dictionary-encoded in a request, so the scalar string/text split has nothing to choose between; not lists,
    /// which would make every layer handle nested variable-length data.</summary>
    public static bool IsListElement(StorageKind kind) =>
        kind is StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64 or StorageKind.String;

    public static ColumnType Bool => new(StorageKind.Bool);

    public static ColumnType Int32 => new(StorageKind.Int32);

    public static ColumnType Int64 => new(StorageKind.Int64);

    public static ColumnType Float32 => new(StorageKind.Float32);

    public static ColumnType Float64 => new(StorageKind.Float64);

    public static ColumnType String => new(StorageKind.String);

    public static ColumnType Text => new(StorageKind.Text);

    /// <summary><c>list&lt;E&gt;</c> of <paramref name="element"/> (<see cref="IsListElement"/>), or <c>list&lt;E,N&gt;</c>
    /// with a <paramref name="fixedLength"/>.</summary>
    public static ColumnType List(StorageKind element, bool nullable = false, int fixedLength = 0) => new(StorageKind.List, nullable, element, fixedLength);

    public ColumnType AsNullable() => WithNullable(true);

    public ColumnType AsNonNullable() => WithNullable(false);

    public ColumnType WithNullable(bool nullable) => new(Kind, nullable, Element, FixedLength);

    /// <summary>The expression-level type: int32→int64 and float32→float64, for a scalar or a list's elements; other kinds
    /// unchanged, a list's length included.</summary>
    public ColumnType Promoted() => Kind switch
    {
        StorageKind.Int32 => new ColumnType(StorageKind.Int64, Nullable),
        StorageKind.Float32 => new ColumnType(StorageKind.Float64, Nullable),
        StorageKind.List => List(Promote(Element), Nullable, FixedLength),
        _ => this,
    };

    private static StorageKind Promote(StorageKind kind) => kind switch
    {
        StorageKind.Int32 => StorageKind.Int64,
        StorageKind.Float32 => StorageKind.Float64,
        _ => kind,
    };

    /// <summary>Parses the configuration spelling of a type, e.g. <c>"float32?"</c> or <c>"list&lt;float32,64&gt;"</c>.</summary>
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
            var inner = s[5..^1];
            var comma = inner.Contains('<', StringComparison.Ordinal) ? -1 : inner.LastIndexOf(',');
            var elementText = (comma < 0 ? inner : inner[..comma]).Trim();
            var length = 0;
            if (comma >= 0)
            {
                var lengthText = inner[(comma + 1)..].Trim();
                if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 1)
                {
                    error = $"Unknown type '{text}': the length N of list<E,N> is a whole number of at least 1, got '{lengthText}'; "
                            + $"a list of any length is written list<{elementText}>.";
                    return false;
                }
            }

            if (TryParseScalar(elementText, out var element) && IsListElement(element))
            {
                type = List(element, nullable, length);
                error = null;
                return true;
            }

            error = $"Unknown type '{text}': " + ListElementError(elementText);
            return false;
        }

        if (VectorAsList(s) is { } list)
        {
            error = $"Unknown type '{text}': a vector is a list of fixed length, written list<float32,N>: '{list}{(nullable ? "?" : string.Empty)}'.";
            return false;
        }

        error = $"Unknown type '{text}'. Expected one of: bool, int32, int64, float32, float64, string, text, " +
                "list<E> or list<E,N> (E: bool, int32, int64, float32, float64 or string; N: its fixed length), optionally followed by '?'.";
        return false;
    }

    /// <summary>
    /// The list spelling of a vector type written in another syntax (<c>vec&lt;float32,64&gt;</c>, <c>vector</c>): a list of
    /// the same element (float32 when the element written is not one a list holds) and the same length, or <c>N</c> where
    /// none was written. Null when <paramref name="written"/> is not a vector spelling. For the errors and hints of every
    /// place a type is written, so they agree.
    /// </summary>
    public static string? VectorAsList(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        var core = written.Trim().ToLowerInvariant();
        if (!core.StartsWith("vec", StringComparison.Ordinal))
        {
            return null;
        }

        var open = core.IndexOf('<', StringComparison.Ordinal);
        var close = core.LastIndexOf('>');
        var comma = core.LastIndexOf(',');
        var element = "float32";
        var length = "N";
        if (open > 0 && close > open)
        {
            var hasLength = comma > open && comma < close;
            if (TryParseScalar(core[(open + 1)..(hasLength ? comma : close)].Trim(), out var kind) && IsListElement(kind))
            {
                element = ScalarName(kind);
            }

            if (hasLength
                && int.TryParse(core.AsSpan(comma + 1, close - comma - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                length = n.ToString(CultureInfo.InvariantCulture);
            }
        }

        return $"list<{element},{length}>";
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
            _ when element.StartsWith("list<", StringComparison.Ordinal) || element.StartsWith("vec", StringComparison.Ordinal) =>
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
            StorageKind.List when FixedLength > 0 => "list<" + ScalarName(Element) + "," + FixedLength.ToString(CultureInfo.InvariantCulture) + ">",
            StorageKind.List => "list<" + ScalarName(Element) + ">",
            _ => ScalarName(Kind),
        };
        return Nullable ? core + "?" : core;
    }

    public bool Equals(ColumnType other) =>
        Kind == other.Kind && Nullable == other.Nullable && FixedLength == other.FixedLength && Element == other.Element;

    public override bool Equals(object? obj) => obj is ColumnType other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Nullable, FixedLength, Element);

    public static bool operator ==(ColumnType left, ColumnType right) => left.Equals(right);

    public static bool operator !=(ColumnType left, ColumnType right) => !left.Equals(right);
}
