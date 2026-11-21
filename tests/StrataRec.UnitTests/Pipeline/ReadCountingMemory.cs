using System.Buffers;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>Memory over <paramref name="data"/> that counts how often its span is read: how many times an input's values were walked.</summary>
internal sealed class ReadCountingMemory<T>(T[] data) : MemoryManager<T>
{
    public int Reads { get; private set; }

    public override Span<T> GetSpan()
    {
        Reads++;
        return data;
    }

    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
