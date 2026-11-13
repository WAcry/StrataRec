using System.Text;

namespace StrataRec.Logging;

/// <summary>
/// Destination of encoded log batches: a message queue, object storage, a file (design 8.6). Throwing signals a
/// failed write; durable batches are retried, best-effort ones are dropped and counted.
/// </summary>
public interface ILogSink
{
    ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken);
}

/// <summary>Keeps batches in memory: tests, <c>stratarec run --logs</c>, replays.</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<LogBatch> _batches = new();
    private readonly object _gate = new();

    public List<LogBatch> Snapshot()
    {
        lock (_gate)
        {
            return new List<LogBatch>(_batches);
        }
    }

    public ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            for (var i = 0; i < batches.Count; i++)
            {
                _batches.Add(batches[i]);
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Writes one JSON document per batch per line (the debug encoding of design 8.7).</summary>
public sealed class JsonLinesLogSink : ILogSink, IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesLogSink(TextWriter writer, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
    }

    /// <summary>Appends to a file, creating it when missing.</summary>
    public static JsonLinesLogSink ForFile(string path) =>
        new(new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)), ownsWriter: true);

    public async ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        for (var i = 0; i < batches.Count; i++)
        {
            text.Append(LogBatchJson.Encode(batches[i], indented: false)).Append('\n');
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(text, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }

        _gate.Dispose();
    }
}
