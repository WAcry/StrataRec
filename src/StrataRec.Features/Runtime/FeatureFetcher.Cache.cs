using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The request read path of cache groups (design 6.6 <c>cache</c>): everything a request does with a cache source is here.
/// </summary>
/// <remarks>
/// <para><b>Tiers.</b> A cache read starts as a local read (<c>FeatureFetcher.Local.cs</c>): the same rows, one lookup of
/// their distinct keys, one fill. A key whose record is missing or past its lifetime is a miss. The misses go to the
/// distributed tier, if the binding has one, in chunks of <c>max_batch</c>, one round trip each, waited for at most its
/// <c>timeout_ms</c> (by default half the read's budget); what it does not have fresh goes to the connector through the
/// fetch path's own call (<see cref="ConnectorCall"/>: chunks, bounded waits, one cancellation). The read's budget is the
/// source's <c>timeout_ms</c> or what is left of the request, whichever is less, from the read's start, for both tiers
/// together. Both calls keep the wait invariants of the fetch path (decisions.md): <see cref="StoreCall"/> has the same
/// shape as <see cref="ConnectorCall"/>.</para>
/// <para><b>Hits never wait.</b> Fresh keys are filled at once; a read whose keys are all fresh is a local read, which
/// allocates nothing and awaits nothing. A read with misses fills its hits, resolves its misses while the wave's other I/O
/// runs (<see cref="ResolveAsync"/>, which touches only the read's own buffers) and fills them, on the request's thread,
/// when the wave completes (<see cref="CompleteCacheReadAsync"/>).</para>
/// <para><b>Failures.</b> The distributed tier only ever saves time: when it fails or is late, its keys go to the
/// connector, and the features are not degraded for it. When the connector fails for a key, a record within
/// <c>stale_if_error_ms</c> after its lifetime is served (the newer of the two tiers'), and the features are degraded; a
/// key without one takes its absent value. The features fail only when no key of the read got a value.</para>
/// <para><b>Write-back</b> is off the request path and never waits (<see cref="WriteBack"/>): records the distributed tier
/// had go to the table's writer; records the connector answered in full and in time go to both tiers, a key it answered
/// without any field as an absent record (negative caching). A record's version is when the connector call started, so
/// a slow call never replaces what a later one wrote. A miss fetches every field of the table, not only the ones the
/// node needs, so that the records it writes back are complete.</para>
/// <para><b>Replay</b> (design 13.4). A recording request records each cache read as one feature call: its keys (the
/// keys a fetch of the same rows takes, in the same order), the values it served whichever tier they came from, and how
/// it ended. A replay answers cache groups like local ones, through the fetch path from the recording, so it never
/// touches a table, a store or a connector.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    /// <summary>The sequence of a connector call recorded as part of something else (a cache read's misses), not on its own.</summary>
    private const int NotRecorded = -1;

    /// <summary>What a read of the distributed tier gives for a chunk that failed, was late or was cancelled: its records are never read.</summary>
    private const long ChunkNotAnswered = long.MinValue;

    private CacheRead? _cacheHead; // candidate cache reads of the current wave still resolving, in start order
    private CacheRead? _cacheTail;

    /// <summary>The oldest candidate cache read still resolving, taken off the list; null when there is none.</summary>
    private CacheRead? TakeCacheRead()
    {
        var read = _cacheHead;
        if (read is not null)
        {
            _cacheHead = read.Next;
            read.Next = null;
            if (_cacheHead is null)
            {
                _cacheTail = null;
            }
        }

        return read;
    }

    /// <summary>
    /// One read of a candidate (or foreign-key) cache group: the rows <see cref="LookUp"/> selects. Fills the fresh keys'
    /// rows now; a read with misses is left resolving on the wave's list.
    /// </summary>
    private void ReadCached(FetchGroup group, ReadOnlySpan<int> features, RowScope scope, CancellationToken cancellationToken)
    {
        var cache = group.Supply.Table!.Table.Cache!;
        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);
        var batch = LookUp(group, features, version, scope);
        try
        {
            ReadCached(group, features, cache, version, batch, started, cancellationToken);
        }
        finally
        {
            batch.Return(_memory); // a read that goes on resolving has copied what it needs of the batch
        }
    }

    private void ReadCached(
        FetchGroup group, ReadOnlySpan<int> features, LocalTableCache cache, TableVersion version, in LocalReadBatch batch, long started,
        CancellationToken cancellationToken)
    {
        if (batch.Rows.Length == 0)
        {
            SetReadState(group, features);
            return;
        }

        var keyCount = batch.KeyRecords.Length;
        var nowMs = cache.NowMs();
        var misses = 0;
        var keys = keyCount == 0 ? default : batch.Keys.Batch(_memory);
        var hashes = _memory.Rent<ulong>(Math.Max(1, keyCount));
        for (var k = 0; k < keyCount; k++)
        {
            hashes[k] = LocalTableCache.KeyHash(keys, k);
            misses += Freshness(cache, version, batch.KeyRecords[k], hashes[k], nowMs) == CacheFreshness.Fresh ? 0 : 1;
        }

        if (misses == 0)
        {
            // Every key fresh (or no row has a key): a local read, recorded before its metrics (ReadLocal).
            Fill(group, features, version, batch.Rows, batch.RowRecords);
            if (keyCount > 0 && _hook is { Replays: false } recorder)
            {
                RecordRead(recorder, group, features, version, batch, started);
            }

            cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Hit, keyCount);
            if (keyCount > 0 && CountsValues)
            {
                CountTableValues(group, features, version, batch.KeyRecords);
            }

            SetReadState(group, features);
            return;
        }

        var read = new CacheRead(group, features.ToArray(), version, keys, hashes, misses, batch.Rows.Length, isContext: false, _memory);
        batch.KeyRecords.CopyTo(read.Records);
        SortMisses(read, nowMs);

        // The hits are filled now, from scratch lists; the misses' rows go to the read, which fills them once resolved.
        var hitRows = _memory.RentScratch<int>(batch.Rows.Length);
        var hitRecords = _memory.RentScratch<LocalRecordRef>(batch.Rows.Length);
        try
        {
            var hits = 0;
            for (var i = 0; i < batch.Rows.Length; i++)
            {
                var entry = batch.RowKeys[i];
                if (entry >= 0 && read.MissOf[entry] >= 0)
                {
                    read.AddRow(batch.Rows[i], read.MissOf[entry]);
                }
                else
                {
                    hitRows[hits] = batch.Rows[i];
                    hitRecords[hits++] = batch.RowRecords[i];
                }
            }

            if (hits > 0)
            {
                Fill(group, features, version, hitRows.AsSpan(0, hits), hitRecords.AsSpan(0, hits));
            }
        }
        finally
        {
            _memory.ReturnScratch(hitRows);
            _memory.ReturnScratch(hitRecords);
        }

        Resolve(read, started, cancellationToken);
        if (_cacheTail is null)
        {
            _cacheHead = read;
        }
        else
        {
            _cacheTail.Next = read;
        }

        _cacheTail = read;
    }

    /// <summary>
    /// The read of a context cache group (one key, from request inputs or context key features), from the context prefetch: a fresh record is
    /// published at once; otherwise the read is returned resolving, for <see cref="CompleteCachedContextAsync"/>.
    /// </summary>
    private CacheRead? ReadCachedContext(
        FetchGroup group, ReadOnlySpan<int> features, TableVersion version, in EntityKeyBatch key, in LocalRecordRef record, long started,
        CancellationToken cancellationToken)
    {
        var cache = group.Supply.Table!.Table.Cache!;
        var nowMs = cache.NowMs();
        var hashes = _memory.Rent<ulong>(1);
        hashes[0] = LocalTableCache.KeyHash(key, 0);
        if (Freshness(cache, version, record, hashes[0], nowMs) == CacheFreshness.Fresh)
        {
            StoreLocalContext(group, features, version, key, record, started); // recorded before its metrics (ReadLocal)
            cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Hit, 1);
            if (CountsValues)
            {
                CountTableValues(group, features, version, new ReadOnlySpan<LocalRecordRef>(in record));
            }

            return null;
        }

        var read = new CacheRead(group, features.ToArray(), version, key, hashes, missCount: 1, rowCapacity: 0, isContext: true, _memory);
        read.Records[0] = record;
        SortMisses(read, nowMs);
        Resolve(read, started, cancellationToken);
        return read;
    }

    /// <summary>How a key's table record stands now; a key the table does not hold is <see cref="CacheFreshness.Expired"/>.</summary>
    private static CacheFreshness Freshness(LocalTableCache cache, TableVersion version, in LocalRecordRef record, ulong keyHash, long nowMs) =>
        record.Found ? cache.Classify(version.VersionOf(record), keyHash, nowMs) : CacheFreshness.Expired;

    /// <summary>Sorts the read's keys into fresh ones and misses (whose table record may still serve if the connector fails).</summary>
    private static void SortMisses(CacheRead read, long nowMs)
    {
        var cache = read.Cache;
        int fresh = 0, missing = 0, expired = 0, m = 0;
        for (var k = 0; k < read.KeyCount; k++)
        {
            var freshness = Freshness(cache, read.Version, read.Records[k], read.KeyHashes[k], nowMs);
            if (freshness == CacheFreshness.Fresh)
            {
                read.MissOf[k] = -1;
                fresh++;
                continue;
            }

            read.MissOf[k] = m;
            read.MissKey[m] = k;
            read.LocalStale[m] = freshness == CacheFreshness.Stale;
            m++;
            if (read.Records[k].Found)
            {
                expired++;
            }
            else
            {
                missing++;
            }
        }

        read.SelectMisses();
        cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Hit, fresh);
        cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Miss, missing);
        cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Expired, expired);
    }

    /// <summary>
    /// Begins the read's recorded call (on the request's thread, so the order is deterministic), fixes its budget and
    /// starts resolving its misses. The read holds the request memory until it has resolved.
    /// </summary>
    private void Resolve(CacheRead read, long started, CancellationToken cancellationToken)
    {
        read.Started = started;
        if (_hook is { Replays: false } recorder)
        {
            read.Fields = FieldBindings(read.Group, read.Features);
            read.Sequence = recorder.Begin(read.Group, read.Batch, ImmutableCollectionsMarshal.AsImmutableArray(read.Fields));
        }

        read.BudgetMs = BudgetMs(read.Group);
        if (read.BudgetMs > 0)
        {
            read.Cancellation = new FetchCancellation(cancellationToken, read.BudgetMs, _events, _requestId);
            read.StoreBudgetMs = Math.Min(read.BudgetMs, read.Supply.DistributedTimeoutMs ?? Math.Max(1, read.BudgetMs / 2));
            var clock = read.Cache.Clock; // the cache's clock times the tier too (a test moves it by hand)
            read.StoreDeadline = clock.GetTimestamp() + (read.StoreBudgetMs * clock.TimestampFrequency / 1000);
        }

        // Tracked, as the context fetch is: it may start an origin call after every call the request has sent so far
        // completed (the request may have stopped reading it, after a failure), so the drain waits for it, not only for
        // the calls it starts (review 78).
        _memory.Hold();
        read.Resolution = ResolveAsync(read, cancellationToken);
        TrackIo(read.Resolution);
    }

    /// <summary>
    /// Resolves the misses of a read: the distributed tier, then the connector for what it did not have fresh, then a
    /// stale record for what the connector failed; then hands what was fetched to the tiers' writers. Touches only the
    /// read's own buffers (it runs while the request's thread does other work) and never throws for an I/O failure.
    /// </summary>
    private async Task ResolveAsync(CacheRead read, CancellationToken cancellationToken)
    {
        try
        {
            if (read.Cancellation is null)
            {
                read.Error = NoTimeLeft.Error;
            }
            else
            {
                if (read.Supply.Distributed is { } tier)
                {
                    await ReadDistributedAsync(read, tier, cancellationToken).ConfigureAwait(false);
                }

                await FetchOriginAsync(read, read.Cancellation, cancellationToken).ConfigureAwait(false);
            }

            Settle(read);
            WriteBack(read);
        }
        finally
        {
            read.Cancellation?.Release(); // the read's own reference; every sent call holds one until it really completes
            _memory.Release();
        }
    }

    /// <summary>Reads the misses from the distributed tier, chunk by chunk, and sorts what it had by freshness.</summary>
    private async ValueTask ReadDistributedAsync(CacheRead read, DistributedCacheTier tier, CancellationToken cancellationToken)
    {
        var chunkSize = read.IsContext ? 1 : read.Supply.MaxBatch;
        var chunks = Batching.Count(read.MissCount, chunkSize);
        var calls = _memory.Rent<ValueTask<long>>(chunks);
        for (var c = 0; c < chunks; c++)
        {
            // A store's synchronous part runs here, unbounded (see IFeatureCacheStore.GetAsync): once one has taken the
            // tier's time, the chunks not sent yet are not sent; their keys go to the connector like a late chunk's.
            var offset = c * chunkSize;
            calls[c] = read.Cache.Clock.GetTimestamp() >= read.StoreDeadline || read.Cancellation!.Token.IsCancellationRequested || cancellationToken.IsCancellationRequested
                ? new ValueTask<long>(ChunkNotAnswered)
                : StoreCall(read, tier, offset, Math.Min(chunkSize, read.MissCount - offset), cancellationToken);
        }

        var cache = read.Cache;
        var layout = cache.Layout;
        int hits = 0, missing = 0, expired = 0, mismatched = 0, failed = 0;
        for (var c = 0; c < chunks; c++)
        {
            var offset = c * chunkSize;
            var count = Math.Min(chunkSize, read.MissCount - offset);
            // Each chunk is judged at the time its call was seen to answer, not when this loop reaches it: a later chunk
            // may answer after a record it carries expired, so the time an earlier chunk was judged at would serve it as
            // fresh (pre-release assessment); and one that answered at once, waiting here behind a slow earlier chunk,
            // would be judged late and its fresh records sent to the connector (review 78).
            var nowMs = await calls[c].ConfigureAwait(false);
            if (nowMs == ChunkNotAnswered)
            {
                failed += count; // its region of Stored may still be written: never read
                continue;
            }

            for (var m = offset; m < offset + count; m++)
            {
                var record = read.Stored[m];
                if (record.IsEmpty)
                {
                    missing++;
                    continue;
                }

                if (CacheRecord.ReadHeader(record.Span, layout.FieldCount, layout.Fingerprint, out var writtenMs, out _) != CacheRecordHeader.Ok
                    || !CacheRecord.IsWellFormed(record.Span, layout))
                {
                    mismatched++; // another catalog version's field set, or not a record: a miss
                    continue;
                }

                switch (cache.Classify(writtenMs, read.KeyHashes[read.MissKey[m]], nowMs))
                {
                    case CacheFreshness.Fresh:
                        read.Sources[m] = CacheSource.Distributed;
                        read.Bytes[m] = record;
                        hits++;
                        break;
                    case CacheFreshness.Stale:
                        read.Sources[m] = CacheSource.StaleCandidate;
                        read.Bytes[m] = record;
                        expired++;
                        break;
                    case CacheFreshness.Expired:
                        expired++;
                        break;
                }
            }
        }

        cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Hit, hits);
        cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Miss, missing);
        cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Expired, expired);
        cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Mismatch, mismatched);
        cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Error, failed);
    }

    /// <summary>
    /// Sends one read of the distributed tier: a chunk of the read's misses, one round trip. The rules of a connector call
    /// (decisions.md, the wait invariants): the request memory is held from before the call until the store really
    /// completes and the call site is done; the wait is bounded by the tier's deadline and the caller's token, never by
    /// the store's token; the call is completed exactly once; what the store writes after the read stopped waiting is
    /// never read. A store that answers synchronously allocates nothing here. The cache clock's time (ms) when the chunk
    /// was seen to answer in time, which its records are judged at; <see cref="ChunkNotAnswered"/> when it did not.
    /// </summary>
    private ValueTask<long> StoreCall(CacheRead read, DistributedCacheTier tier, int offset, int count, CancellationToken cancellationToken)
    {
        var cancellation = read.Cancellation!;
        _memory.Hold();
        var io = read.Supply.Io.Start();
        cancellation.AddCall();
        ValueTask call;
        try
        {
            call = tier.Store.GetAsync(
                read.Cache.Keyspace, read.MissTexts!.AsMemory(offset, count), read.Stored.AsMemory(offset, count), cancellation.Token);
        }
        catch (Exception ex)
        {
            call = ValueTask.FromException(ex); // threw before returning a task: completed, writing no more
        }

        io.Sent(); // the read's next chunk is not a child of this one
        if (call.IsCompleted)
        {
            try
            {
                return new ValueTask<long>(StoreObserved(io, read, count, call, cancellationToken));
            }
            finally
            {
                cancellation.Release();
                _memory.Release();
            }
        }

        return AwaitStoreAsync(io, read, count, call, cancellation, cancellationToken);
    }

    /// <summary>The rest of a store read that did not complete synchronously; the shape of <see cref="AwaitConnectorAsync"/>.</summary>
    private async ValueTask<long> AwaitStoreAsync(
        IoCall io, CacheRead read, int count, ValueTask call, FetchCancellation cancellation, CancellationToken cancellationToken)
    {
        var lease = new IoLease(_memory, cancellation);
        var task = call.AsTask();
        _ = task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observe a late failure nobody awaits any more
                ((IoLease)state!).ConnectorDone();
            },
            lease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        TrackIo(lease.Task); // done once the connector and this call site both are (review 78)

        // The wait decides the outcome; the call is completed once, after it, outside the catch. The outer finally covers
        // the wait, the classification and the completion alike.
        try
        {
            var outcome = IoOutcome.Ok;
            string? error = null;
            var observeNow = false;
            try
            {
                var remaining = read.Cache.Clock.GetElapsedTime(read.Cache.Clock.GetTimestamp(), read.StoreDeadline);
                var wait = task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                observeNow = ReferenceEquals(wait, task); // completed already: no wait decided, so it is observed now
                if (!observeNow)
                {
                    await wait.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                outcome = Classify(ex, cancellationToken);
                error = StoreFailure(read, outcome, ex);
            }

            return observeNow
                ? StoreObserved(io, read, count, new ValueTask(task), cancellationToken)
                : StoreCompleted(io, read, count, outcome, error);
        }
        finally
        {
            lease.CallSiteDone();
        }
    }

    /// <summary>
    /// Settles a store read found completed without an installed wait to decide it (the rule of <see cref="Observed"/>):
    /// it counts only if observed within the tier's bound, that is now; otherwise it is timed out or cancelled and its
    /// records are never read. Returns what <see cref="StoreCompleted"/> does.
    /// </summary>
    private long StoreObserved(in IoCall io, CacheRead read, int count, ValueTask completed, CancellationToken cancellationToken)
    {
        IoOutcome? cutOff = cancellationToken.IsCancellationRequested ? IoOutcome.Cancelled
            : read.Cache.Clock.GetTimestamp() >= read.StoreDeadline || read.Cancellation!.Token.IsCancellationRequested ? IoOutcome.Timeout
            : null;
        var outcome = IoOutcome.Ok;
        string? error = null;
        try
        {
            completed.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            outcome = Classify(ex, cancellationToken);
            error = StoreFailure(read, outcome, ex);
        }

        if (cutOff is { } late)
        {
            outcome = late;
            error = StoreFailure(read, late, null);
        }

        return StoreCompleted(io, read, count, outcome, error);
    }

    /// <summary>
    /// Records a finished store read (metrics, source health, span). Returns the cache clock's time (ms) when it was seen
    /// to answer in time, which its records are judged at, or <see cref="ChunkNotAnswered"/> when its records must not be
    /// read.
    /// </summary>
    /// <remarks>
    /// The time is taken as soon as the outcome is known, before <see cref="IoCall.Complete"/> calls the host's listeners:
    /// one that took time, or moved the cache's clock, made a record fresh when the store answered look expired, and sent
    /// its key to the connector (review 80).
    /// </remarks>
    private long StoreCompleted(in IoCall io, CacheRead read, int count, IoOutcome outcome, string? error)
    {
        var answeredAt = outcome == IoOutcome.Ok ? read.Cache.NowMs() : ChunkNotAnswered;
        io.Complete(outcome, count, _requestId, error, _events);
        return answeredAt;
    }

    private static string StoreFailure(CacheRead read, IoOutcome outcome, Exception? ex) => outcome switch
    {
        IoOutcome.Timeout => $"distributed cache of source '{read.Group.SourceName}' timed out after {read.StoreBudgetMs} ms",
        IoOutcome.Cancelled => $"distributed cache of source '{read.Group.SourceName}' was cancelled",
        _ => $"distributed cache of source '{read.Group.SourceName}' failed: {(ex is null ? "(no exception)" : ExceptionText.Of(ex))}",
    };

    /// <summary>
    /// Fetches what neither tier had fresh from the connector, in <c>max_batch</c> chunks through the fetch path's call
    /// (not recorded on their own: the read is recorded as one call), and encodes each answered key's record.
    /// </summary>
    private async ValueTask FetchOriginAsync(CacheRead read, FetchCancellation cancellation, CancellationToken cancellationToken)
    {
        var count = 0;
        for (var m = 0; m < read.MissCount; m++)
        {
            count += read.Sources[m] is CacheSource.Pending or CacheSource.StaleCandidate ? 1 : 0;
        }

        if (count == 0)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() >= cancellation.DeadlineTimestamp)
        {
            read.Error ??= NoTimeLeft.Error; // the distributed tier took the whole budget
            return;
        }

        var group = read.Group;
        var missOf = _memory.Rent<int>(count);
        var keyOf = _memory.Rent<int>(count);
        var n = 0;
        for (var m = 0; m < read.MissCount; m++)
        {
            if (read.Sources[m] is CacheSource.Pending or CacheSource.StaleCandidate)
            {
                keyOf[n] = read.MissKey[m];
                missOf[n++] = m;
            }
        }

        var layout = group.KeyLayout;
        var keys = read.KeyBatch.Select(keyOf.AsSpan(0, count), _memory.Rent<EntityKey>((layout.VaryingCount * count) + layout.PartCount - layout.VaryingCount));

        var chunkSize = read.IsContext ? 1 : read.Supply.MaxBatch;
        var chunks = Batching.Count(count, chunkSize);
        var staging = new StagingArea(group.Fields.AsSpan(), count, chunkSize, _memory);
        var calls = _memory.Rent<ValueTask<FetchStatus>>(chunks);
        var writtenMs = read.Cache.NowMs(); // the version of what this fetch writes back: when it started
        StagingBuffer? writer = null;
        for (var c = 0; c < chunks; c++)
        {
            // As a plain fetch's (FetchCandidates): once the time is up or the caller has cancelled, the chunks not sent
            // yet are not sent; their keys stay unanswered.
            if (CutOff(cancellation, cancellationToken) is { } cut)
            {
                calls[c] = new ValueTask<FetchStatus>(cut == IoOutcome.Cancelled ? Cancelled(group) : TimedOut(group, read.BudgetMs));
                continue;
            }

            var offset = c * chunkSize;
            var batch = keys.Slice(offset, Math.Min(chunkSize, count - offset));
            writer ??= new StagingBuffer(staging);
            writer.Bind(c);
            calls[c] = ConnectorCall(group, batch, group.FieldArray, writer, read.BudgetMs, NotRecorded, cancellation, cancellationToken, out var writerHeld);
            if (writerHeld)
            {
                writer = null; // the call runs on and may write through this view until it really completes
            }
        }

        var records = new CacheRecordWriter(read.Cache.Layout, count * read.Cache.Layout.TypicalSize);
        var spans = _memory.Rent<(int Offset, int Length)>(count);
        for (var c = 0; c < chunks; c++)
        {
            var status = await calls[c].ConfigureAwait(false);
            if (status.Outcome == FetchOutcome.Failed)
            {
                read.Error ??= status.Error; // its keys stay unanswered; its staging is never read
                continue;
            }

            var partial = status.Outcome == FetchOutcome.Partial;
            if (partial)
            {
                read.Partial = true;
                read.Error ??= status.Error;
            }

            var offset = c * chunkSize;
            for (var k = offset; k < Math.Min(offset + chunkSize, count); k++)
            {
                spans[k] = EncodeStaged(staging, k, read, records, writtenMs);
                read.Sources[missOf[k]] = partial ? CacheSource.OriginPartial : CacheSource.Origin;
            }
        }

        var bytes = records.Bytes; // complete now: the array is replaced while it grows

        // The records written back hold their array for as long as they are queued, and the queues count record bytes.
        // When they are less than half of it (chunks that failed or answered in part, room the misses did not use), they
        // get an array of their own, so a queue's byte limit bounds the memory it holds.
        var kept = 0;
        for (var k = 0; k < count; k++)
        {
            kept += read.Sources[missOf[k]] == CacheSource.Origin ? spans[k].Length : 0;
        }

        var own = kept > 0 && kept < bytes.Length / 2 ? new byte[kept] : null;
        var at = 0;
        for (var k = 0; k < count; k++)
        {
            var source = read.Sources[missOf[k]];
            if (source is not (CacheSource.Origin or CacheSource.OriginPartial))
            {
                continue;
            }

            var record = bytes.AsMemory(spans[k].Offset, spans[k].Length);
            if (own is not null && source == CacheSource.Origin)
            {
                record.CopyTo(own.AsMemory(at));
                record = own.AsMemory(at, record.Length);
                at += record.Length;
            }

            read.Bytes[missOf[k]] = record;
        }
    }

    /// <summary>
    /// Encodes what the connector wrote for fetch key <paramref name="key"/> as the key's record: each table field from
    /// the first group member holding it, a field the connector did not write as missing, and a key it wrote nothing for
    /// as an absent record.
    /// </summary>
    private static (int Offset, int Length) EncodeStaged(StagingArea staging, int key, CacheRead read, CacheRecordWriter writer, long writtenMs)
    {
        var layout = read.Cache.Layout;
        var members = read.Supply.MemberOfTableField;
        var any = false;
        for (var t = 0; t < layout.FieldCount && !any; t++)
        {
            any = members[t] >= 0 && staging.HasValue(members[t], key);
        }

        writer.Begin(writtenMs, layout.Fingerprint, absent: !any);
        var chunk = key / staging.ChunkSize;
        var inChunk = key - (chunk * staging.ChunkSize);
        for (var t = 0; t < layout.FieldCount; t++)
        {
            var m = members[t];
            if (m < 0 || !staging.HasValue(m, key))
            {
                continue;
            }

            switch (layout.Kinds[t])
            {
                case StorageKind.Bool: writer.Bool(t, ((ScalarColumn<bool>)staging.ColumnOf(m)!)[key]); break;
                case StorageKind.Int32: writer.Int32(t, ((ScalarColumn<int>)staging.ColumnOf(m)!)[key]); break;
                case StorageKind.Int64: writer.Int64(t, ((ScalarColumn<long>)staging.ColumnOf(m)!)[key]); break;
                case StorageKind.Float32: writer.Float32(t, ((ScalarColumn<float>)staging.ColumnOf(m)!)[key]); break;
                case StorageKind.Float64: writer.Float64(t, ((ScalarColumn<double>)staging.ColumnOf(m)!)[key]); break;
                case StorageKind.String: writer.String(t, ((StringColumn)staging.ColumnOf(m)!).Get(key)); break;
                case StorageKind.List: writer.List(t, (ListColumn)staging.ListBuffer(m, chunk)!, inChunk); break;
                default: throw new InvalidOperationException($"Unsupported field kind {layout.Kinds[t]}.");
            }
        }

        return writer.End();
    }

    /// <summary>
    /// What became of each miss once the connector has answered or failed (design 6.6 <c>cache</c> item 4): a key the
    /// connector did not answer is served stale if a tier has a record within <c>stale_if_error_ms</c> (the newer one),
    /// and fails otherwise.
    /// </summary>
    /// <remarks>
    /// The window is judged now, when the fallback is chosen: the candidates were classified when the read started (the
    /// table's) and when the distributed tier answered, and the connector's wait in between may have taken a record past
    /// its window. Such a record is not served.
    /// </remarks>
    private static void Settle(CacheRead read)
    {
        int staleLocal = 0, staleDistributed = 0;
        var nowMs = read.Cache.NowMs();
        for (var m = 0; m < read.MissCount; m++)
        {
            switch (read.Sources[m])
            {
                case CacheSource.Pending or CacheSource.StaleCandidate:
                    var key = read.KeyHashes[read.MissKey[m]];
                    var localMs = read.LocalStale[m] ? read.Version.VersionOf(read.Records[read.MissKey[m]]) : 0;
                    var local = read.LocalStale[m] && read.Cache.Classify(localMs, key, nowMs) != CacheFreshness.Expired;
                    var sharedMs = read.Sources[m] == CacheSource.StaleCandidate ? CacheRecord.WrittenMs(read.Bytes[m].Span) : 0;
                    var shared = read.Sources[m] == CacheSource.StaleCandidate && read.Cache.Classify(sharedMs, key, nowMs) != CacheFreshness.Expired;
                    if (shared && !(local && localMs > sharedMs))
                    {
                        read.Sources[m] = CacheSource.StaleDistributed;
                        staleDistributed++;
                        break;
                    }

                    read.Bytes[m] = default;
                    if (local)
                    {
                        read.Sources[m] = CacheSource.StaleLocal;
                        staleLocal++;
                    }
                    else
                    {
                        read.Sources[m] = CacheSource.Failed;
                        read.FailedKeys++;
                    }

                    break;
                case CacheSource.Distributed or CacheSource.Origin or CacheSource.OriginPartial:
                    break;
                case CacheSource.StaleDistributed or CacheSource.StaleLocal or CacheSource.Failed:
                    throw new InvalidOperationException("A cache read is settled once.");
            }
        }

        read.StaleKeys = staleLocal + staleDistributed;
        read.Cache.CountKeys(CacheTier.Local, CacheKeyOutcome.Stale, staleLocal);
        read.Cache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Stale, staleDistributed);
    }

    /// <summary>
    /// Hands what the read fetched to the tiers' writers, without waiting: the distributed tier's fresh records to the
    /// table, the connector's records (of chunks that answered in full and in time) to the table and the distributed
    /// tier. The arrays are the writers' from here on, so they are not the request's.
    /// </summary>
    private static void WriteBack(CacheRead read)
    {
        int local = 0, shared = 0;
        for (var m = 0; m < read.MissCount; m++)
        {
            local += read.Sources[m] is CacheSource.Distributed or CacheSource.Origin ? 1 : 0;
            shared += read.Sources[m] == CacheSource.Origin ? 1 : 0;
        }

        if (local == 0)
        {
            return;
        }

        var cache = read.Cache;
        var parts = read.KeyBatch.PartCount;
        var keys = new EntityKey[local * parts];
        var records = new ReadOnlyMemory<byte>[local];
        var tier = read.Supply.Distributed;
        var entries = tier is null || shared == 0 ? null : new CacheStoreEntry[shared];
        var nowMs = cache.NowMs();
        int l = 0, s = 0;
        for (var m = 0; m < read.MissCount; m++)
        {
            if (read.Sources[m] is not (CacheSource.Distributed or CacheSource.Origin))
            {
                continue;
            }

            for (var p = 0; p < parts; p++)
            {
                keys[(l * parts) + p] = read.KeyBatch.Key(read.MissKey[m], p);
            }

            records[l++] = read.Bytes[m];
            if (entries is not null && read.Sources[m] == CacheSource.Origin)
            {
                entries[s++] = new CacheStoreEntry(read.MissTexts![m], read.Bytes[m], cache.StoreTimeToLive(CacheRecord.WrittenMs(read.Bytes[m].Span), nowMs));
            }
        }

        cache.TryWriteBack(new CacheWriteBatch(keys, records, local, parts));
        if (entries is not null)
        {
            tier!.TryWrite(cache, entries, shared);
        }
    }

    /// <summary>
    /// Completes a candidate cache read on the request's thread once its misses have resolved: records it, then fills the
    /// rows of its misses and sets the features' states by the rule of a fetch (<see cref="SettleKey"/>).
    /// </summary>
    private async ValueTask CompleteCacheReadAsync(CacheRead read)
    {
        await read.Resolution.ConfigureAwait(false);
        var status = read.Status();
        RecordCacheRead(read, status);
        if (CountsValues)
        {
            CountResolvedValues(read);
        }

        var degraded = status.Outcome != FetchOutcome.Ok;
        var error = status.Error;
        if (!SettleKey(read.Group, read.Features, status.Outcome == FetchOutcome.Failed, ref degraded, ref error))
        {
            return;
        }

        FillMisses(read);
        foreach (var f in read.Features)
        {
            if (_state[f] != FeatureState.Failed)
            {
                SetFetchedState(f, degraded, error);
            }
        }
    }

    /// <summary>Completes a context cache read (from the context prefetch) by the rule of a fetched context group: the
    /// prefetch's task for its group, which readers of its features wait for.</summary>
    private async Task CompleteCachedContextAsync(CacheRead read)
    {
        await read.Resolution.ConfigureAwait(false);
        var status = read.Status();
        RecordCacheRead(read, status);
        if (CountsValues)
        {
            CountResolvedValues(read);
        }

        var partial = status.Outcome == FetchOutcome.Partial;
        foreach (var f in read.Features)
        {
            if (status.Outcome == FetchOutcome.Failed)
            {
                _state[f] = FeatureState.Failed;
                _errors[f] = status.Error;
                continue;
            }

            var info = _plan.Features[f];
            _state[f] = partial ? FeatureState.Degraded : FeatureState.Ok;
            _errors[f] = partial ? status.Error : null;
            StoreContext(info, read.ValueOf(read.Group.Supply.Table!.TableFields[info.FieldIndex], 0));
        }
    }

    /// <summary>
    /// Counts what the source had of the values a read with misses asked for (<see cref="StrataRecMetrics.SourceValues"/>),
    /// once it has resolved: each key the read resolved, once per read feature — fresh in the table, or answered by the
    /// distributed tier or the connector — present where its record has the field. A key served stale, or not at all,
    /// was not resolved: the connector failed for it, which <see cref="StrataRecMetrics.SourceErrors"/> counts.
    /// </summary>
    private void CountResolvedValues(CacheRead read)
    {
        var tableFields = read.Group.Supply.Table!.TableFields;
        foreach (var f in read.Features)
        {
            var info = _plan.Features[f];
            var field = tableFields[info.FieldIndex];
            int present = 0, resolved = 0;
            for (var k = 0; k < read.KeyCount; k++)
            {
                var m = read.MissOf[k];
                if (m < 0)
                {
                    resolved++;
                    present += read.Version.HasValue(field, read.Records[k]) ? 1 : 0;
                }
                else if (read.Sources[m] is CacheSource.Distributed or CacheSource.Origin or CacheSource.OriginPartial)
                {
                    resolved++;
                    present += CacheRecord.HasValue(read.Bytes[m].Span, field) ? 1 : 0;
                }
            }

            CountValues(info, present, resolved - present);
        }
    }

    /// <summary>Records the read as the one feature call a replay answers it with.</summary>
    private void RecordCacheRead(CacheRead read, in FetchStatus status)
    {
        if (_hook is not { Replays: false } recorder)
        {
            return;
        }

        var outcome = status.Outcome switch
        {
            FetchOutcome.Ok => IoOutcome.Ok,
            FetchOutcome.Partial => IoOutcome.Partial,
            FetchOutcome.Failed => IoOutcome.Error,
        };
        var tableFields = TableFields(read.Group, read.Features);
        var fields = ImmutableCollectionsMarshal.AsImmutableArray(read.Fields!);
        recorder.Record(
            new FeatureCallInfo(read.Group, read.Batch, fields, read.Sequence, true, status, outcome, Stopwatch.GetElapsedTime(read.Started)),
            outcome == IoOutcome.Error ? null : new CacheReadValues(read, tableFields));
    }

    /// <summary>
    /// Fills the rows of the read's misses with what each miss resolved to: a record's bytes (decoded into the slots), the
    /// table's stale record (one multi-field fill), or the absent value. A feature already filled on a row keeps its value.
    /// </summary>
    private void FillMisses(CacheRead read)
    {
        if (read.RowCount == 0)
        {
            return;
        }

        var group = read.Group;
        var layout = read.Cache.Layout;
        var tableFields = group.Supply.Table!.TableFields;

        // Scratch, given back before this returns: the fill writes into the table's slots and keeps nothing.
        var starts = _memory.RentScratch<int>(layout.FieldCount + 1);
        var targets = _memory.RentScratch<CacheTarget>(read.Features.Length);
        var next = _memory.RentScratch<int>(layout.FieldCount);
        var staleRows = _memory.RentScratch<int>(read.RowCount);
        var staleRecords = _memory.RentScratch<LocalRecordRef>(read.RowCount);
        try
        {
            // The slots each table field fills, grouped by field (two features may read one field).
            Array.Clear(starts, 0, layout.FieldCount + 1);
            foreach (var f in read.Features)
            {
                starts[tableFields[_plan.Features[f].FieldIndex] + 1]++;
            }

            for (var t = 0; t < layout.FieldCount; t++)
            {
                starts[t + 1] += starts[t];
            }

            starts.AsSpan(0, layout.FieldCount).CopyTo(next);
            foreach (var f in read.Features)
            {
                var info = _plan.Features[f];
                targets[next[tableFields[info.FieldIndex]]++] = new CacheTarget(info, _table.GetOrCreateSlot(info.Slot));
            }

            var stale = 0;
            var fill = new ColumnFill(this, targets, starts, read.Strings);
            for (var i = 0; i < read.RowCount; i++)
            {
                var m = read.RowMiss[i];
                fill.Row = read.Rows[i];
                switch (read.Sources[m])
                {
                    case CacheSource.Distributed or CacheSource.Origin or CacheSource.OriginPartial or CacheSource.StaleDistributed:
                        CacheRecord.Read(read.Bytes[m].Span, layout, ref fill); // checked well formed (or encoded here) already
                        break;
                    case CacheSource.StaleLocal:
                        staleRows[stale] = read.Rows[i];
                        staleRecords[stale++] = read.Records[read.MissKey[m]];
                        break;
                    case CacheSource.Failed:
                        fill.AllMissing(layout.FieldCount);
                        break;
                    case CacheSource.Pending or CacheSource.StaleCandidate:
                        throw new InvalidOperationException("A cache read is filled after it settled.");
                }
            }

            if (stale > 0)
            {
                Fill(group, read.Features, read.Version, staleRows.AsSpan(0, stale), staleRecords.AsSpan(0, stale));
            }
        }
        finally
        {
            _memory.ReturnScratch(starts);
            _memory.ReturnScratch(targets);
            _memory.ReturnScratch(next);
            _memory.ReturnScratch(staleRows);
            _memory.ReturnScratch(staleRecords);
        }
    }

    /// <summary>Where a miss's value came from (and so where a fill and a recording read it).</summary>
    private enum CacheSource : byte
    {
        /// <summary>Not answered (yet); a failure if it stays so.</summary>
        Pending = 0,

        /// <summary>The distributed tier had a stale record (in <see cref="CacheRead.Bytes"/>); the connector is asked.</summary>
        StaleCandidate,

        /// <summary>A fresh record from the distributed tier.</summary>
        Distributed,

        /// <summary>The connector's answer, of a chunk that answered in full: written back.</summary>
        Origin,

        /// <summary>The connector's answer, of a chunk that answered in part: served, not written back.</summary>
        OriginPartial,

        /// <summary>The connector failed; the distributed tier's stale record is served.</summary>
        StaleDistributed,

        /// <summary>The connector failed; the table's stale record is served.</summary>
        StaleLocal,

        /// <summary>The connector failed and no tier has a usable record: the absent value.</summary>
        Failed,
    }

    /// <summary>A slot a cache read fills, and the feature it holds.</summary>
    private readonly record struct CacheTarget(FeatureRuntimeInfo Info, ColumnBuffer Column);

    /// <summary>
    /// One cache read that missed: its keys and what each resolved to, the rows waiting for them, and its budget. The
    /// arrays are rented from the request memory, which the read holds until it has resolved.
    /// </summary>
    private sealed class CacheRead
    {
        public CacheRead(
            FetchGroup group, int[] features, TableVersion version, in EntityKeyBatch keys, ulong[] keyHashes, int missCount, int rowCapacity, bool isContext,
            RequestMemory memory)
        {
            Group = group;
            Cache = group.Supply.Table!.Table.Cache!;
            Supply = group.Supply.Cache!;
            Features = features;
            Version = version;
            KeyBatch = keys;
            KeyHashes = keyHashes;
            KeyCount = keys.Count;
            MissCount = missCount;
            IsContext = isContext;
            _memory = memory;
            Records = memory.Rent<LocalRecordRef>(KeyCount);
            MissOf = memory.Rent<int>(KeyCount);
            MissKey = memory.Rent<int>(missCount);
            LocalStale = memory.Rent<bool>(missCount);
            Sources = memory.RentCleared<CacheSource>(missCount);
            Bytes = memory.RentCleared<ReadOnlyMemory<byte>>(missCount);
            Stored = memory.RentCleared<ReadOnlyMemory<byte>>(missCount);
            Rows = memory.Rent<int>(Math.Max(1, rowCapacity));
            RowMiss = memory.Rent<int>(Math.Max(1, rowCapacity));
        }

        public FetchGroup Group { get; }

        public LocalTableCache Cache { get; }

        public CacheSupply Supply { get; }

        /// <summary>The features the read fills (staging field i of its recording is <c>Features[i]</c>).</summary>
        public int[] Features { get; }

        public TableVersion Version { get; }

        public bool IsContext { get; }

        private readonly RequestMemory _memory;

        /// <summary>The read's distinct keys, in first-seen order (a fetch of the same rows takes the same).</summary>
        public int KeyCount { get; }

        /// <summary>The read's keys, as the recorded call takes them.</summary>
        public EntityKeyBatch KeyBatch { get; }

        /// <summary>Per key, its canonical hash (<see cref="LocalTableCache.KeyHash(in EntityKeyBatch, int)"/>): its lifetime's jitter.</summary>
        public ulong[] KeyHashes { get; }

        /// <summary>Per key, its table record in <see cref="Version"/>, or none.</summary>
        public LocalRecordRef[] Records { get; }

        /// <summary>Per key, its miss, or -1 for a fresh key.</summary>
        public int[] MissOf { get; }

        public int MissCount { get; }

        /// <summary>Per miss, its key's text for the distributed tier (<see cref="CacheKeyspace.KeyText"/>); null without a tier.</summary>
        public string[]? MissTexts { get; private set; }

        /// <summary>Per miss, its key.</summary>
        public int[] MissKey { get; }

        /// <summary>Per miss, whether its table record is past its lifetime but within <c>stale_if_error_ms</c>.</summary>
        public bool[] LocalStale { get; }

        public CacheSource[] Sources { get; }

        /// <summary>Per miss, the record it resolved to (see <see cref="Sources"/>).</summary>
        public ReadOnlyMemory<byte>[] Bytes { get; }

        /// <summary>Per miss, what the distributed tier answered: written by the store, read only for a chunk answered in time.</summary>
        public ReadOnlyMemory<byte>[] Stored { get; }

        /// <summary>The candidate rows waiting for a miss, and each one's miss.</summary>
        public int[] Rows { get; }

        public int[] RowMiss { get; }

        public int RowCount { get; private set; }

        public int BudgetMs { get; set; }

        public int StoreBudgetMs { get; set; }

        /// <summary>
        /// When the read stops waiting for the distributed tier, as a timestamp of the cache's clock
        /// (<see cref="LocalTableCache.Clock"/>; the system's is the <see cref="Stopwatch"/>).
        /// </summary>
        public long StoreDeadline { get; set; }

        /// <summary>The read's cancellation and deadline, for both tiers; null when it had no time left to send anything.</summary>
        public FetchCancellation? Cancellation { get; set; }

        /// <summary>The resolution of the misses (<see cref="ResolveAsync"/>); also tracked by the request's I/O drain.</summary>
        public Task Resolution { get; set; } = Task.CompletedTask;

        public long Started { get; set; }

        public int Sequence { get; set; }

        public CompiledFieldBinding[]? Fields { get; set; }

        public string? Error { get; set; }

        public bool Partial { get; set; }

        public int FailedKeys { get; set; }

        public int StaleKeys { get; set; }

        public CacheRead? Next { get; set; }

        /// <summary>
        /// The strings decoded from the read's record bytes, deduplicated across its keys (a category repeated across them
        /// is one string); made by the first one. A read is filled, recorded and read for its context values on one
        /// thread at a time, once it has resolved.
        /// </summary>
        public StringDedup Strings => _strings ??= new StringDedup();

        private StringDedup? _strings;

        /// <summary>The recorded call's keys.</summary>
        public EntityKeyBatch Batch => KeyBatch;

        /// <summary>
        /// Once the misses are known (<see cref="MissKey"/>): each one's key text, when the read has a distributed tier to
        /// ask. Computed once per miss, for the read and the write-back.
        /// </summary>
        public void SelectMisses()
        {
            if (Supply.Distributed is null || MissCount == 0)
            {
                return;
            }

            var texts = _memory.Rent<string>(MissCount);
            var partsBuffer = default(KeyPartBuffer);
            Span<EntityKey> parts = partsBuffer;
            parts = parts[..KeyBatch.PartCount];
            for (var m = 0; m < MissCount; m++)
            {
                for (var p = 0; p < parts.Length; p++)
                {
                    parts[p] = KeyBatch.Key(MissKey[m], p);
                }

                texts[m] = CacheKeyspace.KeyText(parts);
            }

            MissTexts = texts;
        }

        public void AddRow(int row, int miss)
        {
            Rows[RowCount] = row;
            RowMiss[RowCount++] = miss;
        }

        /// <summary>
        /// How the read ended, as a fetch would report it: failed when no key got a value, partial (degraded) when some
        /// key failed, was served stale, or came from a partial answer.
        /// </summary>
        public FetchStatus Status()
        {
            if (FailedKeys == 0 && StaleKeys == 0 && !Partial)
            {
                return FetchStatus.Ok;
            }

            var error = Error ?? $"source '{Group.SourceName}' answered in part";
            if (StaleKeys > 0)
            {
                error += $"; {StaleKeys} key(s) served stale from the cache (stale_if_error_ms)";
            }

            return FailedKeys == KeyCount ? FetchStatus.Failed(error) : new FetchStatus(FetchOutcome.Partial, FailedKeys, error);
        }

        /// <summary>The value the read served for (table field, key), as stored (non-finite values too); for recordings and context reads.</summary>
        public ScalarValue ValueOf(int tableField, int key)
        {
            var m = MissOf[key];
            if (m < 0)
            {
                return Version.Read(tableField, Records[key]);
            }

            switch (Sources[m])
            {
                case CacheSource.Distributed or CacheSource.Origin or CacheSource.OriginPartial or CacheSource.StaleDistributed:
                    var value = new FieldValue(tableField, Strings);
                    CacheRecord.Read(Bytes[m].Span, Cache.Layout, ref value);
                    return value.Value;
                case CacheSource.StaleLocal:
                    return Version.Read(tableField, Records[key]);
                case CacheSource.Failed:
                    return ScalarValue.Missing;
                case CacheSource.Pending or CacheSource.StaleCandidate:
                default:
                    throw new InvalidOperationException("A cache read is read after it settled.");
            }
        }
    }

    /// <summary>What a recorded cache read served: field i is <c>tableFields[i]</c>.</summary>
    private sealed class CacheReadValues(CacheRead read, int[] tableFields) : IStagedFeatureValues
    {
        public ScalarValue Get(int fieldIndex, int keyIndex) => read.ValueOf(tableFields[fieldIndex], keyIndex);
    }

    /// <summary>
    /// Writes one record's fields into the slots of a row (<see cref="CacheRecord.Read"/> walks the record once for all of
    /// them). Like a backfill: a non-finite float is counted and takes the absent value, a missing field takes it too, and a
    /// slot already filled on the row is left as it is. Strings are decoded through the read's deduplication, so a value
    /// repeated across the read's keys is one string.
    /// </summary>
    private struct ColumnFill : ICacheRecordVisitor
    {
        private readonly FeatureFetcher _owner;
        private readonly CacheTarget[] _targets;
        private readonly int[] _starts; // the targets of table field t are [_starts[t], _starts[t + 1])
        private readonly StringDedup _strings;
        private string[]? _items;
        private int _itemCount;

        public ColumnFill(FeatureFetcher owner, CacheTarget[] targets, int[] starts, StringDedup strings)
        {
            _owner = owner;
            _targets = targets;
            _starts = starts;
            _strings = strings;
        }

        public int Row { get; set; }

        public readonly void AllMissing(int fieldCount)
        {
            for (var t = 0; t < fieldCount; t++)
            {
                Missing(t);
            }
        }

        public readonly void Missing(int field)
        {
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (Take(i))
                {
                    ColumnValues.Set(_targets[i].Column, Row, _targets[i].Info.Definition.Absent);
                }
            }
        }

        public readonly void Bool(int field, bool value) => Scalar(field, value);

        public readonly void Int32(int field, int value) => Scalar(field, value);

        public readonly void Int64(int field, long value) => Scalar(field, value);

        public readonly void Float32(int field, float value)
        {
            if (float.IsFinite(value))
            {
                Scalar(field, value);
            }
            else
            {
                NonFinite(field);
            }
        }

        public readonly void Float64(int field, double value)
        {
            if (double.IsFinite(value))
            {
                Scalar(field, value);
            }
            else
            {
                NonFinite(field);
            }
        }

        public readonly void String(int field, ReadOnlySpan<byte> utf8)
        {
            string? value = null;
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (!Take(i))
                {
                    continue;
                }

                value ??= _strings.InternUtf8(utf8);
                if (_targets[i].Column is not StringColumn strings)
                {
                    throw new InvalidOperationException($"A string field fills a {_targets[i].Column.Type} slot.");
                }

                strings.Set(Row, value);
            }
        }

        public readonly void List<T>(int field, ReadOnlySpan<T> value)
            where T : unmanaged
        {
            // A float list holding NaN or ±inf is replaced as a whole, as a float is (design 4.3).
            var finite = typeof(T) == typeof(float) ? ColumnValues.AllFinite(MemoryMarshal.Cast<T, float>(value))
                : typeof(T) != typeof(double) || ColumnValues.AllFinite(MemoryMarshal.Cast<T, double>(value));
            ListValue(field, value, finite);
        }

        public void StringListStart(int field, int count)
        {
            _items = ArrayPool<string>.Shared.Rent(Math.Max(1, count));
            _itemCount = 0;
        }

        public void StringListItem(int field, ReadOnlySpan<byte> utf8) => _items![_itemCount++] = _strings.InternUtf8(utf8);

        public void StringListEnd(int field)
        {
            var items = _items!;
            ListValue<string>(field, items.AsSpan(0, _itemCount), finite: true);
            items.AsSpan(0, _itemCount).Clear();
            ArrayPool<string>.Shared.Return(items);
            _items = null;
        }

        private readonly void Scalar<T>(int field, T value)
            where T : unmanaged
        {
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (Take(i))
                {
                    ((ScalarColumn<T>)_targets[i].Column).Set(Row, value);
                }
            }
        }

        /// <summary>A list into each target as the target's type takes it: a list of another length than a
        /// <c>list&lt;E,N&gt;</c> target's takes the absent value and is counted out of range, as backfill does with a
        /// connector's answer (the record holds what the connector wrote); a float list holding NaN or ±∞ is counted non-finite.</summary>
        private readonly void ListValue<T>(int field, ReadOnlySpan<T> value, bool finite)
        {
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (!Take(i))
                {
                    continue;
                }

                var target = _targets[i];
                if (!ColumnValues.FitsLength(value.Length, target.Column.Type))
                {
                    ColumnValues.Set(target.Column, Row, target.Info.Definition.Absent);
                    _owner._counters.AddOutOfRange(1);
                }
                else if (!finite)
                {
                    ColumnValues.Set(target.Column, Row, target.Info.Definition.Absent);
                    _owner.AddNonFinite(target.Info, 1);
                }
                else
                {
                    ((ListColumn<T>)target.Column).Set(Row, value);
                }
            }
        }

        private readonly void NonFinite(int field)
        {
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (Take(i))
                {
                    ColumnValues.Set(_targets[i].Column, Row, _targets[i].Info.Definition.Absent);
                    _owner.AddNonFinite(_targets[i].Info, 1);
                }
            }
        }

        /// <summary>Marks target <paramref name="i"/> filled on the row; false when it already was (the value stays).</summary>
        private readonly bool Take(int i)
        {
            var filled = _owner._table.GetFilled(_targets[i].Info.Slot);
            if (Bits.Get(filled, Row))
            {
                return false;
            }

            Bits.Set(filled, Row);
            return true;
        }
    }

    /// <summary>Reads one field of a record as a promoted scalar, as <see cref="TableVersion.Read"/> does for a table record;
    /// strings through the read's deduplication.</summary>
    private struct FieldValue(int field, StringDedup strings) : ICacheRecordVisitor
    {
        private string[]? _items;
        private int _itemCount;

        public ScalarValue Value { get; private set; } = ScalarValue.Missing;

        public readonly void Missing(int f)
        {
        }

        public void Bool(int f, bool value) => Set(f, ScalarValue.FromBool(value));

        public void Int32(int f, int value) => Set(f, ScalarValue.FromInt64(value));

        public void Int64(int f, long value) => Set(f, ScalarValue.FromInt64(value));

        public void Float32(int f, float value) => Set(f, ScalarValue.FromFloat32(value));

        public void Float64(int f, double value) => Set(f, ScalarValue.FromFloat64(value));

        public void String(int f, ReadOnlySpan<byte> utf8)
        {
            if (f == field)
            {
                Value = ScalarValue.FromString(strings.InternUtf8(utf8));
            }
        }

        public void List<T>(int f, ReadOnlySpan<T> value)
            where T : unmanaged
        {
            if (f == field)
            {
                Value = ColumnValues.ListValue(value);
            }
        }

        public void StringListStart(int f, int count)
        {
            // The value's own array, filled in place: the record says how many items there are.
            _items = f == field ? new string[count] : null;
            _itemCount = 0;
        }

        public void StringListItem(int f, ReadOnlySpan<byte> utf8)
        {
            if (_items is not null)
            {
                _items[_itemCount++] = strings.InternUtf8(utf8);
            }
        }

        public void StringListEnd(int f)
        {
            if (_items is not null)
            {
                Value = ScalarValue.FromStringList(_items);
                _items = null;
            }
        }

        private void Set(int f, in ScalarValue value)
        {
            if (f == field)
            {
                Value = value;
            }
        }
    }
}
