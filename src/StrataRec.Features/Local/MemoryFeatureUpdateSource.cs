using StrataRec.Artifacts.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;

namespace StrataRec.Features.Local;

/// <summary>One update held by a <see cref="MemoryFeatureUpdateSource"/>: the key's parts in declared order, field
/// values by physical name (<see cref="ScalarValue.Missing"/> clears a field), or a tombstone when <see cref="Fields"/>
/// is null.</summary>
public sealed record MemoryFeatureUpdate(IReadOnlyList<EntityKey> Key, long Version, IReadOnlyDictionary<string, ScalarValue>? Fields)
{
    public bool IsDelete => Fields is null;
}

/// <summary>
/// <c>memory_updates@1</c>: an update stream held in memory. Updates are appended with
/// <see cref="Upsert(EntityKey[], long, IReadOnlyDictionary{string, ScalarValue})"/> and
/// <see cref="Delete(EntityKey[], long)"/> (or their one-part forms); <see cref="RunAsync"/> applies them in order as
/// one partition (offset = index of the next update), checkpoints after each batch, and waits for more until
/// <see cref="Complete"/> or cancellation.
/// </summary>
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

    /// <summary>Appends an update of a one-part key.</summary>
    public void Upsert(EntityKey key, long version, IReadOnlyDictionary<string, ScalarValue> fields) => Upsert([key], version, fields);

    /// <summary>Appends an update of a key given by its parts in declared order; a key that is not one of the table's
    /// fails the run at <c>/key</c>.</summary>
    public void Upsert(EntityKey[] key, long version, IReadOnlyDictionary<string, ScalarValue> fields)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);
        Append(new MemoryFeatureUpdate((EntityKey[])key.Clone(), version, fields));
    }

    public void Delete(EntityKey key, long version) => Delete([key], version);

    public void Delete(EntityKey[] key, long version)
    {
        ArgumentNullException.ThrowIfNull(key);
        Append(new MemoryFeatureUpdate((EntityKey[])key.Clone(), version, null));
    }

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
            cancellationToken.ThrowIfCancellationRequested();
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

            // Cancellation is checked before the checkpoint and the return, so a cancellation that arrives while an
            // update is applied (the last one included) still ends the run with OperationCanceledException. Updates
            // applied before it stay; the position is not advanced past them, so a rerun from the table's position
            // applies them again, which versions make harmless.
            foreach (var update in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Apply(update, next, writer);
                next++;
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        var key = KeyOf(update, offset, writer.Schema);
        if (update.Fields is null)
        {
            writer.Delete(key, update.Version);
            return;
        }

        var builder = writer.Begin(key, update.Version);
        foreach (var (name, value) in update.Fields)
        {
            if (!writer.Schema.TryGetFieldIndex(name, out var field))
            {
                continue;
            }

            try
            {
                builder.Set(field, value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new FeatureUpdateException($"{Type} update {offset}", "/fields/" + JsonPointer.Escape(name), ex.Message,
                    $"give the value the field's type ({writer.Schema.Fields[field].Type})", ex);
            }
        }

        builder.Commit();
    }

    private static EntityKey[] KeyOf(MemoryFeatureUpdate update, int offset, LocalTableSchema schema)
    {
        var key = new EntityKey[update.Key.Count];
        for (var p = 0; p < key.Length; p++)
        {
            key[p] = update.Key[p];
        }

        var layout = schema.Key;
        if (key.Length != layout.PartCount)
        {
            throw new FeatureUpdateException($"{Type} update {offset}", "/key",
                $"The key has {key.Length} part(s); table {schema.Source}/{schema.Entity} is keyed by {layout.PartCount}: {layout.TypeText}.",
                "write the key as " + schema.KeyHint());
        }

        for (var p = 0; p < key.Length; p++)
        {
            if (key[p].IsString != (layout[p].Type == EntityKeyType.String))
            {
                throw new FeatureUpdateException($"{Type} update {offset}", layout.PartCount == 1 ? "/key" : "/key/" + p,
                    $"Key part {p} ({layout[p].Reference}) is {(key[p].IsString ? "a string" : "an int64")}; table {schema.Source}/{schema.Entity} is keyed by {layout.TypeText}.",
                    "write the key as " + schema.KeyHint());
            }
        }

        return key;
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
