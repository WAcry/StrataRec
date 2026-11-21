using System.Text;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <see cref="StringDedup"/>, the scoped deduplication behind <c>IFeatureBatchWriter.Intern</c> and the cache's record
/// decoding: one instance per distinct value from characters or UTF-8, a bounded table, and decoding as
/// <see cref="Encoding.UTF8"/> decodes.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class StringDedupTests
{
    [Fact]
    public void EqualValues_AreOneInstance_FromCharsAndUtf8_ShortAndLong()
    {
        var dedup = new StringDedup();

        var music = dedup.Intern("music".AsSpan());
        Assert.Equal("music", music);
        Assert.Same(music, dedup.Intern("music".ToCharArray()));
        Assert.Same(music, dedup.InternUtf8("music"u8));

        // Past the stack bound: decoded into a rented buffer, and still one instance.
        var longValue = new string('x', 1000) + "é😀";
        var longUtf8 = Encoding.UTF8.GetBytes(longValue);
        var held = dedup.InternUtf8(longUtf8);
        Assert.Equal(longValue, held);
        Assert.Same(held, dedup.InternUtf8(longUtf8));
        Assert.Same(held, dedup.Intern(longValue.AsSpan()));

        // At the bound and one past it.
        var atBound = new string('y', StringDedup.OnStack);
        var pastBound = new string('z', StringDedup.OnStack + 1);
        Assert.Same(dedup.InternUtf8(Encoding.UTF8.GetBytes(atBound)), dedup.Intern(atBound.AsSpan()));
        Assert.Same(dedup.InternUtf8(Encoding.UTF8.GetBytes(pastBound)), dedup.Intern(pastBound.AsSpan()));

        Assert.Same(string.Empty, dedup.Intern(default));
        Assert.Same(string.Empty, dedup.InternUtf8(default));
        Assert.Equal(4, dedup.Count);
    }

    public static TheoryData<byte[]> InvalidUtf8 => new()
    {
        new byte[] { 0x61, 0xFF, 0x62 }, // a byte that never starts a sequence
        new byte[] { 0x61, 0xC3 }, // a sequence cut short at the end
        new byte[] { 0xF0, 0x9F, 0x98, 0x61 }, // a 4-byte sequence cut short
        new byte[] { 0xED, 0xA0, 0x80 }, // an encoded surrogate
        new byte[] { 0xC0, 0xAF }, // an overlong form
        Enumerable.Repeat((byte)0x62, 300).Concat(new byte[] { 0xFE, 0xE2, 0x82 }).ToArray(), // long, past the stack bound
    };

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public void InvalidUtf8_DecodesAsEncodingUtf8Does(byte[] bytes)
    {
        var dedup = new StringDedup();

        var decoded = dedup.InternUtf8(bytes);

        Assert.Equal(Encoding.UTF8.GetString(bytes), decoded);
        Assert.Contains('�', decoded);
        Assert.Same(decoded, dedup.InternUtf8(bytes));
    }

    [Fact]
    public void TheTable_KeepsAtMostMaxValues_AndPastThemEachCallIsANewString()
    {
        var dedup = new StringDedup();
        for (var i = 0; i < StringDedup.MaxValues + 10; i++)
        {
            dedup.Intern(("v" + i).AsSpan());
        }

        Assert.Equal(StringDedup.MaxValues, dedup.Count);
        Assert.Same(dedup.Intern("v1".AsSpan()), dedup.Intern("v1".AsSpan())); // held before the bound

        var past = "v" + (StringDedup.MaxValues + 5);
        var first = dedup.Intern(past.AsSpan());
        var second = dedup.InternUtf8(Encoding.UTF8.GetBytes(past));
        Assert.Equal(past, first);
        Assert.Equal(past, second);
        Assert.NotSame(first, second); // past the bound: a string of its own each time
        Assert.Equal(StringDedup.MaxValues, dedup.Count);
    }

    [Fact]
    public void AHeldValue_CostsNoAllocation_FromCharsOrUtf8_ShortOrLong()
    {
        var dedup = new StringDedup();
        var longUtf8 = Encoding.UTF8.GetBytes(new string('x', 2000));
        dedup.InternUtf8("music"u8);
        dedup.InternUtf8(longUtf8);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            dedup.InternUtf8("music"u8);
            dedup.Intern("music".AsSpan());
            dedup.InternUtf8(longUtf8);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
