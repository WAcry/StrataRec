using System.Text;

namespace StrataRec.Logging;

/// <summary>
/// Destination of encoded log batches: a message queue, object storage, a file (design 8.6). Throwing signals a
/// failed write; durable batches are retried, best-effort ones are dropped and counted. Throw
/// <see cref="LogBatchRejectedException"/> for batches that can never be written, after writing all the others.
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

/// <summary>
/// Writes one JSON document per batch per line (the debug encoding of design 8.7), and before the first batch of each
/// schema id the sink has not written, that schema's record (<see cref="LogBatchJson.WriteSchema"/>).
/// </summary>
public sealed class JsonLinesLogSink : ILogSink, IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _schemas = new(StringComparer.Ordinal);

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
        List<LogBatch>? rejected = null;
        Exception? reason = null;
        for (var i = 0; i < batches.Count; i++)
        {
            string line;
            try
            {
                line = LogBatchJson.Encode(batches[i], indented: false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                // Encoding is deterministic: retrying cannot help, and must not block the batches behind this one.
                (rejected ??= new List<LogBatch>()).Add(batches[i]);
                reason ??= ex;
                continue;
            }

            text.Append(line).Append('\n');
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The records of schemas this sink has not written, ahead of the batches; noted only once written, so a failed
            // write writes them again with the retry.
            List<string>? added = null;
            var records = new StringBuilder();
            for (var i = 0; i < batches.Count; i++)
            {
                var schema = batches[i].Schema;
                if (!_schemas.Contains(schema.Id) && (added is null || !added.Contains(schema.Id)))
                {
                    (added ??= new List<string>()).Add(schema.Id);
                    records.Append(LogBatchJson.EncodeSchema(schema, indented: false)).Append('\n');
                }
            }

            await _writer.WriteAsync(records.Append(text), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (added is not null)
            {
                _schemas.UnionWith(added);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (rejected is not null)
        {
            throw new LogBatchRejectedException(rejected, reason);
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
