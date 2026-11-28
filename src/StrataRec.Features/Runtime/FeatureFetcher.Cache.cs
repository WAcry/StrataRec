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
using StrataRec.Core.Threading;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The request read path of cache groups: everything a request does with a cached source is here.
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
/// <para><b>Reads of one request</b>. A context group's read is of one record, which the runs of a request often all read
/// (a user's features): a read neither tier answered joins the request's read of that record from the connector
/// (<see cref="RequestReads"/>). The first asks the connector as above; the others wait for what it gets within their
/// own budget, take its record when it is fresh by their own times, and ask the connector themselves otherwise.
/// Candidate reads, of many keys, are not joined.</para>
/// <para><b>Replay</b>. A recording request records each cache read as one feature call: its keys (the
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
        var read = FindCacheMisses(group, features, cache, version, batch, nowMs);
        if (read is null)
        {
            // Every key fresh (or no row has a key): a local read, recorded before its metrics (ReadLocal).
            Fill(group, features, version, batch.Rows, batch.RowRecords);
            if (keyCount > 0 && _hook is { Replays: false } recorder)
            {
                RecordRead(recorder, group, features, version, batch, started);
            }

            cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Hit, keyCount);
            if (keyCount > 0 && CountsValues)
            {
                CountTableValues(group, features, version, batch.KeyRecords);
            }

            SetReadState(group, features);
            return;
        }

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

    private CacheRead? FindCacheMisses(
        FetchGroup group, ReadOnlySpan<int> features, LocalTableCache cache, TableVersion version, in LocalReadBatch batch, long nowMs)
    {
        var records = batch.KeyRecords;
        // Before the earliest possible expiry, the write time alone proves freshness. In a fresh batch this avoids
        // materializing the distinct keys and hashing every part of every key just to reproduce its TTL jitter.
        var k = 0;
        if (batch.Found == records.Length)
        {
            var freshSince = nowMs - group.Supply.Cache!.Policy.MinLifetimeMs;
            while (k < records.Length && version.VersionOf(records[k]) >= freshSince)
            {
                records[k].MarkCacheHit();
                k++;
            }

            if (k == records.Length)
            {
                return null;
            }
        }

        var keys = batch.Keys.Batch(_memory);
        var policy = group.Supply.Cache!.Policy;
        CacheMiss[]? misses = null;
        var count = 0;
        for (; k < records.Length; k++)
        {
            var record = records[k];
            var freshness = record.Found ? policy.Classify(version.VersionOf(record), keys, k, nowMs) : CacheFreshness.Expired;
            if (freshness == CacheFreshness.Fresh)
            {
                record.MarkCacheHit();
            }
            else
            {
                misses ??= _memory.Rent<CacheMiss>(records.Length - k);
                misses[count++] = new CacheMiss(k, freshness == CacheFreshness.Stale);
            }
        }

        return misses is null ? null : new CacheRead(group, features.ToArray(), version, keys, records, misses, count, batch.Rows.Length, _memory);
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
        var freshness = record.Found ? group.Supply.Cache!.Policy.Classify(version.VersionOf(record), key, 0, nowMs) : CacheFreshness.Expired;
        if (freshness == CacheFreshness.Fresh)
        {
            record.MarkCacheHit();
            StoreLocalContext(group, features, version, key, record, started); // recorded before its metrics (ReadLocal)
            cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Hit, 1);
            if (CountsValues)
            {
                CountTableValues(group, features, version, new ReadOnlySpan<LocalRecordRef>(in record));
            }

            return null;
        }

        var misses = _memory.Rent<CacheMiss>(1);
        misses[0] = new CacheMiss(0, freshness == CacheFreshness.Stale);
        var read = new CacheRead(group, features.ToArray(), version, key, new ReadOnlySpan<LocalRecordRef>(in record), misses, 1, rowCapacity: 0, _memory)
        {
            OneRecord = true,
        };
        Resolve(read, started, cancellationToken);
        return read;
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

        var deadline = DeadlineOf(read.Group, out var budget);
        read.BudgetMs = budget;
        if (read.BudgetMs > 0)
        {
            // The host's clock is read before the bound is made, which ResolveAsync releases: a clock that throws leaves no
            // bound behind. The bound ends at the deadline taken with the budget all the same.
            read.StoreBudgetMs = Math.Min(read.BudgetMs, read.Supply.DistributedTimeoutMs ?? Math.Max(1, read.BudgetMs / 2));
            var clock = read.Cache.Clock; // the cache's clock times the tier too (a test moves it by hand)
            read.StoreDeadline = clock.GetTimestamp() + (read.StoreBudgetMs * clock.TimestampFrequency / 1000);
            read.Cancellation = new CallBound(CancellationSite.Fetch, cancellationToken, deadline, _events, _requestId);
        }

        // Tracked, as the context fetch is: it may start an origin call after every call the request has sent so far
        // completed (the request may have stopped reading it, after a failure), so the drain waits for it, not only for
        // the calls it starts.
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
        RequestReads.SharedRead? reading = null; // the request's read of the record from the connector, when this read is its reader
        try
        {
            if (read.Cancellation is null)
            {
                read.Fail(NoTimeLeft.Error, IoOutcome.Timeout, sent: false);
            }
            else
            {
                if (read.Supply.Distributed is { } tier)
                {
                    await ReadDistributedAsync(read, tier, cancellationToken).ConfigureAwait(false);
                }

                // A context read is one record. When the tiers did not have it fresh, another read of the request may be
                // asking the connector for it, or have asked: this one takes what that one gets (RequestReads).
                var taken = false;
                if (read.OneRecord && read.Sources[0] is (CacheSource.Pending or CacheSource.StaleCandidate) && Reads is { } reads)
                {
                    var shared = reads.Join(read.Group.Supply.Table!.Table, LocalTableCache.RecordKey(read.KeyBatch, 0), limit: 0, out var reader);
                    if (reader)
                    {
                        reading = shared;
                    }
                    else
                    {
                        taken = Take(read, await shared.WaitAsync(read.Cancellation, CallDeadline.None).ConfigureAwait(false));
                    }
                }

                if (!taken)
                {
                    await FetchOriginAsync(read, read.Cancellation, cancellationToken).ConfigureAwait(false);
                }
            }

            // Only the record the connector gave whole, which is the one written back: anything else is this read's own.
            if (reading is not null && read.Sources[0] == CacheSource.Origin)
            {
                reading.Complete(read.Bytes[0]);
            }

            Settle(read);
            WriteBack(read);
        }
        finally
        {
            reading?.Withdraw(); // unless it ended above: the reads waiting for it ask the connector themselves
            read.Cancellation?.Release(); // the read's own reference; every sent call holds one until it really completes
            _memory.Release();
        }
    }

    /// <summary>
    /// What the request's read of a context read's record gave it (<see cref="RequestReads"/>); false when the read asks
    /// the connector itself. The record is taken as a tier's is, when fresh by this read's times; a wait this read's
    /// budget cut off ends the read as a call of its own cut off, with the stale record it holds, if any.
    /// </summary>
    private static bool Take(CacheRead read, in SharedAnswer answer)
    {
        switch (answer.End)
        {
            case SharedEnd.Record:
                var record = (ReadOnlyMemory<byte>)answer.Record!;
                var fresh = read.Supply.Policy.Classify(CacheRecord.WrittenMs(record.Span), read.KeyBatch, read.Misses[0].KeyIndex, read.Cache.NowMs())
                    == CacheFreshness.Fresh;
                read.Cache.CountKeys(CacheTier.Request, fresh ? CacheKeyOutcome.Hit : CacheKeyOutcome.Expired, 1);
                if (fresh)
                {
                    read.Sources[0] = CacheSource.Shared;
                    read.Bytes[0] = record;
                }

                return fresh;
            case SharedEnd.CutOff or SharedEnd.TimedOut:
                var cancelled = read.Cancellation!.Caller.IsCancellationRequested;
                read.Fail((cancelled ? Cancelled(read.Group) : TimedOut(read.Group, read.BudgetMs)).Error, cancelled ? IoOutcome.Cancelled : IoOutcome.Timeout,
                    sent: false); // it waited for another read's call and sent none
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the misses from the distributed tier, chunk by chunk, and sorts what it had by freshness.</summary>
    private async ValueTask ReadDistributedAsync(CacheRead read, DistributedCacheTier tier, CancellationToken cancellationToken)
    {
        var chunkSize = Batching.Size(read.MissCount, read.Supply.MaxBatch); // the fewest chunks, even
        var chunks = Batching.Count(read.MissCount, chunkSize);
        var calls = _memory.Rent<ValueTask<CallResult<bool>>>(chunks);
        var answeredAt = _memory.Rent<long>(chunks);
        var deadline = CallDeadline.At(read.Cache.Clock, read.StoreDeadline, read.StoreBudgetMs); // the read's, not each chunk's
        for (var c = 0; c < chunks; c++)
        {
            // A store's synchronous part runs here, unbounded (see ICacheStore.GetAsync): once one has taken the
            // tier's time, the chunks not sent yet are not sent (BoundedCall's rule 8); their keys go to the connector
            // like a late chunk's. The tier's time is judged by the cache's clock, which a test holds.
            var offset = c * chunkSize;
            calls[c] = BoundedCall.RunAsync<bool, StoreCall>(
                new StoreCall(this, read, tier, offset, Math.Min(chunkSize, read.MissCount - offset), answeredAt, c), read.Cancellation!, _memory,
                read.Supply.Io, deadline);
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
            // fresh; and one that answered at once, waiting here behind a slow earlier chunk,
            // would be judged late and its fresh records sent to the connector.
            var answered = await calls[c].ConfigureAwait(false);
            var nowMs = answered.Outcome == IoOutcome.Ok ? answeredAt[c] : ChunkNotAnswered;
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

                switch (read.Supply.Policy.Classify(writtenMs, read.KeyBatch, read.Misses[m].KeyIndex, nowMs))
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
    /// One read of the distributed tier, a chunk of the read's misses in one round trip, as <see cref="BoundedCall"/>'s
    /// call site: the wait is bounded by the tier's deadline (by the cache's clock) and the caller's token, never by the
    /// store's token; what the store writes after the read stopped waiting is never read. The store's token carries the
    /// tier's deadline when it comes before the fetch's (a token source and its timer per chunk, about 210 B), so a store
    /// that honours it (<c>redis@1</c> does) stops, and gives the request memory back, at the tier's deadline rather than
    /// the fetch's. It notes the cache clock's time (ms) when the chunk was seen to answer in time, which its records are
    /// judged at.
    /// </summary>
    /// <remarks>
    /// The time is taken as soon as the outcome is known, before <see cref="IoCall.Complete"/> calls the host's listeners
    /// (<see cref="BoundedCall"/> has the call site end first): one that took time, or moved the cache's clock, made a
    /// record fresh when the store answered look expired, and sent its key to the connector.
    /// </remarks>
    private readonly struct StoreCall(FeatureFetcher fetcher, CacheRead read, DistributedCacheTier tier, int offset, int count, long[] answeredAt, int chunk)
        : IBoundedCall<bool>
    {
        public int Keys => count;

        public ValueTask<bool> Send(CancellationToken token)
        {
            return tier.ReadAsync(read.Cache.Keyspace, read.MissTexts!.AsMemory(offset, count), read.Stored.AsMemory(offset, count), token);
        }

        public IoOutcome Judge(bool value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        public void Pending(Task done) => fetcher.TrackIo(done); // done once the store and this call site both are

        public void Ended(in CallResult<bool> result, TimeSpan duration) =>
            answeredAt[chunk] = result.Outcome == IoOutcome.Ok ? read.Cache.NowMs() : ChunkNotAnswered;
    }

    /// <summary>
    /// Fetches what neither tier had fresh from the connector, in <c>max_batch</c> chunks through the fetch path's call
    /// (not recorded on their own: the read is recorded as one call), and encodes each answered key's record.
    /// </summary>
    private async ValueTask FetchOriginAsync(CacheRead read, CallBound bound, CancellationToken cancellationToken)
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

        if (Stopwatch.GetTimestamp() >= bound.Deadline)
        {
            read.Fail(NoTimeLeft.Error, IoOutcome.Timeout, sent: false); // the distributed tier took the whole budget
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
                keyOf[n] = read.Misses[m].KeyIndex;
                missOf[n++] = m;
            }
        }

        var layout = group.KeyLayout;
        var keys = read.KeyBatch.Select(keyOf.AsSpan(0, count), _memory.Rent<EntityKey>((layout.VaryingCount * count) + layout.PartCount - layout.VaryingCount));

        var chunkSize = Batching.Size(count, read.Supply.MaxBatch); // the fewest chunks, even
        var chunks = Batching.Count(count, chunkSize);
        var staging = new StagingArea(group.Fields.AsSpan(), count, chunkSize, _memory);
        var calls = _memory.Rent<ValueTask<CallResult<FetchStatus>>>(chunks);
        var writtenMs = read.Cache.NowMs(); // the version of what this fetch writes back: when it started
        StagingBuffer? writer = null;
        for (var c = 0; c < chunks; c++)
        {
            // As a plain fetch's (FetchCandidates): once the time is up or the caller has cancelled, the chunks not sent
            // yet are not sent (BoundedCall's rule 8); their keys stay unanswered. Not recorded on their own: the read is
            // recorded as one call.
            var offset = c * chunkSize;
            var batch = keys.Slice(offset, Math.Min(chunkSize, count - offset));
            writer ??= new StagingBuffer(staging);
            writer.Bind(c);
            calls[c] = Send(group, batch, group.FieldArray, writer, read.BudgetMs, NotRecorded, bound, out var writerHeld);
            if (writerHeld)
            {
                writer = null; // the call runs on and may write through this view until it really completes
            }
        }

        var records = new CacheRecordWriter(read.Cache.Layout, count * read.Cache.Layout.TypicalSize);
        var spans = _memory.Rent<(int Offset, int Length)>(count);
        for (var c = 0; c < chunks; c++)
        {
            var answered = await calls[c].ConfigureAwait(false);
            var status = StatusOf(group, read.BudgetMs, answered);
            read.Asked |= answered.Sent;
            if (status.Outcome == FetchOutcome.Failed)
            {
                read.Fail(status.Error, answered.Outcome, answered.Sent); // its keys stay unanswered; its staging is never read
                continue;
            }

            var partial = status.Outcome == FetchOutcome.Partial;
            if (partial)
            {
                read.Partial = true;
                read.PartialKeys += status.FailedKeys;
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
    /// What became of each miss once the connector has answered or failed: a key the
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
                    var miss = read.Misses[m];
                    var localMs = miss.LocalStale ? read.Version.VersionOf(read.Records[miss.KeyIndex]) : 0;
                    var local = miss.LocalStale && read.Supply.Policy.Classify(localMs, read.KeyBatch, miss.KeyIndex, nowMs) != CacheFreshness.Expired;
                    var sharedMs = read.Sources[m] == CacheSource.StaleCandidate ? CacheRecord.WrittenMs(read.Bytes[m].Span) : 0;
                    var shared = read.Sources[m] == CacheSource.StaleCandidate && read.Supply.Policy.Classify(sharedMs, read.KeyBatch, miss.KeyIndex, nowMs) != CacheFreshness.Expired;
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
                case CacheSource.Distributed or CacheSource.Shared or CacheSource.Origin or CacheSource.OriginPartial:
                    break;
                case CacheSource.StaleDistributed or CacheSource.StaleLocal or CacheSource.Failed:
                    throw new InvalidOperationException("A cache read is settled once.");
            }
        }

        read.StaleKeys = staleLocal + staleDistributed;
        read.Cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Stale, staleLocal);
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

        // A cache without an in-process tier keeps nothing in process: its hits of the distributed tier are not gathered
        // for a writer that would drop them (a key and a record reference for each).
        var cache = read.Cache;
        var tier = read.Supply.Distributed;
        local = cache.Memory ? local : 0;
        shared = tier is null ? 0 : shared; // nor are the connector's records, for a tier the cache does not have
        if (local == 0 && shared == 0)
        {
            return;
        }

        var parts = read.KeyBatch.PartCount;
        var keys = local == 0 ? [] : new EntityKey[local * parts];
        var records = local == 0 ? [] : new ReadOnlyMemory<byte>[local];
        var entries = shared == 0 ? null : new CacheStoreEntry[shared];
        var nowMs = cache.NowMs();
        int l = 0, s = 0;
        for (var m = 0; m < read.MissCount; m++)
        {
            if (read.Sources[m] is not (CacheSource.Distributed or CacheSource.Origin))
            {
                continue;
            }

            if (local > 0)
            {
                for (var p = 0; p < parts; p++)
                {
                    keys[(l * parts) + p] = read.KeyBatch.Key(read.Misses[m].KeyIndex, p);
                }

                records[l++] = read.Bytes[m];
            }

            if (entries is not null && read.Sources[m] == CacheSource.Origin)
            {
                entries[s++] = new CacheStoreEntry(read.MissTexts![m], read.Bytes[m], read.Supply.Policy.StoreTimeToLive(CacheRecord.WrittenMs(read.Bytes[m].Span), nowMs));
            }
        }

        if (local > 0)
        {
            cache.TryWriteBack(new CacheWriteBatch(keys, records, local, parts));
        }

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
    /// was not resolved: the connector failed for it, which <see cref="StrataRecMetrics.IoErrors"/> counts.
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
                else if (read.Sources[m] is CacheSource.Distributed or CacheSource.Shared or CacheSource.Origin or CacheSource.OriginPartial)
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
            FetchOutcome.Failed => read.Failure, // as its call's would be recorded: a timeout is not an error
        };
        var tableFields = TableFields(read.Group, read.Features);
        var fields = ImmutableCollectionsMarshal.AsImmutableArray(read.Fields!);
        recorder.Record(
            new FeatureCallInfo(read.Group, read.Batch, fields, read.Sequence, read.Sent, status, outcome, Stopwatch.GetElapsedTime(read.Started)),
            status.Outcome == FetchOutcome.Failed ? null : new CacheReadValues(read, tableFields));
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
                    case CacheSource.Distributed or CacheSource.Shared or CacheSource.Origin or CacheSource.OriginPartial or CacheSource.StaleDistributed:
                        CacheRecord.Read(read.Bytes[m].Span, layout, ref fill); // checked well formed (or encoded here) already
                        break;
                    case CacheSource.StaleLocal:
                        staleRows[stale] = read.Rows[i];
                        staleRecords[stale++] = read.Records[read.Misses[m].KeyIndex];
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

        /// <summary>
        /// The record another read of the request got from the connector (<see cref="RequestReads"/>), fresh by this
        /// read's times: written back by the read that got it, not by this one.
        /// </summary>
        Shared,

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

    private readonly record struct CacheMiss(int KeyIndex, bool LocalStale);

    /// <summary>
    /// One cache read that missed: its keys and what each resolved to, the rows waiting for them, and its budget. The
    /// arrays are rented from the request memory, which the read holds until it has resolved.
    /// </summary>
    private sealed class CacheRead
    {
        public CacheRead(
            FetchGroup group, int[] features, TableVersion version, in EntityKeyBatch keys, ReadOnlySpan<LocalRecordRef> records,
            CacheMiss[] misses, int missCount, int rowCapacity, RequestMemory memory)
        {
            Group = group;
            Cache = group.Supply.Table!.Table.Cache!;
            Supply = group.Supply.Cache!;
            Features = features;
            Version = version;
            KeyBatch = keys;
            Misses = misses;
            KeyCount = keys.Count;
            MissCount = missCount;
            Records = memory.Rent<LocalRecordRef>(KeyCount);
            records.CopyTo(Records);
            MissOf = memory.Rent<int>(KeyCount);
            MissOf.AsSpan(0, KeyCount).Fill(-1);
            var notFound = 0;
            for (var m = 0; m < missCount; m++)
            {
                var key = misses[m].KeyIndex;
                MissOf[key] = m;
                if (!records[key].Found)
                {
                    notFound++;
                }
            }

            Sources = memory.RentCleared<CacheSource>(missCount);
            Bytes = memory.RentCleared<ReadOnlyMemory<byte>>(missCount);
            Stored = Supply.Distributed is null ? [] : memory.RentCleared<ReadOnlyMemory<byte>>(missCount);
            Rows = rowCapacity == 0 ? [] : memory.Rent<int>(rowCapacity);
            RowMiss = rowCapacity == 0 ? [] : memory.Rent<int>(rowCapacity);

            MissTexts = Supply.Distributed is null ? null : FormatMissTexts(memory);
            Cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Hit, KeyCount - missCount);
            Cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Miss, notFound);
            Cache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Expired, missCount - notFound);
        }

        public FetchGroup Group { get; }

        public LocalTableCache Cache { get; }

        public CacheSupply Supply { get; }

        /// <summary>The features the read fills (staging field i of its recording is <c>Features[i]</c>).</summary>
        public int[] Features { get; }

        public TableVersion Version { get; }

        /// <summary>The read's distinct keys, in first-seen order (a fetch of the same rows takes the same).</summary>
        public int KeyCount { get; }

        /// <summary>The read's keys, as the recorded call takes them.</summary>
        public EntityKeyBatch KeyBatch { get; }

        /// <summary>Per key, its table record in <see cref="Version"/>, or none.</summary>
        public LocalRecordRef[] Records { get; }

        /// <summary>Per key, its miss, or -1 for a fresh key.</summary>
        public int[] MissOf { get; }

        public int MissCount { get; }

        /// <summary>Per miss, its key's text for the distributed tier (<see cref="CacheKeyspace.KeyText"/>); null without a tier.</summary>
        public string[]? MissTexts { get; }

        /// <summary>Distinct misses in first-seen order, including local stale candidates to recheck at fallback.</summary>
        public CacheMiss[] Misses { get; }

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
        public CallBound? Cancellation { get; set; }

        /// <summary>The resolution of the misses (<see cref="ResolveAsync"/>); also tracked by the request's I/O drain.</summary>
        public Task Resolution { get; set; } = Task.CompletedTask;

        public long Started { get; set; }

        public int Sequence { get; set; }

        public CompiledFieldBinding[]? Fields { get; set; }

        public string? Error { get; set; }

        /// <summary>How the read's first call to fail ended, for the read's recording when no key got a value.</summary>
        public IoOutcome Failure { get; private set; } = IoOutcome.Error;

        /// <summary>A call of the read reached its connector.</summary>
        public bool Asked { get; set; }

        /// <summary>
        /// Whether the read's call was sent, for its recording: false when the read had something to ask its origin
        /// for and sent none of it (no time left, or its caller had cancelled), whatever it could still give (a stale
        /// value). True when any of its chunks was sent, and for a read with no failure.
        /// </summary>
        public bool Sent => !_refused || Asked;

        private bool _failed;
        private bool _refused; // a call of the read was not sent

        /// <summary>The keys the connector said it could not answer, in the chunks it answered in part.</summary>
        public int PartialKeys { get; set; }

        /// <summary>A context group's read: one record, which the reads of a request share (<see cref="RequestReads"/>).</summary>
        public bool OneRecord { get; init; }

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
        /// Each miss's key text, when the read has a distributed tier to
        /// ask. Computed once per miss, for the read and the write-back.
        /// </summary>
        private string[] FormatMissTexts(RequestMemory memory)
        {
            var texts = memory.Rent<string>(MissCount);
            var partsBuffer = default(KeyPartBuffer);
            Span<EntityKey> parts = partsBuffer;
            parts = parts[..KeyBatch.PartCount];
            for (var m = 0; m < MissCount; m++)
            {
                for (var p = 0; p < parts.Length; p++)
                {
                    parts[p] = KeyBatch.Key(Misses[m].KeyIndex, p);
                }

                texts[m] = CacheKeyspace.KeyText(parts);
            }

            return texts;
        }

        /// <summary>The read's first failure is the one it reports, whether or not it came with words.</summary>
        public void Fail(string? error, IoOutcome outcome, bool sent = true)
        {
            _refused |= !sent;
            if (!_failed)
            {
                _failed = true;
                Error ??= error;
                Failure = outcome;
            }
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

            if (FailedKeys == KeyCount)
            {
                return new FetchStatus(FetchOutcome.Failed, 0, Error); // as its call said it, with words or without
            }

            var error = Error ?? $"source '{Group.SourceName}' answered in part";
            if (StaleKeys > 0)
            {
                error += $"; {StaleKeys} key(s) served stale from the cache (stale_if_error_ms)";
            }

            return new FetchStatus(FetchOutcome.Partial, FailedKeys + PartialKeys, error);
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
                case CacheSource.Distributed or CacheSource.Shared or CacheSource.Origin or CacheSource.OriginPartial or CacheSource.StaleDistributed:
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
            // A float list holding NaN or ±inf is replaced as a whole, as a float is.
            var finite = typeof(T) == typeof(float) ? ColumnValues.AllFinite(MemoryMarshal.Cast<T, float>(value))
                : typeof(T) != typeof(double) || ColumnValues.AllFinite(MemoryMarshal.Cast<T, double>(value));
            ListValue(field, value, finite);
        }

        /// <summary>
        /// A list no target of the row takes is walked without being decoded: a record holds every field of its source,
        /// and a node that reads one number of it paid for each tag's string (1.9 MB a read of 1,000
        /// records with 24 tags each). A string is decoded when a target takes it, as <see cref="String"/> does.
        /// </summary>
        public void StringListStart(int field, int count)
        {
            _items = Wanted(field) ? ArrayPool<string>.Shared.Rent(Math.Max(1, count)) : null;
            _itemCount = 0;
        }

        public void StringListItem(int field, ReadOnlySpan<byte> utf8)
        {
            if (_items is { } items)
            {
                items[_itemCount++] = _strings.InternUtf8(utf8);
            }
        }

        public void StringListEnd(int field)
        {
            if (_items is not { } items)
            {
                return;
            }

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

        /// <summary>Whether a target of <paramref name="field"/> is not filled on the row yet: one that <see cref="Take"/> would take.</summary>
        private readonly bool Wanted(int field)
        {
            for (var i = _starts[field]; i < _starts[field + 1]; i++)
            {
                if (!Bits.Get(_owner._table.GetFilled(_targets[i].Info.Slot), Row))
                {
                    return true;
                }
            }

            return false;
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
