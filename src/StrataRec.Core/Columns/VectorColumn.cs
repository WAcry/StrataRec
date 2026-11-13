using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Fixed-length <c>vec&lt;float32,N&gt;</c> column: one flat array, row <c>r</c> is <c>Flat[r*Dim .. r*Dim+Dim)</c>
/// (design 4.4). Suited to SIMD dot products.
/// </summary>
public sealed class VectorColumn : ColumnBuffer
{
    private float[] _flat;

    public VectorColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        if (type.Dimension <= 0)
        {
            throw new ArgumentException("Vector columns need a positive dimension.", nameof(type));
        }

        Dimension = type.Dimension;
        _flat = memory.Rent<float>(checked(capacity * Dimension));
    }

    public int Dimension { get; }

    public float[] Flat => _flat;

    public Span<float> Row(int row) => _flat.AsSpan(row * Dimension, Dimension);

    public void Set(int row, ReadOnlySpan<float> value)
    {
        if (value.Length != Dimension)
        {
            throw new ArgumentException($"Expected {Dimension} elements, got {value.Length}.", nameof(value));
        }

        value.CopyTo(Row(row));
        SetValid(row);
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (VectorColumn)source;
        if (s.Dimension != Dimension)
        {
            throw new ArgumentException($"Cannot copy a {s.Dimension}-dimensional vector into a {Dimension}-dimensional column.", nameof(source));
        }

        s.Row(sourceRow).CopyTo(Row(row));
        CopyValidityFrom(source, sourceRow, row);
    }

    protected override void GatherValues(ReadOnlySpan<int> rows, int newCapacity)
    {
        var next = Memory.Rent<float>(Math.Max(1, newCapacity * Dimension));
        for (var i = 0; i < rows.Length; i++)
        {
            Row(rows[i]).CopyTo(next.AsSpan(i * Dimension, Dimension));
        }

        _flat = next;
    }
}
