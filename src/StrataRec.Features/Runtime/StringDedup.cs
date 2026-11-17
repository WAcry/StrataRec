using System.Buffers;
using System.Text;

namespace StrataRec.Features.Runtime;

/// <summary>
/// Scoped string deduplication for decoding: within one scope (a connector call's writer, one cache read's fill), equal
/// values decoded from characters or UTF-8 come back as the same instance, so a category repeated across 5,000 keys is
/// one string, not 5,000. The one place this logic lives: <see cref="StagingBuffer"/> (<c>IFeatureBatchWriter.Intern</c>)
/// and the cache's record decoding both use it.
/// </summary>
/// <remarks>
/// <para>Not <c>string.Intern</c>, which keeps every string for the life of the process: for data values
/// that is a leak. The table here goes away with its owner, and holds at most <see cref="MaxValues"/> values; past that a
/// value it does not hold is returned as a new string each time, so a scope full of distinct values (titles, ids) costs a
/// bounded table and no more.</para>
/// <para>A lookup allocates nothing (an alternate lookup by span); a value it does not hold costs its string and, below the
/// bound, a table entry. UTF-8 is decoded on the stack up to <see cref="OnStack"/> bytes and into a rented buffer beyond,
/// invalid bytes becoming U+FFFD as <see cref="Encoding.UTF8"/> decodes them. Not thread-safe: one scope is written by one
/// thread at a time.</para>
/// </remarks>
internal sealed class StringDedup
{
    /// <summary>Distinct values kept at most.</summary>
    public const int MaxValues = 4096;

    /// <summary>UTF-8 bytes decoded on the stack; a longer value is decoded into a rented buffer.</summary>
    internal const int OnStack = 256;

    private HashSet<string>? _values; // made by the first value, so a scope that decodes no string costs this object only

    /// <summary>Number of distinct values held.</summary>
    public int Count => _values?.Count ?? 0;

    /// <summary>A string with these characters: the held instance when an equal value was seen in this scope.</summary>
    public string Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var values = _values ??= new HashSet<string>(StringComparer.Ordinal);
        if (values.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(value, out var held))
        {
            return held;
        }

        var created = value.ToString();
        if (values.Count < MaxValues)
        {
            values.Add(created);
        }

        return created;
    }

    /// <summary>A string decoded from UTF-8, as <see cref="Intern(ReadOnlySpan{char})"/>; invalid bytes become U+FFFD.</summary>
    public string InternUtf8(ReadOnlySpan<byte> utf8)
    {
        // UTF-8 never decodes to more chars than it has bytes (a 4-byte sequence is 2 chars, an invalid byte at most 1).
        char[]? rented = null;
        var chars = utf8.Length <= OnStack ? stackalloc char[utf8.Length] : (rented = ArrayPool<char>.Shared.Rent(utf8.Length));
        try
        {
            return Intern(chars[..Encoding.UTF8.GetChars(utf8, chars)]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
