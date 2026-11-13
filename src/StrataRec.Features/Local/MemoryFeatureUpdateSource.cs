using StrataRec.Core.Keys;
using StrataRec.Core.Values;

namespace StrataRec.Features.Local;

/// <summary>One update held by a <see cref="MemoryFeatureUpdateSource"/>: field values by physical name
/// (<see cref="ScalarValue.Missing"/> clears a field), or a tombstone when <see cref="Fields"/> is null.</summary>
public sealed record MemoryFeatureUpdate(EntityKey Key, long Version, IReadOnlyDictionary<string, ScalarValue>? Fields)
{
    public bool IsDelete => Fields is null;
}

/// <summary>
/// <c>memory_updates@1</c>: an update stream held in memory, for tests and samples. Updates are appended with
/// <see cref="Upsert"/> and <see cref="Delete"/>; <see cref="RunAsync"/> applies them in order as one partition (offset =
/// index of the next update), checkpoints after each batch, and waits for more until <see cref="Complete"/> or cancellation.
/// </summary>
/// <remarks>Not built for speed: values arrive as <see cref="ScalarValue"/>s and go through <see cref="RecordBuilder.Set"/>.</remarks>
public sealed class MemoryFeatureUpdateSource : IFeatureUpdateSource
{
    public const string Type = "memory_updates@1";

    private readonly object _gate = new();
    private readonly List<MemoryFeatureUpdate> _updates = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _completed;

    public string SourceType => Type;

    /// <summary>Updates appended so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _updates.Count;
            }
        }
    }

    public void Upsert(EntityKey key, long version, IReadOnlyDictionary<string, ScalarValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Append(new MemoryFeatureUpdate(key, version, fields));
    }

    public void Delete(EntityKey key, long version) => Append(new MemoryFeatureUpdate(key, version, null));

    /// <summary>No more updates: <see cref="RunAsync"/> returns once it has applied the ones appended so far.</summary>
    public void Complete()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _completed = true;
            signal = _changed;
        }

        signal.TrySetResult();
    }

    public async Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(writer);
        var next = (int)Math.Min(int.MaxValue, from.OffsetOf(0));
        while (true)
        {
            MemoryFeatureUpdate[] batch;
            Task changed;
            bool completed;
            lock (_gate)
            {
                var count = Math.Max(0, _updates.Count - next);
                batch = count == 0 ? Array.Empty<MemoryFeatureUpdate>() : _updates.GetRange(next, count).ToArray();
                changed = _changed.Task;
                completed = _completed;
            }

            foreach (var update in batch)
            {
                Apply(update, next, writer);
                next++;
            }

            if (batch.Length > 0)
            {
                writer.Checkpoint(UpdatePosition.Single(next));
            }

            if (completed)
            {
                return;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Apply(MemoryFeatureUpdate update, int offset, IFeatureTableWriter writer)
    {
        if (update.Fields is null)
        {
            writer.Delete(update.Key, update.Version);
            return;
        }

        var builder = writer.Begin(update.Key, update.Version);
        foreach (var (name, value) in update.Fields)
        {
            if (!writer.Schema.TryGetFieldIndex(name, out var field))
            {
                continue; // a field of the stream that no feature of this table reads
            }

            try
            {
                builder.Set(field, value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new FeatureUpdateException($"{Type} update {offset}", "/fields/" + name, ex.Message,
                    $"give the value the field's type ({writer.Schema.Fields[field].Type})", ex);
            }
        }

        builder.Commit();
    }

    private void Append(MemoryFeatureUpdate update)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The source was completed; no more updates can be added.");
            }

            _updates.Add(update);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
