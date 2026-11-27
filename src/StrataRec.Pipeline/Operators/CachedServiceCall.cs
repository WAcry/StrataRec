using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// One call of a service whose binding declares a cache (docs/decisions/external-calls.md): the in-process tier,
/// then the distributed one, then the service for what is left, within the service's <c>timeout_ms</c>. The answer is put
/// together in the caller's response, written back for the next calls, and recorded as the node got it. A call about no
/// items is cached whole; a call about items item by item, the items it misses sent to the service in one call of their
/// own. The one place that knows what a service cache record holds (<see cref="ServiceCache"/> stores them).
/// </summary>
/// <remarks>
/// <para><b>A record's layout</b> (its <c>layout</c> list): <c>[kind, n, …]</c>, <c>n</c> the outputs the call reads, in
/// its order. A call's answer (kind 1) goes on with <c>[items, id kind, limit asked, flags, dropped]</c> (dropped: the items
/// the service added past the limit) and an item's (kind 2) with nothing; then each output's <c>(type, elements)</c>. A call's item ids come first in the int64 or string list, its
/// scores first in the float64 list; then each output's values, in order, in the list of its type (a text one string).
/// The cache key's format (<see cref="ServiceInputDigest.CacheCallKey"/>) changes with this layout.</para>
/// <para><b>Only what can be put back is written back</b>: an item's answer needs every output the call reads, of whole
/// rows and no text, and a call that was not cut short (a text and a truncated generation are the whole call's); a call's
/// answer needs one kind of item id; neither may have a string slot the service left unwritten (a record has no null
/// string). Other answers are served and not cached, so their calls always go to the service.</para>
/// <para><b>A record is read only when it is well formed</b> (<see cref="WellFormed"/>): its layout is one this code
/// writes and its lists hold exactly what the layout says. Records come from a store other processes write too.</para>
/// </remarks>
internal sealed class CachedServiceCall
{
    private const int CallKind = 1;
    private const int ItemKind = 2;
    private const int CallHeader = 7; // kind, n, items, id kind, limit asked, flags, dropped
    private const int ItemHeader = 2; // kind, n
    private const int NoIds = 0;
    private const int Int64Ids = 1;
    private const int StringIds = 2;
    private const int TruncatedFlag = 1;

    // Output types in a layout: absent (the service did not write it), a tensor's element type, or a text.
    private const int Absent = 0;
    private const int BoolType = 1;
    private const int Int32Type = 2;
    private const int Int64Type = 3;
    private const int Float32Type = 4;
    private const int Float64Type = 5;
    private const int StringType = 6;
    private const int TextType = 7;

    private readonly IServiceCallScope _scope;
    private readonly BoundService _service;
    private readonly ServiceCache _cache;
    private readonly ServiceCall _call;
    private readonly ServiceResponse _response;
    private readonly RequestMemory _memory;
    private readonly bool _keyed;
    private readonly int _count;
    private readonly UInt128[] _keys;
    private readonly Row[] _rows;
    private readonly TableVersion _version;
    private readonly long _nowMs;
    private readonly CallDeadline _deadline;
    private bool _timeUsed; // the distributed tier's read was cut off at the service's own deadline
    private RequestReads.SharedRead? _reading; // the request's read of the call's record, when this call is its reader
    private int[]? _layout; // a call about items: the layout every record it uses has

    private CachedServiceCall(IServiceCallScope scope, BoundService service, ServiceCache cache, ServiceCall call, ServiceResponse response)
    {
        _scope = scope;
        _service = service;
        _cache = cache;
        _call = call;
        _response = response;
        _memory = scope.Memory;
        _keyed = !call.Items.IsEmpty;
        _count = _keyed ? call.BatchSize : 1;
        _keys = _memory.Rent<UInt128>(_count);
        _rows = _memory.RentCleared<Row>(_count);
        var order = ServiceInputDigest.NameOrder(call);
        if (_keyed)
        {
            ServiceInputDigest.CacheItemKeys(call, order, cache.AnswerIdentity, _keys.AsSpan(0, _count));
        }
        else
        {
            _keys[0] = ServiceInputDigest.CacheCallKey(call, order, cache.AnswerIdentity);
        }

        _version = cache.Table.Pin();
        _nowMs = cache.TableCache.NowMs();
        _deadline = CallDeadline.After(service.TimeoutMs); // the whole read's: the distributed tier and the service share it
    }

    private enum RowState : byte
    {
        Miss = 0,
        Fresh,
        Stale,
        Origin,
    }

    /// <summary>
    /// Runs the call and records it as its caller got it; throws <see cref="OperationCanceledException"/> as
    /// <see cref="ServiceCallPath.CallAsync"/> does, when the scope's bound ran out or the caller cancelled.
    /// </summary>
    public static async Task<ServiceStatus> RunAsync(IServiceCallScope scope, BoundService service, ServiceCache cache, ServiceCall call, ServiceResponse response)
    {
        var recorder = scope.Recorder;
        var sequence = recorder?.BeginCall(scope.Caller, service.Target, call) ?? 0;
        var started = Stopwatch.GetTimestamp();
        var result = new Result(ServiceStatus.Failed("the call did not complete"), IoOutcome.Error, null, false);
        CachedServiceCall? read = null;
        try
        {
            read = new CachedServiceCall(scope, service, cache, call, response);
            result = await read.ReadAsync().ConfigureAwait(false);
        }
        finally
        {
            read?._reading?.Withdraw(); // unless it ended with the record: the calls waiting for it call the service themselves
            response.Close();
            recorder?.EndCall(
                sequence, result.Outcome, result.CutOff?.Message ?? result.Status.Error, result.CutOff is not null, Stopwatch.GetElapsedTime(started), response,
                result.Answered ? result.Status : default, result.Degraded);
        }

        if (result.CutOff is { } cutOff)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cutOff);
        }

        return result.Status;
    }

    private async Task<Result> ReadAsync()
    {
        // A call the node could no longer send (its budget ran out, the caller cancelled) is not answered from the cache
        // either: it ends as the call it stands for would (BoundedCall, rule 8), whatever the tiers hold.
        if (BoundedCall.CutOff(_scope.Budget) is { } before)
        {
            return new Result(default, before.Outcome, before.Exception, false);
        }

        var misses = ReadLocal();
        if (misses > 0 && _cache.Distributed is { } tier)
        {
            if (await ReadDistributedAsync(tier, misses).ConfigureAwait(false) is { } cutOff)
            {
                return new Result(default, cutOff.Outcome, cutOff.Exception, false);
            }

            misses = Count(RowState.Fresh, negate: true);
        }

        if (misses > 0 && !_keyed && !_timeUsed && _scope.Reads is { } reads)
        {
            // A call about no items is one record. The tiers did not have it fresh: another call of the request for as
            // many items may be asking the service for it, or have asked, and this one takes what that one gets
            // (RequestReads).
            var joined = reads.Join(_cache.Table, _keys[0], _call.Limit, out var reader);
            if (reader)
            {
                _reading = joined;
            }
            else
            {
                var answer = await joined.WaitAsync(_scope.Budget, _deadline).ConfigureAwait(false);
                switch (answer.End)
                {
                    case SharedEnd.Record:
                        var took = Take((ReadOnlyMemory<byte>)answer.Record!);
                        _cache.TableCache.CountKeys(CacheTier.Request, took, 1);
                        misses = took == CacheKeyOutcome.Hit ? 0 : misses;
                        break;
                    case SharedEnd.TimedOut:
                        return Failure(ServiceStatus.Failed($"timed out after {_service.TimeoutMs} ms"), IoOutcome.Timeout, answered: false);
                    case SharedEnd.CutOff when BoundedCall.CutOff(_scope.Budget) is { } cut:
                        return new Result(default, cut.Outcome, cut.Exception, false);
                }
            }
        }

        if (misses == 0)
        {
            // Answered by the tiers alone, and taken only if the node could still send the call now, as a service's
            // answer is heard only if the call's bound has not passed when it is observed (BoundedCall, rule 1).
            if (BoundedCall.CutOff(_scope.Budget) is { } late)
            {
                return new Result(default, late.Outcome, late.Exception, false);
            }

            return new Result(Assemble(null, null), IoOutcome.Ok, null, true);
        }

        return await CallServiceAsync(misses).ConfigureAwait(false);
    }

    /// <summary>
    /// The record another call of the request got from the service (<see cref="RequestReads"/>), taken as a tier's is:
    /// when it is fresh by this call's times and answers this call. Returns how the request tier counts it.
    /// </summary>
    private CacheKeyOutcome Take(ReadOnlyMemory<byte> bytes)
    {
        var decoded = _cache.Decode(bytes, out _);
        if (decoded is null || !WellFormed(decoded) || !Answers(decoded))
        {
            return CacheKeyOutcome.Mismatch; // another layout, or too few items
        }

        if (_cache.Classify(decoded.WrittenMs, _keys[0], _cache.TableCache.NowMs()) != CacheFreshness.Fresh)
        {
            return CacheKeyOutcome.Expired;
        }

        _rows[0] = new Row(RowState.Fresh, default, decoded);
        return CacheKeyOutcome.Hit;
    }

    /// <summary>The in-process tier: each key's record, fresh or stale, when it answers the call. Returns the rows not fresh.</summary>
    private int ReadLocal()
    {
        int hits = 0, missing = 0, expired = 0;
        for (var r = 0; r < _count; r++)
        {
            var found = ServiceCache.Find(_version, _keys[r]);
            if (!found.Found)
            {
                missing++;
                continue;
            }

            var record = new LocalServiceRecord(_version, found);
            var freshness = _cache.Classify(record.WrittenMs, _keys[r], _nowMs);
            if (!WellFormed(record))
            {
                missing++; // never written by this code: a write-back copies only well-formed records
            }
            else if (freshness == CacheFreshness.Expired)
            {
                expired++;
            }
            else if (!Answers(record))
            {
                missing++; // of another layout, or too few items: the service answers anew
            }
            else if (freshness == CacheFreshness.Fresh)
            {
                found.MarkCacheHit();
                _rows[r] = new Row(RowState.Fresh, found, null);
                hits++;
            }
            else
            {
                _rows[r] = new Row(RowState.Stale, found, null); // kept for a failure of the service
                expired++;
            }
        }

        _cache.TableCache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Hit, hits);
        _cache.TableCache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Miss, missing);
        _cache.TableCache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Expired, expired);
        return _count - hits;
    }

    /// <summary>
    /// The distributed tier, for the rows the memory tier did not have fresh, in one read bounded by its <c>timeout_ms</c>
    /// within the service's: fresh records answer their rows and go to the memory tier; a stale one is kept for a failure.
    /// Records are judged when the read answered, by the cache's clock: one fresh when the call started may have expired
    /// while the read took its time, as for a source's cache. A read that fails or is late leaves its rows to
    /// the service. Returns the cut-off when the node's budget ran out or the caller cancelled.
    /// </summary>
    private async Task<(Exception Exception, IoOutcome Outcome)?> ReadDistributedAsync(DistributedCacheTier tier, int misses)
    {
        var rows = new int[misses];
        var texts = new string[misses];
        for (int r = 0, i = 0; r < _count; r++)
        {
            if (_rows[r].State != RowState.Fresh)
            {
                rows[i] = r;
                texts[i++] = ServiceCache.KeyText(_keys[r]);
            }
        }

        var records = new ReadOnlyMemory<byte>[misses];
        var answeredAt = _memory.Rent<long>(1);
        var tierDeadline = CallDeadline.After(_cache.DistributedTimeoutMs);
        var serviceFirst = _deadline.IsSet && _deadline.Remaining <= tierDeadline.Remaining; // the read within the service's time
        var read = await BoundedCall.RunAsync<bool, StoreRead>(
            new StoreRead(_scope, tier, _cache.TableCache, texts, records, answeredAt), _scope.Budget, _memory, _cache.Io,
            serviceFirst ? _deadline : tierDeadline).ConfigureAwait(false);
        switch (read.End)
        {
            case CallEnd.BudgetRanOut or CallEnd.Cancelled:
                return (read.Exception!, read.Outcome);
            case CallEnd.Answered:
                break;
            default:
                // Cut off at the service's own deadline, the read took all of its time: the service is not called. Said
                // here, not left to the clock: a wait ends up to a millisecond before its deadline (BoundedCall).
                _timeUsed = serviceFirst && read.End == CallEnd.TimedOut;
                _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Error, misses);
                return null;
        }

        int hits = 0, missing = 0, expired = 0, mismatched = 0, broken = 0;
        List<(UInt128 Key, ReadOnlyMemory<byte> Bytes)>? copies = null;
        var nowMs = answeredAt[0];
        for (var i = 0; i < misses; i++)
        {
            if (records[i].IsEmpty)
            {
                missing++;
                continue;
            }

            var decoded = _cache.Decode(records[i], out var outcome);
            if (decoded is null || !WellFormed(decoded))
            {
                broken += decoded is not null || outcome == CacheKeyOutcome.Error ? 1 : 0;
                mismatched += decoded is null && outcome != CacheKeyOutcome.Error ? 1 : 0; // another format or field set
                continue;
            }

            var r = rows[i];
            var freshness = _cache.Classify(decoded.WrittenMs, _keys[r], nowMs);
            if (freshness == CacheFreshness.Expired)
            {
                expired++;
            }
            else if (!Answers(decoded))
            {
                mismatched++; // another layout, or too few items
            }
            else if (freshness == CacheFreshness.Fresh)
            {
                _rows[r] = new Row(RowState.Fresh, default, decoded);
                if (_cache.TableCache.Memory)
                {
                    (copies ??= new()).Add((_keys[r], decoded.Bytes)); // for the in-process tier, when the cache has one
                }

                hits++;
            }
            else
            {
                // Kept for a failure of the service: the newer of a stale local record and this one, which stays within
                // stale_if_error_ms longer (a stale local record hid a newer one here).
                if (_rows[r].State == RowState.Miss || (_rows[r].State == RowState.Stale && decoded.WrittenMs > WrittenMs(r)))
                {
                    _rows[r] = new Row(RowState.Stale, default, decoded);
                }

                expired++;
            }
        }

        _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Hit, hits);
        _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Miss, missing);
        _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Expired, expired);
        _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Mismatch, mismatched);
        _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Error, broken);
        if (copies is not null)
        {
            // The memory tier keeps them with the write time they were written with.
            var keys = new EntityKey[2 * copies.Count];
            var bytes = new ReadOnlyMemory<byte>[copies.Count];
            for (var i = 0; i < copies.Count; i++)
            {
                keys[2 * i] = EntityKey.FromInt64((long)(ulong)(copies[i].Key >> 64));
                keys[(2 * i) + 1] = EntityKey.FromInt64((long)(ulong)copies[i].Key);
                bytes[i] = copies[i].Bytes;
            }

            _cache.TableCache.TryWriteBack(new CacheWriteBatch(keys, bytes, copies.Count, partCount: 2));
        }

        return null;
    }

    /// <summary>
    /// The service, for the rows no tier had fresh: the whole call, or one of just those items. Its answer completes the
    /// response and is written back; when it fails, stale records answer if every such row has one.
    /// </summary>
    private async Task<Result> CallServiceAsync(int misses)
    {
        if (_timeUsed)
        {
            return Failure(ServiceStatus.Failed($"timed out after {_service.TimeoutMs} ms"), IoOutcome.Timeout, answered: false);
        }

        int[]? subRows = null; // the call's row of each row of the service's call; null when it is the whole call
        var call = _call;
        if (_keyed && misses < _count)
        {
            subRows = new int[misses];
            for (int r = 0, i = 0; r < _count; r++)
            {
                if (_rows[r].State != RowState.Fresh)
                {
                    subRows[i++] = r;
                }
            }

            call = Subset(subRows);
        }

        var writtenMs = _cache.TableCache.NowMs();
        var answer = new ServiceResponse(_memory, call.Limit);

        // Within what is left of the service's timeout_ms: not sent at all once the distributed tier took all of it.
        var sent = await ServiceCallPath.SendAsync(_scope, _service, call, answer, _deadline).ConfigureAwait(false);
        if (sent.End is CallEnd.BudgetRanOut or CallEnd.Cancelled)
        {
            return new Result(default, sent.Outcome, sent.Exception, false);
        }

        if (sent.End == CallEnd.Answered && sent.Value.Succeeded)
        {
            for (var r = 0; r < _count; r++)
            {
                if (_rows[r].State != RowState.Fresh)
                {
                    _rows[r] = new Row(RowState.Origin, default, null);
                }
            }

            var status = Assemble(answer, subRows);
            if (status.Succeeded)
            {
                WriteBack(answer, call, subRows, writtenMs, sent.Value.Truncated);
                status = status with { OutputTokens = sent.Value.OutputTokens, Truncated = sent.Value.Truncated };
            }

            return new Result(status, status.Succeeded ? IoOutcome.Ok : IoOutcome.Error, null, true);
        }

        // A failed answer is recorded as the service gave it (its generation details), as a call without a cache is.
        var failure = sent.End == CallEnd.Answered ? sent.Value : ServiceStatus.Failed(sent.Error ?? "failed");
        return Failure(failure, sent.Outcome == IoOutcome.Ok ? IoOutcome.Error : sent.Outcome, sent.End == CallEnd.Answered);
    }

    /// <summary>
    /// The service did not answer the rows no tier had fresh: stale records answer them when every one has one, or the
    /// call fails as the service did.
    /// </summary>
    /// <param name="answered">The service answered (a failed status): what it said is recorded.</param>
    private Result Failure(in ServiceStatus failure, IoOutcome outcome, bool answered)
    {
        if (Count(RowState.Fresh, negate: true) == Count(RowState.Stale, negate: false) && StaleAnswer(_cache.TableCache.NowMs()))
        {
            // Taken only if the node could still send the call now, as an answer of the tiers alone is
            // (a caller that cancelled while the distributed tier used up the service's time got the stale values).
            if (BoundedCall.CutOff(_scope.Budget) is { } late)
            {
                return new Result(default, late.Outcome, late.Exception, false);
            }

            // Every row without a fresh record has a stale one still within stale_if_error_ms now: they answer, and the
            // node is Degraded, in a replay too (the reason is recorded with the call).
            _cache.TableCache.CountKeys(CacheTier.Memory, CacheKeyOutcome.Stale, CountStale(wire: false));
            _cache.TableCache.CountKeys(CacheTier.Distributed, CacheKeyOutcome.Stale, CountStale(wire: true));
            var degraded = $"{_service.Target.Role} '{_service.Name}' failed ({failure.Error}); its cache answered with values past their TTL";
            _scope.ReportDegraded(degraded);
            return new Result(Assemble(null, null), IoOutcome.Ok, null, true, degraded);
        }

        return new Result(failure, outcome, null, answered);
    }

    /// <summary>
    /// Whether every stale row may still answer at <paramref name="nowMs"/>: its record is within <c>stale_if_error_ms</c>
    /// now, not only when it was read (the service may have taken all its time to fail).
    /// </summary>
    private bool StaleAnswer(long nowMs)
    {
        for (var r = 0; r < _count; r++)
        {
            if (_rows[r].State != RowState.Stale)
            {
                continue;
            }

            if (_cache.Classify(WrittenMs(r), _keys[r], nowMs) == CacheFreshness.Expired)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>When the record row <paramref name="r"/> holds (a stale or fresh one, of either tier) was written.</summary>
    private long WrittenMs(int r) => _rows[r].Wire is { } wire ? wire.WrittenMs : new LocalServiceRecord(_version, _rows[r].Local).WrittenMs;

    // ------------------------------------------------------------------------------------------------------------------
    // Reading records
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a record can answer the call: of its kind and outputs; a call's with enough items (or the service had no
    /// more); an item's with every output a tensor, in the layout every other record of the call has.
    /// </summary>
    private bool Answers<TRecord>(TRecord record)
        where TRecord : IServiceRecord
    {
        var layout = record.Layout;
        var outputs = _call.Outputs.Length;
        if (!_keyed)
        {
            if (layout.Length != CallHeader + (2 * outputs) || layout[0] != CallKind || layout[1] != outputs)
            {
                return false;
            }

            var items = layout[2];
            var asked = layout[4];
            return _call.Limit <= items || (asked > 0 && items < asked);
        }

        if (layout.Length != ItemHeader + (2 * outputs) || layout[0] != ItemKind || layout[1] != outputs)
        {
            return false;
        }

        if (_layout is not null)
        {
            return layout.SequenceEqual(_layout);
        }

        for (var o = 0; o < outputs; o++)
        {
            if (layout[ItemHeader + (2 * o)] is Absent or TextType)
            {
                return false;
            }
        }

        _layout = layout.ToArray();
        return true;
    }

    /// <summary>
    /// Whether a record is one this code writes (see the remarks): its kind and header, each output's type and element
    /// count, and its lists holding exactly that many values of each type. A record that is not is never read: a type code
    /// out of range, or a list shorter than its layout, would throw while the answer is put together, and a write-back
    /// would copy it into the memory tier.
    /// </summary>
    private static bool WellFormed<TRecord>(TRecord record)
        where TRecord : IServiceRecord
    {
        var layout = record.Layout;
        if (layout.Length < 2)
        {
            return false;
        }

        var (kind, outputs) = (layout[0], layout[1]);
        var header = kind == CallKind ? CallHeader : kind == ItemKind ? ItemHeader : -1;
        if (header < 0 || outputs < 0 || layout.Length != header + (2L * outputs))
        {
            return false;
        }

        Span<long> sizes = stackalloc long[StringType + 1]; // values in each list, by type code (a text in the string list)
        sizes.Clear();
        if (kind == CallKind)
        {
            // As a response holds them: no more items than the limit asked, and items dropped only past a full list.
            var (items, ids, asked, flags, dropped) = (layout[2], layout[3], layout[4], layout[5], layout[6]);
            if (items < 0 || asked < 0 || dropped < 0 || items > asked || (dropped > 0 && items != asked) || (flags & ~TruncatedFlag) != 0
                || ids is < NoIds or > StringIds || (ids == NoIds) != (items == 0))
            {
                return false;
            }

            sizes[Float64Type] += items; // the scores
            sizes[ids == StringIds ? StringType : Int64Type] += ids == NoIds ? 0 : items;
        }

        for (var o = 0; o < outputs; o++)
        {
            var (type, count) = (layout[header + (2 * o)], layout[header + (2 * o) + 1]);
            switch (type)
            {
                case < 0 or > TextType:
                    return false;
                case Absent when kind == ItemKind || count != 0:
                case TextType when kind == ItemKind || count != 1:
                    return false;
                case Absent:
                    break;
                case TextType:
                    sizes[StringType]++;
                    break;
                default:
                    if (count < 0)
                    {
                        return false;
                    }

                    sizes[type] += count;
                    break;
            }
        }

        return record.Bools.Length == sizes[BoolType] && record.Int32s.Length == sizes[Int32Type] && record.Int64s.Length == sizes[Int64Type]
            && record.Float32s.Length == sizes[Float32Type] && record.Float64s.Length == sizes[Float64Type] && record.StringCount == sizes[StringType];
    }

    /// <summary>
    /// Puts the answer together in the caller's response: each row from its record or from <paramref name="answer"/> (the
    /// service's, whose row i is the call's row <c>subRows[i]</c>, or the call's own rows when null). A call about items
    /// whose records and service answer differ in an output's type or width fails: the service changed its outputs
    /// without a new version.
    /// </summary>
    private ServiceStatus Assemble(ServiceResponse? answer, int[]? subRows)
    {
        if (!_keyed)
        {
            if (_rows[0].State == RowState.Origin)
            {
                CopyAnswer(answer!);
                return ServiceStatus.Ok;
            }

            return _rows[0].Wire is { } wire ? WriteCall(wire) : WriteCall(new LocalServiceRecord(_version, _rows[0].Local));
        }

        if (answer is not null && subRows is null)
        {
            CopyAnswer(answer); // every row from the service: its answer as it is, whatever its outputs are
            return ServiceStatus.Ok;
        }

        var layout = _layout!;
        if (answer is not null)
        {
            var batch = subRows!.Length;
            for (var o = 0; o < _call.Outputs.Length; o++)
            {
                var name = _call.Outputs.Span[o];
                var (type, width) = (layout[ItemHeader + (2 * o)], layout[ItemHeader + (2 * o) + 1]);
                if (!answer.TryGetValues(name, out var values) || TypeCode(values.ElementType) != type || values.Length != batch * width)
                {
                    var got = answer.TryGetValues(name, out var v) ? $"{TensorValues.Name(v.ElementType)} with {v.Length} value(s) for {batch} item(s)" : "nothing";
                    return ServiceStatus.Failed(
                        $"{_service.Target.Role} '{_service.Name}': the cached answers of output '{name}' are {TypeName(type)} with {width} value(s) "
                        + $"an item, and the service answered {got}: was the service changed without a new version?");
                }
            }
        }

        var origin = answer is null ? null : OriginRows(subRows!);
        var offsets = Offsets(layout, ItemHeader);
        for (var o = 0; o < _call.Outputs.Length; o++)
        {
            var name = _call.Outputs.Span[o];
            var width = layout[ItemHeader + (2 * o) + 1];
            switch (layout[ItemHeader + (2 * o)])
            {
                case BoolType: Fill<bool>(name, width, offsets[o], answer, origin); break;
                case Int32Type: Fill<int>(name, width, offsets[o], answer, origin); break;
                case Int64Type: Fill<long>(name, width, offsets[o], answer, origin); break;
                case Float32Type: Fill<float>(name, width, offsets[o], answer, origin); break;
                case Float64Type: Fill<double>(name, width, offsets[o], answer, origin); break;
                default: Fill<string>(name, width, offsets[o], answer, origin); break;
            }
        }

        return ServiceStatus.Ok;
    }

    /// <summary>Each of the call's rows' row in the service's answer, or -1 for a row a record answers.</summary>
    private int[] OriginRows(int[] subRows)
    {
        var origin = new int[_count];
        Array.Fill(origin, -1);
        for (var i = 0; i < subRows.Length; i++)
        {
            origin[subRows[i]] = i;
        }

        return origin;
    }

    /// <summary>One output of a call about items, row by row from its record or the service's answer.</summary>
    private void Fill<T>(string name, int width, int offset, ServiceResponse? answer, int[]? origin)
    {
        var destination = _response.GetValues<T>(name, _count * width).Span;
        ReadOnlySpan<T> served = default;
        if (answer is not null && answer.TryGetValues(name, out var values))
        {
            served = values.As<T>().Span;
        }

        for (var r = 0; r < _count; r++)
        {
            var row = destination.Slice(r * width, width);
            if (origin is not null && origin[r] >= 0)
            {
                served.Slice(origin[r] * width, width).CopyTo(row);
            }
            else if (_rows[r].Wire is { } wire)
            {
                Values<T, DecodedServiceRecord>(wire, offset, width, row);
            }
            else
            {
                Values<T, LocalServiceRecord>(new LocalServiceRecord(_version, _rows[r].Local), offset, width, row);
            }
        }
    }

    /// <summary>A record's <paramref name="count"/> values of <typeparamref name="T"/> from <paramref name="offset"/> of its list of that type.</summary>
    private static void Values<T, TRecord>(TRecord record, int offset, int count, Span<T> destination)
        where TRecord : IServiceRecord
    {
        if (typeof(T) == typeof(string))
        {
            for (var i = 0; i < count; i++)
            {
                destination[i] = (T)(object)record.String(offset + i);
            }

            return;
        }

        ref var first = ref MemoryMarshal.GetReference(destination);
        if (typeof(T) == typeof(bool))
        {
            record.Bools.Slice(offset, count).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, bool>(ref first), count));
        }
        else if (typeof(T) == typeof(int))
        {
            record.Int32s.Slice(offset, count).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, int>(ref first), count));
        }
        else if (typeof(T) == typeof(long))
        {
            record.Int64s.Slice(offset, count).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, long>(ref first), count));
        }
        else if (typeof(T) == typeof(float))
        {
            record.Float32s.Slice(offset, count).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, float>(ref first), count));
        }
        else
        {
            record.Float64s.Slice(offset, count).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, double>(ref first), count));
        }
    }

    /// <summary>
    /// A call's answer from its record: outputs, texts, items up to the limit, and whether it was cut short. A record of a
    /// larger limit answers as a service keeping to the call's limit would (ServiceCall.Limit): the start of its list, no
    /// item dropped; the items the service added past a limit count only for the limit it was asked (the
    /// recorded items past the smaller limit were counted as dropped).
    /// </summary>
    private ServiceStatus WriteCall<TRecord>(TRecord record)
        where TRecord : IServiceRecord
    {
        var layout = record.Layout;
        var items = layout[2];
        var offsets = Offsets(layout, CallHeader);
        var prefix = layout[3] == StringIds ? items : 0; // a call's ids lead the string list, its scores the float64 list
        for (var o = 0; o < _call.Outputs.Length; o++)
        {
            var name = _call.Outputs.Span[o];
            var (type, count) = (layout[CallHeader + (2 * o)], layout[CallHeader + (2 * o) + 1]);
            var at = offsets[o];
            switch (type)
            {
                case Absent:
                    break;
                case BoolType:
                    record.Bools.Slice(at, count).CopyTo(_response.GetValues<bool>(name, count).Span);
                    break;
                case Int32Type:
                    record.Int32s.Slice(at, count).CopyTo(_response.GetValues<int>(name, count).Span);
                    break;
                case Int64Type:
                    record.Int64s.Slice(at + (layout[3] == Int64Ids ? items : 0), count).CopyTo(_response.GetValues<long>(name, count).Span);
                    break;
                case Float32Type:
                    record.Float32s.Slice(at, count).CopyTo(_response.GetValues<float>(name, count).Span);
                    break;
                case Float64Type:
                    record.Float64s.Slice(at + items, count).CopyTo(_response.GetValues<double>(name, count).Span);
                    break;
                case StringType:
                    var strings = _response.GetValues<string>(name, count).Span;
                    for (var i = 0; i < count; i++)
                    {
                        strings[i] = record.String(prefix + at + i);
                    }

                    break;
                case TextType:
                    _response.SetText(name, record.String(prefix + at));
                    break;
            }
        }

        var scores = record.Float64s;
        var answered = Math.Min(items, _call.Limit);
        for (var i = 0; i < answered; i++)
        {
            var id = layout[3] == StringIds ? EntityKey.FromString(record.String(i)) : EntityKey.FromInt64(record.Int64s[i]);
            _response.AddItem(id, scores[i]);
        }

        if (layout[4] == _call.Limit)
        {
            _response.AddDropped(layout[6]);
        }
        return ServiceStatus.Ok with { Truncated = (layout[5] & TruncatedFlag) != 0 };
    }

    /// <summary>
    /// Where each output's values start in the list of its type: after the outputs of the same type before it (a string
    /// output's and a text's both in the string list).
    /// </summary>
    private static int[] Offsets(ReadOnlySpan<int> layout, int header)
    {
        var outputs = layout[1];
        var offsets = new int[outputs];
        Span<int> next = stackalloc int[TextType + 1];
        next.Clear();
        for (var o = 0; o < outputs; o++)
        {
            var type = layout[header + (2 * o)];
            var list = type == TextType ? StringType : type;
            offsets[o] = next[list];
            next[list] += layout[header + (2 * o) + 1];
        }

        return offsets;
    }

    /// <summary>
    /// The service's whole answer into the caller's response: the outputs the call reads, its texts, its items, and how many
    /// items it added past the limit.
    /// </summary>
    private void CopyAnswer(ServiceResponse answer)
    {
        foreach (var name in _call.Outputs.Span)
        {
            if (answer.TryGetValues(name, out var values))
            {
                switch (values.ElementType)
                {
                    case TensorElementType.Bool: values.Bool.Span.CopyTo(_response.GetValues<bool>(name, values.Length).Span); break;
                    case TensorElementType.Int32: values.Int32.Span.CopyTo(_response.GetValues<int>(name, values.Length).Span); break;
                    case TensorElementType.Int64: values.Int64.Span.CopyTo(_response.GetValues<long>(name, values.Length).Span); break;
                    case TensorElementType.Float32: values.Float32.Span.CopyTo(_response.GetValues<float>(name, values.Length).Span); break;
                    case TensorElementType.Float64: values.Float64.Span.CopyTo(_response.GetValues<double>(name, values.Length).Span); break;
                    default: values.String.Span.CopyTo(_response.GetValues<string>(name, values.Length).Span); break;
                }
            }
            else if (answer.TryGetText(name, out var text))
            {
                _response.SetText(name, text);
            }
        }

        var keys = answer.Keys;
        var scores = answer.Scores;
        for (var i = 0; i < keys.Length; i++)
        {
            _response.AddItem(keys[i], scores[i]);
        }

        _response.AddDropped(answer.Dropped);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Writing records
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>The service's answer, written back: the call's whole, or each item's (those that can be put back).</summary>
    private void WriteBack(ServiceResponse answer, ServiceCall call, int[]? subRows, long writtenMs, bool truncated)
    {
        var outputs = _call.Outputs.Span;
        if (!_keyed)
        {
            var builder = new ServiceRecordBuilder(_cache, 1, writtenMs);
            if (EncodeCall(answer, builder, truncated))
            {
                builder.End(_keys[0]);
                _reading?.Complete(builder.Record(0));
                builder.WriteBack(_cache.TableCache.NowMs());
            }

            return;
        }

        // A truncated generation is the whole call's, not any item's: served, not cached.
        if (truncated)
        {
            return;
        }

        // Every output a tensor of whole rows of the service's call, with no unwritten string: then each row is a record.
        var batch = call.BatchSize;
        var layout = new int[ItemHeader + (2 * outputs.Length)];
        layout[0] = ItemKind;
        layout[1] = outputs.Length;
        var columns = new TensorValues[outputs.Length];
        for (var o = 0; o < outputs.Length; o++)
        {
            if (!answer.TryGetValues(outputs[o], out var values) || batch == 0 || values.Length % batch != 0 || HasNull(values))
            {
                return; // a text, a missing output, one not of whole rows or with a string slot left unwritten: served, not cached
            }

            columns[o] = values;
            layout[ItemHeader + (2 * o)] = TypeCode(values.ElementType);
            layout[ItemHeader + (2 * o) + 1] = values.Length / batch;
        }

        var items = new ServiceRecordBuilder(_cache, batch, writtenMs);
        for (var row = 0; row < batch; row++)
        {
            items.Layout.AddRange(layout);
            for (var o = 0; o < columns.Length; o++)
            {
                var width = layout[ItemHeader + (2 * o) + 1];
                Add(items, columns[o], row * width, width);
            }

            items.End(_keys[subRows is null ? row : subRows[row]]);
        }

        items.WriteBack(_cache.TableCache.NowMs());
    }

    /// <summary>
    /// A call's answer as one record, with whether it was cut short (a generation's <c>max_output_tokens</c>, which is in
    /// the key) and how many items it dropped; false when it cannot be put back (item ids of both kinds, a string slot left
    /// unwritten).
    /// </summary>
    private bool EncodeCall(ServiceResponse answer, ServiceRecordBuilder builder, bool truncated)
    {
        var outputs = _call.Outputs.Span;
        var keys = answer.Keys;
        var idKind = keys.IsEmpty ? NoIds : keys[0].IsString ? StringIds : Int64Ids;
        foreach (var key in keys)
        {
            if (key.IsString != (idKind == StringIds))
            {
                return false;
            }
        }

        builder.Layout.AddRange([CallKind, outputs.Length, keys.Length, idKind, _call.Limit, truncated ? TruncatedFlag : 0, answer.Dropped]);
        foreach (var key in keys)
        {
            if (idKind == StringIds)
            {
                builder.Strings.Add(key.StringValue!);
            }
            else
            {
                builder.Int64s.Add(key.Int64Value);
            }
        }

        foreach (var score in answer.Scores)
        {
            builder.Float64s.Add(score);
        }

        foreach (var name in outputs)
        {
            if (answer.TryGetValues(name, out var values))
            {
                if (HasNull(values))
                {
                    return false;
                }

                builder.Layout.Add(TypeCode(values.ElementType));
                builder.Layout.Add(values.Length);
                Add(builder, values, 0, values.Length);
            }
            else if (answer.TryGetText(name, out var text))
            {
                builder.Layout.Add(TextType);
                builder.Layout.Add(1);
                builder.Strings.Add(text);
            }
            else
            {
                builder.Layout.Add(Absent);
                builder.Layout.Add(0);
            }
        }

        return true;
    }

    /// <summary>A string output with a slot the service left unwritten (null), which no record can hold.</summary>
    private static bool HasNull(in TensorValues values) => values.ElementType == TensorElementType.String && values.String.Span.IndexOf((string?)null) >= 0;

    /// <summary>Values <c>[start, +count)</c> of a tensor, appended to the builder's list of their type.</summary>
    private static void Add(ServiceRecordBuilder builder, in TensorValues values, int start, int count)
    {
        switch (values.ElementType)
        {
            case TensorElementType.Bool: builder.Bools.AddRange(values.Bool.Span.Slice(start, count)); break;
            case TensorElementType.Int32: builder.Int32s.AddRange(values.Int32.Span.Slice(start, count)); break;
            case TensorElementType.Int64: builder.Int64s.AddRange(values.Int64.Span.Slice(start, count)); break;
            case TensorElementType.Float32: builder.Float32s.AddRange(values.Float32.Span.Slice(start, count)); break;
            case TensorElementType.Float64: builder.Float64s.AddRange(values.Float64.Span.Slice(start, count)); break;
            default: builder.Strings.AddRange(values.String.Span.Slice(start, count)); break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The service's call of the items the tiers did not have
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The call of the items at <paramref name="rows"/>: each per-item input's rows, every shared input whole (the rule of
    /// <see cref="ServiceCall.Items"/>), in buffers rented from the request.
    /// </summary>
    private ServiceCall Subset(int[] rows)
    {
        var inputs = _call.Inputs.Span;
        var subset = new NamedInput[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i];
            subset[i] = !ServiceInputDigest.Splits(input, _call.BatchSize) ? input
                : input.IsMissing ? NamedInput.FromMissing(input.Name, input.ElementType, Resized(input.Shape, rows.Length)) // missing on every row
                : input.ElementType switch
            {
                TensorElementType.Bool => Rows(input, input.Bool.Span, rows),
                TensorElementType.Int32 => Rows(input, input.Int32.Span, rows),
                TensorElementType.Int64 => Rows(input, input.Int64.Span, rows),
                TensorElementType.Float32 => Rows(input, input.Float32.Span, rows),
                TensorElementType.Float64 => Rows(input, input.Float64.Span, rows),
                _ => Rows(input, input.String.Span, rows),
            };
        }

        var items = _memory.Rent<EntityKey>(rows.Length);
        var all = _call.Items.Span;
        for (var i = 0; i < rows.Length; i++)
        {
            items[i] = all[rows[i]];
        }

        return new ServiceCall
        {
            Service = _call.Service,
            Version = _call.Version,
            Inputs = subset,
            BatchSize = rows.Length,
            Items = items.AsMemory(0, rows.Length),
            Outputs = _call.Outputs,
            Limit = 0,
            RequestId = _call.RequestId,
            Options = _call.Options,
        };
    }

    private NamedInput Rows<T>(in NamedInput input, ReadOnlySpan<T> values, int[] rows)
    {
        var width = values.Length / _call.BatchSize;
        var data = System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? _memory.RentCleared<T>(Math.Max(1, rows.Length * width))
            : _memory.Rent<T>(rows.Length * width);
        for (var i = 0; i < rows.Length; i++)
        {
            values.Slice(rows[i] * width, width).CopyTo(data.AsSpan(i * width, width));
        }

        return NamedInput.FromValues(input.Name, Resized(input.Shape, rows.Length), new ReadOnlyMemory<T>(data, 0, rows.Length * width));
    }

    /// <summary>A per-item input's shape for a batch of <paramref name="batch"/>.</summary>
    private static int[] Resized(ReadOnlySpan<int> shape, int batch)
    {
        var resized = shape.ToArray();
        resized[0] = batch;
        return resized;
    }

    // ------------------------------------------------------------------------------------------------------------------

    private int Count(RowState state, bool negate)
    {
        var count = 0;
        for (var r = 0; r < _count; r++)
        {
            count += (_rows[r].State == state) != negate ? 1 : 0;
        }

        return count;
    }

    private int CountStale(bool wire)
    {
        var count = 0;
        for (var r = 0; r < _count; r++)
        {
            count += _rows[r].State == RowState.Stale && (_rows[r].Wire is not null) == wire ? 1 : 0;
        }

        return count;
    }

    private static int TypeCode(TensorElementType type) => type switch
    {
        TensorElementType.Bool => BoolType,
        TensorElementType.Int32 => Int32Type,
        TensorElementType.Int64 => Int64Type,
        TensorElementType.Float32 => Float32Type,
        TensorElementType.Float64 => Float64Type,
        TensorElementType.String => StringType,
        _ => TextType,
    };

    private static string TypeName(int type) => type switch
    {
        BoolType => "bool",
        Int32Type => "int32",
        Int64Type => "int64",
        Float32Type => "float32",
        Float64Type => "float64",
        StringType => "string",
        _ => "text",
    };

    /// <summary>Where one row's answer comes from: a record of the memory tier or of the distributed one, or the service.</summary>
    private readonly record struct Row(RowState State, LocalRecordRef Local, DecodedServiceRecord? Wire);

    /// <summary>
    /// How the call ended: its status and outcome, or the cut-off the operator is thrown; whether there is an answer to
    /// record (the tiers', or the service's, a failure too); and why the node is Degraded, when the cache made it so.
    /// </summary>
    private readonly record struct Result(ServiceStatus Status, IoOutcome Outcome, Exception? CutOff, bool Answered, string? Degraded = null);

    /// <summary>
    /// One read of the distributed tier, bounded like any external call. It notes the cache clock's time when the read is
    /// seen to answer in time (<paramref name="answeredAt"/>), before the host's listeners hear of the call.
    /// </summary>
    private readonly struct StoreRead(IServiceCallScope scope, DistributedCacheTier tier, LocalTableCache policy, string[] keys, ReadOnlyMemory<byte>[] records, long[] answeredAt)
        : IBoundedCall<bool>
    {
        public int Keys => keys.Length;

        public ValueTask<bool> Send(CancellationToken token) => tier.ReadAsync(policy.Keyspace, keys, records, token);

        public IoOutcome Judge(bool value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        public void Pending(Task done) => scope.Pending(done);

        public void Ended(in CallResult<bool> result, TimeSpan duration) => answeredAt[0] = policy.NowMs();
    }
}
