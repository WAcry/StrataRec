using StrataRec.Core.Columns;
using StrataRec.Core.Values;

namespace StrataRec.Expressions.Vectorized;

/// <summary>An instruction operand: a row-varying node (by instruction index) or a hoisted request-level value
/// (by scalar slot).</summary>
internal readonly record struct Opnd(int Index, bool IsScalar)
{
    public static Opnd Node(int index) => new(index, false);

    public static Opnd Scalar(int slot) => new(slot, true);

    public override string ToString() => IsScalar ? $"s{Index}" : $"n{Index}";
}

/// <summary>How a vector or list node's per-lane values are found.</summary>
internal enum SourceKind : byte
{
    None,

    /// <summary>A <see cref="VectorColumn"/> or <see cref="ListColumn{T}"/>; lane → physical row.</summary>
    Column,

    /// <summary>One array for every lane (empty for an all-missing column).</summary>
    Broadcast,

    /// <summary>Per lane, <c>SelA</c> where the mask bit is set (or the mask is null), else <c>SelB</c>.</summary>
    Select,
}

/// <summary>
/// Where one node's result lives during an evaluation. Scalar kinds point either at a register (a slice of a rented
/// register file) or, zero-copy, at a column's own array in full-range mode. Validity is <c>null</c> when every lane is
/// present. Vector and list nodes are lazy sources resolved per lane.
/// </summary>
internal struct Loc
{
    public Array? Values;
    public int Offset;
    public ulong[]? Valid;
    public int ValidOffset;
    public SourceKind Source;
    public object? SourceRef;
    public ulong[]? Mask;
    public int MaskOffset;
    public Opnd SelA;
    public Opnd SelB;
}

/// <summary>
/// Per-call evaluation state. Lanes are either all physical rows <c>[0, N)</c> (<see cref="Identity"/>) or the
/// requested rows gathered densely (<c>lane i ↔ Map[i]</c>).
/// </summary>
internal ref struct Frame
{
    public int N;

    /// <summary>The table's physical row count; every <see cref="Map"/> entry is below it (checked on entry).</summary>
    public int Physical;
    public int Words;
    public bool Identity;
    public ReadOnlySpan<int> Map;
    public EvalContext Context;
    public ScalarValue[] Scalars;
    public Loc[] Locs;
    public double[] F;
    public long[] I;
    public ulong[] B;
    public string?[] S;

    /// <summary>Live lanes, only for counting total-semantics events in full-range mode (null = all lanes live).</summary>
    public ulong[]? Live;

    public readonly int Row(int lane) => Identity ? lane : Map[lane];

    public readonly ReadOnlySpan<double> GetF(int node)
    {
        ref var l = ref Locs[node];
        return new ReadOnlySpan<double>((double[])l.Values!, l.Offset, N);
    }

    public readonly ReadOnlySpan<long> GetI(int node)
    {
        ref var l = ref Locs[node];
        return new ReadOnlySpan<long>((long[])l.Values!, l.Offset, N);
    }

    public readonly ReadOnlySpan<ulong> GetB(int node)
    {
        ref var l = ref Locs[node];
        return new ReadOnlySpan<ulong>((ulong[])l.Values!, l.Offset, Words);
    }

    public readonly ReadOnlySpan<string?> GetS(int node)
    {
        ref var l = ref Locs[node];
        return new ReadOnlySpan<string?>((string?[])l.Values!, l.Offset, N);
    }

    public readonly ref ScalarValue Scalar(Opnd o) => ref Scalars[o.Index];

    /// <summary>Validity of an operand as a per-word view.</summary>
    public readonly WordView Validity(Opnd o)
    {
        if (o.IsScalar)
        {
            return WordView.Constant(Scalars[o.Index].IsMissing ? 0UL : ulong.MaxValue);
        }

        ref var l = ref Locs[o.Index];
        return l.Valid is null ? WordView.Constant(ulong.MaxValue) : new WordView(new ReadOnlySpan<ulong>(l.Valid, l.ValidOffset, Words));
    }

    /// <summary>Bool values of an operand as a per-word view (a missing scalar reads as false; validity decides).</summary>
    public readonly WordView BoolValues(Opnd o)
    {
        if (o.IsScalar)
        {
            var s = Scalars[o.Index];
            return WordView.Constant(!s.IsMissing && s.AsBool() ? ulong.MaxValue : 0UL);
        }

        return new WordView(GetB(o.Index));
    }

    public readonly bool IsLive(int lane) => Live is null || Bits.Get(Live, lane);
}

/// <summary>A bitmap that is either backed by words or constant (all ones / all zeros).</summary>
internal readonly ref struct WordView
{
    private readonly ReadOnlySpan<ulong> _words;
    private readonly ulong _fill;
    private readonly bool _hasWords;

    public WordView(ReadOnlySpan<ulong> words)
    {
        _words = words;
        _fill = 0;
        _hasWords = true;
    }

    private WordView(ulong fill)
    {
        _words = default;
        _fill = fill;
        _hasWords = false;
    }

    public static WordView Constant(ulong fill) => new(fill);

    public bool IsConstant => !_hasWords;

    public ulong Fill => _fill;

    public ulong this[int word] => _hasWords ? _words[word] : _fill;

    public bool Get(int lane) => ((this[lane >> 6] >> lane) & 1) != 0;
}
