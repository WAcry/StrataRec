using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using StrataRec.Core.Values;

namespace StrataRec.Core.Hashing;

/// <summary>
/// Stable, cross-platform hashing used by traffic assignment, log sampling and <c>hash01</c> (design 5.7, 8.4, 10.2).
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is XXH3-64 with seed 0 over a canonical byte encoding, so offline jobs in any language can
/// recompute bucket assignments and sampling decisions. Every value is encoded as a one-byte tag followed by
/// its payload:
/// </para>
/// <list type="bullet">
///   <item><c>0x01</c> int64: 8 bytes little-endian.</item>
///   <item><c>0x02</c> float64: 8 bytes little-endian IEEE 754, canonicalized by <see cref="CanonicalFloat64Bits"/>:
///   −0.0 is written as 0.0 and every NaN as the quiet NaN <c>0x7FF8000000000000</c>. IEEE 754 leaves the sign and
///   payload of a NaN produced by a domain error (<c>sqrt(-1)</c>, <c>0/0</c>) to the hardware and the operand order, so
///   an optimizer or SIMD rewrite may change them; the hash must not.</item>
///   <item><c>0x03</c> string: 4-byte little-endian UTF-8 length, then the UTF-8 bytes.</item>
///   <item><c>0x04</c> bool: 1 byte (0 or 1).</item>
///   <item><c>0x05</c> list index: 4 bytes little-endian (which expression in a key list matched, design 8.2).</item>
/// </list>
/// <para><c>hash01</c> maps the hash to [0, 1) as <c>(h &gt;&gt; 11) * 2^-53</c>.</para>
/// </remarks>
public static class StableHash
{
    private const byte TagInt64 = 0x01;
    private const byte TagFloat64 = 0x02;
    private const byte TagString = 0x03;
    private const byte TagBool = 0x04;
    private const byte TagIndex = 0x05;
    private const double Unit = 1.0 / (1UL << 53);

    /// <summary>The one NaN the canonical encoding writes: the positive quiet NaN <c>0x7FF8000000000000</c>.</summary>
    public const long CanonicalNaNBits = 0x7FF8000000000000;

    /// <summary>Maps a 64-bit hash to a uniform double in [0, 1).</summary>
    public static double ToUnit(ulong hash) => (hash >> 11) * Unit;

    /// <summary>
    /// The IEEE 754 bits that encode <paramref name="value"/> after tag <c>0x02</c>: −0.0 becomes 0.0 and every NaN
    /// becomes <see cref="CanonicalNaNBits"/>. Every writer of the canonical encoding must go through this method.
    /// </summary>
    public static long CanonicalFloat64Bits(double value) =>
        double.IsNaN(value) ? CanonicalNaNBits : value == 0.0 ? 0L : BitConverter.DoubleToInt64Bits(value);

    public static ulong Hash(long a)
    {
        Span<byte> buf = stackalloc byte[9];
        WriteInt64(buf, a);
        return XxHash3.HashToUInt64(buf);
    }

    public static ulong Hash(long a, long b)
    {
        Span<byte> buf = stackalloc byte[18];
        WriteInt64(buf, a);
        WriteInt64(buf[9..], b);
        return XxHash3.HashToUInt64(buf);
    }

    public static ulong Hash(long a, long b, long c)
    {
        Span<byte> buf = stackalloc byte[27];
        WriteInt64(buf, a);
        WriteInt64(buf[9..], b);
        WriteInt64(buf[18..], c);
        return XxHash3.HashToUInt64(buf);
    }

    public static double Hash01(long a, long b) => ToUnit(Hash(a, b));

    public static double Hash01(long a, long b, long c) => ToUnit(Hash(a, b, c));

    /// <summary>Derives a 64-bit seed from a salt string, e.g. a layer salt <c>"home_feed/rerank/3"</c>.</summary>
    public static long SeedFromSalt(string salt)
    {
        using var builder = new Builder();
        builder.Add(salt);
        return unchecked((long)builder.Finish());
    }

    /// <summary>
    /// Builder over the canonical encoding for mixed-type keys. Bytes accumulate in a pooled buffer and are hashed
    /// once by <see cref="Finish"/>, so building a key does not allocate in steady state. Dispose returns the buffer.
    /// </summary>
    public ref struct Builder
    {
        private byte[]? _buffer;
        private int _length;

        public void Add(long value)
        {
            var span = Reserve(9);
            WriteInt64(span, value);
        }

        public void Add(double value)
        {
            var span = Reserve(9);
            span[0] = TagFloat64;
            BinaryPrimitives.WriteInt64LittleEndian(span[1..], CanonicalFloat64Bits(value));
        }

        public void Add(bool value)
        {
            var span = Reserve(2);
            span[0] = TagBool;
            span[1] = value ? (byte)1 : (byte)0;
        }

        public void Add(string value)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            var span = Reserve(5 + byteCount);
            span[0] = TagString;
            BinaryPrimitives.WriteInt32LittleEndian(span[1..], byteCount);
            Encoding.UTF8.GetBytes(value, span[5..]);
        }

        public void AddIndex(int index)
        {
            var span = Reserve(5);
            span[0] = TagIndex;
            BinaryPrimitives.WriteInt32LittleEndian(span[1..], index);
        }

        /// <summary>Adds a non-missing scalar value (int64, float64, bool or string).</summary>
        public void Add(in ScalarValue value)
        {
            switch (value.Kind)
            {
                case Types.StorageKind.Int64: Add(value.AsInt64()); break;
                case Types.StorageKind.Float64: Add(value.AsFloat64()); break;
                case Types.StorageKind.Bool: Add(value.AsBool()); break;
                case Types.StorageKind.String or Types.StorageKind.Text: Add(value.AsString()); break;
                default: throw new ArgumentException($"Value of kind {value.Kind} cannot be hashed.", nameof(value));
            }
        }

        /// <summary>Hashes the bytes added so far. The builder can keep accumulating afterwards.</summary>
        public readonly ulong Finish() =>
            XxHash3.HashToUInt64(_buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _length));

        /// <summary>Clears the accumulated bytes, keeping the buffer for reuse.</summary>
        public void Reset() => _length = 0;

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                _length = 0;
            }
        }

        private Span<byte> Reserve(int count)
        {
            if (_buffer is null || _length + count > _buffer.Length)
            {
                var next = ArrayPool<byte>.Shared.Rent(Math.Max(64, (_length + count) * 2));
                if (_buffer is not null)
                {
                    _buffer.AsSpan(0, _length).CopyTo(next);
                    ArrayPool<byte>.Shared.Return(_buffer);
                }

                _buffer = next;
            }

            var span = _buffer.AsSpan(_length, count);
            _length += count;
            return span;
        }
    }

    private static void WriteInt64(Span<byte> destination, long value)
    {
        destination[0] = TagInt64;
        BinaryPrimitives.WriteInt64LittleEndian(destination[1..], value);
    }
}
