using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using StrataRec.Core.Hashing;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// <c>hash01(a, b, ...)</c>: <see cref="StableHash.ToUnit"/> of the XXH3 hash of the canonical encoding of the
/// arguments in order — exactly what a <see cref="StableHash.Builder"/> fed the same values produces (design 5.7).
/// </summary>
/// <remarks>
/// All-int64 calls with two or three arguments use <see cref="StableHash.Hash01(long, long)"/> directly. Mixed
/// arguments are encoded into a reusable byte buffer and hashed in one shot, which equals the builder's streaming
/// result and allocates nothing per row.
/// This is deliberately a second writer of the canonical encoding rather than a <see cref="StableHash.Builder"/>: it
/// starts on a caller-provided stack buffer, so the scalar path never touches the array pool. Float64 values go through
/// <see cref="StableHash.CanonicalFloat64Bits"/> like the builder's (−0.0 as 0.0, every NaN as one quiet NaN), so the
/// optimizer's and the SIMD kernels' freedom over NaN sign and payload cannot change a hash. The hash01 parity tests
/// (<c>SemanticsTests</c>, <c>ExpressionReviewRegressionTests</c>) pin both writers to the same bytes.
/// </remarks>
internal ref struct Hash01Encoder
{
    private const byte TagInt64 = 0x01;
    private const byte TagFloat64 = 0x02;
    private const byte TagString = 0x03;
    private const byte TagBool = 0x04;

    private byte[]? _rented;
    private Span<byte> _buffer;
    private int _length;

    public Hash01Encoder(Span<byte> initial)
    {
        _buffer = initial;
        _rented = null;
        _length = 0;
    }

    public void Reset() => _length = 0;

    public void Add(long value)
    {
        var dst = Reserve(9);
        dst[0] = TagInt64;
        BinaryPrimitives.WriteInt64LittleEndian(dst[1..], value);
    }

    public void Add(double value)
    {
        var dst = Reserve(9);
        dst[0] = TagFloat64;
        BinaryPrimitives.WriteInt64LittleEndian(dst[1..], StableHash.CanonicalFloat64Bits(value));
    }

    public void Add(bool value)
    {
        var dst = Reserve(2);
        dst[0] = TagBool;
        dst[1] = value ? (byte)1 : (byte)0;
    }

    public void Add(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var dst = Reserve(5 + byteCount);
        dst[0] = TagString;
        BinaryPrimitives.WriteInt32LittleEndian(dst[1..], byteCount);
        Encoding.UTF8.GetBytes(value, dst[5..]);
    }

    public readonly double Finish() => StableHash.ToUnit(XxHash3.HashToUInt64(_buffer[.._length]));

    public void Dispose()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = null;
        }
    }

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
            _buffer[.._length].CopyTo(bigger);
            if (_rented is not null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
            }

            _rented = bigger;
            _buffer = bigger;
        }

        var span = _buffer.Slice(_length, count);
        _length += count;
        return span;
    }
}
