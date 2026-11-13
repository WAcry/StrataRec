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
    Text,
    Vector,
    ListInt,
    ListString,

    /// <summary>A constant float list literal such as <c>[0.0, 0.5]</c>; only usable by <c>in</c> and
    /// <c>piecewise_linear</c>, never a runtime value.</summary>
    ListFloat,
}

/// <summary>A promoted type with nullability. <see cref="Dim"/> is the vector dimension (0 = unknown).</summary>
internal readonly record struct ExprType(TypeKind Kind, bool Nullable, int Dim = 0)
{
    public static ExprType Error => default;

    public static ExprType Bool => new(TypeKind.Bool, false);

    public static ExprType Int => new(TypeKind.Int, false);

    public static ExprType Float => new(TypeKind.Float, false);

    public static ExprType String => new(TypeKind.String, false);

    public bool IsError => Kind == TypeKind.Error;

    public bool IsNumeric => Kind is TypeKind.Int or TypeKind.Float;

    public bool IsList => Kind is TypeKind.ListInt or TypeKind.ListString;

    public ExprType WithNullable(bool nullable) => new(Kind, nullable, Dim);

    public static ExprType FromColumnType(ColumnType type)
    {
        var p = type.Promoted();
        var kind = p.Kind switch
        {
            StorageKind.Bool => TypeKind.Bool,
            StorageKind.Int64 => TypeKind.Int,
            StorageKind.Float64 => TypeKind.Float,
            StorageKind.String => TypeKind.String,
            StorageKind.Text => TypeKind.Text,
            StorageKind.Vector => TypeKind.Vector,
            StorageKind.ListInt64 => TypeKind.ListInt,
            StorageKind.ListString => TypeKind.ListString,
            _ => TypeKind.Error,
        };
        return new ExprType(kind, type.Nullable, kind == TypeKind.Vector ? p.Dimension : 0);
    }

    public ColumnType ToColumnType()
    {
        var storage = Kind switch
        {
            TypeKind.Bool => StorageKind.Bool,
            TypeKind.Int => StorageKind.Int64,
            TypeKind.Float => StorageKind.Float64,
            TypeKind.String => StorageKind.String,
            TypeKind.Text => StorageKind.Text,
            TypeKind.Vector => StorageKind.Vector,
            TypeKind.ListInt => StorageKind.ListInt64,
            TypeKind.ListString => StorageKind.ListString,
            _ => throw new InvalidOperationException($"Type {this} has no column representation."),
        };
        return new ColumnType(storage, Nullable, Dim);
    }

    public override string ToString()
    {
        var core = Kind switch
        {
            TypeKind.Bool => "bool",
            TypeKind.Int => "int64",
            TypeKind.Float => "float64",
            TypeKind.String => "string",
            TypeKind.Text => "text",
            TypeKind.Vector => Dim > 0 ? $"vec<float32,{Dim}>" : "vec",
            TypeKind.ListInt => "list<int64>",
            TypeKind.ListString => "list<string>",
            TypeKind.ListFloat => "list<float64> literal",
            _ => "<error>",
        };
        return Nullable ? core + "?" : core;
    }
}
