using StrataRec.Core.Types;

namespace StrataRec.Expressions.Binding;

/// <summary>Expression-level type kinds (design 4.3, after promotion).</summary>
internal enum TypeKind : byte
{
    /// <summary>Poison type of an ill-typed subexpression; suppresses cascading diagnostics.</summary>
    Error = 0,
    Bool,
    Int,
    Float,
    String,

    // Lists, by promoted element type (design 4.3: list<int32> reads as list<int64>, list<float32> as list<float64>).
    ListBool,
    ListInt,
    ListFloat,
    ListString,
}

/// <summary>A promoted type with nullability. <see cref="Length"/> is a list's fixed length (0 = any length, and for
/// every other kind): what <c>dot</c> and <c>cosine</c> compare at compile time, and what a fixed-length site checks.</summary>
internal readonly record struct ExprType(TypeKind Kind, bool Nullable, int Length = 0)
{
    public static ExprType Error => default;

    public static ExprType Bool => new(TypeKind.Bool, false);

    public static ExprType Int => new(TypeKind.Int, false);

    public static ExprType Float => new(TypeKind.Float, false);

    public static ExprType String => new(TypeKind.String, false);

    public bool IsError => Kind == TypeKind.Error;

    public bool IsNumeric => Kind is TypeKind.Int or TypeKind.Float;

    public bool IsList => IsListKind(Kind);

    public static bool IsListKind(TypeKind kind) => kind is TypeKind.ListBool or TypeKind.ListInt or TypeKind.ListFloat or TypeKind.ListString;

    /// <summary>The type of a list's elements (never nullable): bool, int64, float64 or string.</summary>
    public ExprType Element => new(ElementKind(Kind), false);

    /// <summary>The kind of a list kind's elements; <see cref="TypeKind.Error"/> for other kinds.</summary>
    public static TypeKind ElementKind(TypeKind list) => list switch
    {
        TypeKind.ListBool => TypeKind.Bool,
        TypeKind.ListInt => TypeKind.Int,
        TypeKind.ListFloat => TypeKind.Float,
        TypeKind.ListString => TypeKind.String,
        _ => TypeKind.Error,
    };

    /// <summary>The list kind whose elements are <paramref name="element"/>; <see cref="TypeKind.Error"/> when there is none.</summary>
    public static TypeKind ListOf(TypeKind element) => element switch
    {
        TypeKind.Bool => TypeKind.ListBool,
        TypeKind.Int => TypeKind.ListInt,
        TypeKind.Float => TypeKind.ListFloat,
        TypeKind.String => TypeKind.ListString,
        _ => TypeKind.Error,
    };

    public ExprType WithNullable(bool nullable) => new(Kind, nullable, Length);

    public static ExprType FromColumnType(ColumnType type)
    {
        var p = type.Promoted();
        var kind = p.Kind == StorageKind.List ? ListOf(ScalarKind(p.Element)) : ScalarKind(p.Kind);
        return new ExprType(kind, type.Nullable, p.FixedLength);
    }

    private static TypeKind ScalarKind(StorageKind kind) => kind switch
    {
        StorageKind.Bool => TypeKind.Bool,
        StorageKind.Int64 => TypeKind.Int,
        StorageKind.Float64 => TypeKind.Float,
        StorageKind.String => TypeKind.String,
        StorageKind.Text => TypeKind.String, // text reads as string, as int32 reads as int64: storage, not a type of its own
        _ => TypeKind.Error,
    };

    public ColumnType ToColumnType()
    {
        if (IsList)
        {
            return ColumnType.List(Element.ToColumnType().Kind, Nullable, Length);
        }

        var storage = Kind switch
        {
            TypeKind.Bool => StorageKind.Bool,
            TypeKind.Int => StorageKind.Int64,
            TypeKind.Float => StorageKind.Float64,
            TypeKind.String => StorageKind.String,
            _ => throw new InvalidOperationException($"Type {this} has no column representation."),
        };
        return new ColumnType(storage, Nullable);
    }

    public override string ToString() => IsList ? ToColumnType().ToString() : Kind switch
    {
        TypeKind.Bool => Nullable ? "bool?" : "bool",
        TypeKind.Int => Nullable ? "int64?" : "int64",
        TypeKind.Float => Nullable ? "float64?" : "float64",
        TypeKind.String => Nullable ? "string?" : "string",
        _ => "<error>",
    };
}
