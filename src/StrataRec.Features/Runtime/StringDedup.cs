using System.Buffers;
using System.Text;

namespace StrataRec.Features.Runtime;

/// <summary>
/// Scoped string deduplication for decoding: within one scope (a connector call's writer, one cache read's fill), equal
/// values decoded from characters or UTF-8 come back as the same instance, so a category repeated across 5,000 keys is
/// one string, not 5,000.
/// </summary>
/// <remarks>
/// <para>Not thread-safe: one scope is written by one thread at a time.</para>
/// </remarks>
internal sealed class StringDedup
{
    public const int MaxValues = 4096;

    internal const int OnStack = 256;

    private HashSet<string>? _values;

    public int Count => _values?.Count ?? 0;

    public void Clear() => _values?.Clear();

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
