using System.Buffers;

namespace StrataRec.UnitTests.Pipeline;

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
