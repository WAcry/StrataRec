using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Features;
using static StrataRec.UnitTests.Pipeline.ServiceCacheRig;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The review of IO step 3 (a service's cache): what a record must be to be read, when the cache may answer at all, when
/// it judges a record, what it keeps of an answer, and which answers one key may share.
/// </summary>
public sealed class ServiceCacheReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A record whose envelope is valid but whose layout this code never writes (a type code out of range, a width its
    /// lists do not hold) is never read: the service answers, and nothing of the record reaches the local tier. It threw
    /// while the answer was put together, and the write-back copied it into the local tier, where every later call threw.
    /// </summary>
    [Theory]
    [InlineData("type", 3, 123)]
    [InlineData("width", 4, 2)]
    public async Task AWellFramedRecordWithALayoutThisCodeNeverWrites_IsNeverRead(string what, int at, int value)
    {
        _ = what;
        var store = new FakeCacheStore();
        var writer = new ServiceCacheRig(store: store);
        var call = Items([41], [0.1f]);
        await writer.CallAsync(call);
        await store.WhenWrittenAsync(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(store.Gets.TryDequeue(out var keys));
        Assert.True(store.TryGet(keys[0], out var record, out _));

        // The layout list after the prefix: its length, then kind, n, the output's type and its width.
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(CacheRecord.PrefixSize(7) + (at * sizeof(int))), value);
        store.Put(keys[0], record);

        var reader = new ServiceCacheRig(store: store);
        reader.Service.Offset = 80;
        var (status, response) = await reader.CallAsync(call);
        reader.Apply();
        var (again, answered) = await reader.CallAsync(call);

        Assert.True(status.Succeeded && again.Succeeded, status.Error ?? again.Error);
        Assert.Equal([81f], Floats(response, "p"));
        Assert.Equal([81f], Floats(answered, "p")); // the service's answer, kept by the local tier
        Assert.Single(reader.Service.Calls);
    }

    /// <summary>
    /// A call its node could no longer send (the budget ran out, the caller cancelled) is not answered from the cache
    /// either: it throws as the call without a cache does. A local hit answered it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallItsNodeCouldNoLongerSend_IsNotAnsweredFromTheCache(bool callerCancelled)
    {
        var rig = new ServiceCacheRig();
        var call = Items([1], [0.1f]);
        await rig.CallAsync(call);
        rig.Apply();
        var memory = new RequestMemory();
        using var cancellation = new CancellationTokenSource();
        if (callerCancelled)
        {
            cancellation.Cancel();
        }

        var deadline = callerCancelled ? long.MaxValue : Stopwatch.GetTimestamp() - 1;
        var plain = Context(memory, cancellation.Token, deadline);
        var cached = Context(memory, cancellation.Token, deadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plain.CallAsync(rig.Unbound, call, new ServiceResponse(memory)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cached.CallAsync(rig.Bound, call, new ServiceResponse(memory)));
        Assert.Single(rig.Service.Calls); // the first call's
        memory.Release();
    }

    /// <summary>
    /// A distributed read that takes all of the service's <c>timeout_ms</c> leaves no time to call the service: the call
    /// times out, and the service is not called. It was called with a fresh millisecond of its own.
    /// </summary>
    [Fact]
    public async Task ADistributedReadThatTakesTheServicesTime_LeavesNoCallOfTheService()
    {
        var store = new FakeCacheStore();
        var rig = new ServiceCacheRig(store: store, timeoutMs: 40, distributedTimeoutMs: 10_000);
        var token = TestContext.Current.CancellationToken;
        store.BeforeGet = async _ => await Task.Delay(200, token);

        var (status, _) = await rig.CallAsync(Query(2, 2));

        Assert.Empty(rig.Service.Calls);
        Assert.Equal(ServiceStatus.Failed("timed out after 40 ms"), status);
    }

    /// <summary>
    /// An item is cached by what it saw of each input: a shared <c>[1, d]</c> input is all of it, in a call of two items
    /// and in a call of the item alone, whose record the other answers (review of the IO redesign: in a batch of one the
    /// shared input was per item by its shape, and the item's key changed with the batch).
    /// </summary>
    /// <remarks>A view of a few values enters a key inline, one of more as its own digest (<paramref name="width"/> 64): both alike in a batch of one and of two, for a shared input and for an item's own.</remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task AnItemWithASharedInput_HasOneKey_InABatchOfOneAndOfTwo(int width)
    {
        var rig = new ServiceCacheRig();
        ServiceCall WithShared(long[] ids, float[] x) => new()
        {
            Service = "m",
            BatchSize = ids.Length,
            Items = ids.Select(EntityKey.FromInt64).ToArray(),
            Inputs = new[]
            {
                NamedInput.FromFloat32("x", new[] { ids.Length }, x),
                NamedInput.FromFloat32("u", new[] { 1, width }, Enumerable.Range(0, width).Select(i => (float)i).ToArray()),
                NamedInput.FromFloat32("e", new[] { ids.Length, width }, ids.SelectMany(id => Enumerable.Range(0, width).Select(i => (float)((id * 100) + i))).ToArray()),
            },
            Outputs = new[] { "p" },
        };

        await rig.CallAsync(WithShared([1, 2], [0.1f, 0.2f]));
        rig.Apply();
        var (status, response) = await rig.CallAsync(WithShared([1], [0.1f]));
        await rig.CallAsync(WithShared([3], [0.3f]));
        rig.Apply();
        var (both, again) = await rig.CallAsync(WithShared([3, 2], [0.3f, 0.2f]));

        Assert.True(status.Succeeded && both.Succeeded, status.Error ?? both.Error);
        Assert.Equal([1f], Floats(response, "p"));
        Assert.Equal([3f, 2f], Floats(again, "p"));
        Assert.Equal(2, rig.Service.Calls.Count); // [1, 2] and [3]
    }

    /// <summary>
    /// A call whose items share an input (a user vector, <c>[1, d]</c>) reads it once for their keys, not once for every
    /// item (review of 43bdf97: a full hit of 2,048 items hashed a vector of 4,096 values 2,048 times, which used up its
    /// node's budget). The per-item input is still read per item, and every item still hits.
    /// </summary>
    /// <remarks>A shared string is one value, of any size (review of 3a0d9e8: one was encoded again for every item); a few shared values are encoded once too.</remarks>
    [Fact]
    public async Task ASharedInput_IsHashedOnceForTheItemsKeys_NotOnceAnItem()
    {
        const int count = 64;
        var rig = new ServiceCacheRig();
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray();
        var x = ids.Select(i => (float)i).ToArray();
        var wide = new ReadCountingMemory<float>(Enumerable.Range(0, 64).Select(i => (float)i).ToArray());
        var small = new ReadCountingMemory<float>([8, 9]);
        var text = new ReadCountingMemory<string>([new string('p', 4096)]);
        ServiceCall WithShared() => new()
        {
            Service = "m",
            BatchSize = count,
            Items = ids.Select(EntityKey.FromInt64).ToArray(),
            Inputs = new[]
            {
                NamedInput.FromFloat32("x", new[] { count }, x), NamedInput.FromFloat32("u", new[] { 1, 64 }, wide.Memory),
                NamedInput.FromFloat32("s", new[] { 1, 2 }, small.Memory), NamedInput.FromString("p", new[] { 1 }, text.Memory),
            },
            Outputs = new[] { "p" },
        };

        await rig.CallAsync(WithShared());
        rig.Apply();
        var before = (wide.Reads, small.Reads, text.Reads);
        var (status, response) = await rig.CallAsync(WithShared());

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(x.Select(v => v * 10), Floats(response, "p")); // as the service answered them
        Assert.Single(rig.Service.Calls); // the second call is a full hit
        Assert.All(new[] { wide.Reads - before.Item1, small.Reads - before.Item2, text.Reads - before.Item3 }, reads => Assert.InRange(reads, 1, 4)); // a few, not one an item
    }

    /// <summary>
    /// A store that cancels its own read, with the service's time left, leaves its rows to the service: only the service's
    /// deadline uses up its time (review of the IO redesign: the store's own cancellation was taken for that deadline, and
    /// a healthy service was not called).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoreThatCancelsItsOwnRead_LeavesTheServiceItsTime(bool pending)
    {
        var store = new FakeCacheStore();
        var rig = new ServiceCacheRig(store: store, timeoutMs: 10_000, distributedTimeoutMs: 30_000);
        store.BeforeGet = async _ =>
        {
            if (pending)
            {
                await Task.Yield();
            }

            throw new OperationCanceledException("the store's own timeout", new CancellationToken(canceled: true));
        };

        var (status, response) = await rig.CallAsync(Query(2, 2));

        Assert.True(status.Succeeded, status.Error);
        Assert.Single(rig.Service.Calls);
        Assert.Equal([1L, 2L], Ids(response));
    }

    /// <summary>
    /// A store's read that completed at once is consumed once, as one that completed later: a store may pool what backs
    /// it and take it back only then (review of the IO redesign: a service's cache read only its status).
    /// </summary>
    [Fact]
    public async Task AStoreReadThatCompletedAtOnce_IsConsumed()
    {
        var store = new PooledStore();
        var rig = new ServiceCacheRig(store: store);

        await rig.CallAsync(Query(1, 2));
        await rig.CallAsync(Query(2, 2));

        Assert.Equal((2, 2), (store.Reads, store.Consumed));
    }

    /// <summary>A store whose reads complete at once on an <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/>, consumed when its result is read.</summary>
    private sealed class PooledStore : ICacheStore, System.Threading.Tasks.Sources.IValueTaskSource
    {
        private int _reads;
        private int _consumed;

        public int Reads => Volatile.Read(ref _reads);

        public int Consumed => Volatile.Read(ref _consumed);

        public string StoreType => TestCacheStoreFactory.TypeName;

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            var read = (short)Interlocked.Increment(ref _reads);
            return new ValueTask(this, read);
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void GetResult(short token) => Interlocked.Increment(ref _consumed);

        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) => System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags) =>
            continuation(state);
    }

    /// <summary>
    /// A distributed record is judged when the read answers: one fresh when the call started that expired while the read
    /// took its time is not a hit, and the service answers. It was judged by the time the call started.
    /// </summary>
    [Fact]
    public async Task ADistributedRecord_IsJudgedWhenTheReadAnswers()
    {
        var store = new FakeCacheStore();
        var writer = new ServiceCacheRig(ttlMs: 1000, store: store);
        var query = Query(1, limit: 1, "answer");
        await writer.CallAsync(query);
        await store.WhenWrittenAsync(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var reader = new ServiceCacheRig(ttlMs: 1000, store: store);
        reader.Service.Offset = 100;
        store.BeforeGet = _ =>
        {
            reader.Clock.AdvanceMs(2000);
            return ValueTask.CompletedTask;
        };
        var (status, answer) = await reader.CallAsync(query);

        Assert.True(status.Succeeded, status.Error);
        Assert.True(answer.TryGetText("answer", out var text));
        Assert.Equal("a100", text);
    }

    /// <summary>
    /// A stale record answers a failure of the service only while it is within <c>stale_if_error_ms</c> when the service
    /// failed: one that passed it while the call took its time does not, and the call fails as the service did.
    /// </summary>
    [Fact]
    public async Task AStaleRecordPastItsWindowWhenTheServiceFails_DoesNotAnswer()
    {
        var store = new FakeCacheStore();
        var rig = new ServiceCacheRig(ttlMs: 1000, staleIfErrorMs: 5000, store: store);
        var query = Query(1, limit: 1);
        Assert.True((await rig.CallAsync(query)).Status.Succeeded);
        Assert.Equal(1, rig.Apply());
        rig.Clock.AdvanceMs(2000); // stale, within its window
        store.BeforeGet = _ =>
        {
            rig.Clock.AdvanceMs(5000); // past it by the time the service fails
            return ValueTask.CompletedTask;
        };
        rig.Service.Fail = true;

        var (status, _) = await rig.CallAsync(query);

        Assert.Equal(ServiceStatus.Failed("m is down"), status);
        Assert.Null(rig.Last!.DegradedReason);
    }

    /// <summary>
    /// A string slot the service left unwritten (null) is served as it is and not cached: a record has no null string.
    /// Encoding it threw, and a call that had answered failed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStringSlotLeftUnwritten_IsServed_AndNotCached(bool aboutItems)
    {
        var rig = new ServiceCacheRig();
        var client = new Client((_, response) =>
        {
            response.GetValues<string>("s", 1).Span[0] = null!; // allocated, never written
            response.GetValues<string>("t", 2).Span[0] = "written";
            return ServiceStatus.Ok;
        });
        var call = aboutItems ? Items([1, 2], [0.1f, 0.2f], "t") : Query(1f, 0, "s", "t");
        var memory = new RequestMemory();
        var response = new ServiceResponse(memory, call.Limit);

        var status = await Context(memory, CancellationToken.None, long.MaxValue).CallAsync(rig.With(client), call, response);

        Assert.True(status.Succeeded, status.Error);
        Assert.True(response.TryGetValues("t", out var values));
        Assert.Equal(["written", null], values.String.ToArray());
        Assert.Equal(0, rig.Apply());
        memory.Release();
    }

    /// <summary>
    /// A call about items whose generation was cut short is served and not cached: the truncation is the whole call's,
    /// not any item's, so a later call of the items (alone or with others) asks the service again. Its items were cached,
    /// and a hit said it was not truncated.
    /// </summary>
    [Fact]
    public async Task ATruncatedAnswerAboutItems_IsServed_AndNotCached()
    {
        var rig = new ServiceCacheRig();
        rig.Service.Truncated = true;

        var (first, _) = await rig.CallAsync(Items([1], [0.1f]));
        var written = rig.Apply();
        var (second, _) = await rig.CallAsync(Items([1], [0.1f]));

        Assert.True(first.Succeeded && first.Truncated && second.Truncated);
        Assert.Equal(0, written);
        Assert.Equal(2, rig.Service.Calls.Count);
    }

    /// <summary>
    /// The items a service added past the limit are counted on a cached call as on the service's own answer, cold and from
    /// the record. A record answering a smaller limit answers as a service keeping to that limit would: the start of its
    /// list, none dropped (review of IO step 4: its items past the smaller limit were counted as dropped, 2 here).
    /// </summary>
    [Fact]
    public async Task TheItemsAServiceAddsPastTheLimit_AreCountedOnACachedCall()
    {
        var rig = new ServiceCacheRig();
        var client = new Client((_, response) =>
        {
            response.AddItem(1, 0);
            response.AddItem(2, 0);
            response.AddItem(3, 0);
            return ServiceStatus.Ok;
        });
        var memory = new RequestMemory();
        var context = Context(memory, CancellationToken.None, long.MaxValue);
        var plain = new ServiceResponse(memory, 2);
        var cold = new ServiceResponse(memory, 2);
        var hit = new ServiceResponse(memory, 2);
        var smaller = new ServiceResponse(memory, 1);

        await context.CallAsync(new BoundService("m", client, IoRoles.Recall, null), Query(2, 2), plain);
        await context.CallAsync(rig.With(client), Query(2, 2), cold);
        rig.Apply();
        await context.CallAsync(rig.With(client), Query(2, 2), hit);
        await context.CallAsync(rig.With(client), Query(2, 1), smaller);

        Assert.Equal((1, 1, 1, 0), (plain.Dropped, cold.Dropped, hit.Dropped, smaller.Dropped));
        Assert.Equal([1L], Ids(smaller));
        memory.Release();
    }

    /// <summary>
    /// A call about no items that reads named outputs is answered only by an answer to its own limit: an output or a text
    /// may say how many items it was asked for. A call that reads only its items still takes a longer list's start, or an
    /// exhausted one. A record of another limit answered with its outputs.
    /// </summary>
    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 6)]
    public async Task ACallReadingOutputs_IsAnsweredOnlyByAnAnswerToItsLimit(int first, int next)
    {
        var rig = new ServiceCacheRig();
        var client = new Client((call, response) =>
        {
            for (var i = 1; i <= Math.Min(3, call.Limit); i++)
            {
                response.AddItem(i, 0);
            }

            response.SetText("summary", "asked " + call.Limit);
            return ServiceStatus.Ok;
        });
        var memory = new RequestMemory();
        var context = Context(memory, CancellationToken.None, long.MaxValue);
        await context.CallAsync(rig.With(client), Query(2, first, "summary"), new ServiceResponse(memory, first));
        await context.CallAsync(rig.With(client), Query(3, first), new ServiceResponse(memory, first));
        rig.Apply();

        var reading = new ServiceResponse(memory, next);
        var listing = new ServiceResponse(memory, next);
        await context.CallAsync(rig.With(client), Query(2, next, "summary"), reading);
        await context.CallAsync(rig.With(client), Query(3, next), listing);

        Assert.True(reading.TryGetText("summary", out var summary));
        Assert.Equal("asked " + next, summary);
        Assert.Equal(Enumerable.Range(1, Math.Min(3, next)).Select(i => (long)i), Ids(listing)); // from the record
        Assert.Equal(3, client.Calls);
        memory.Release();
    }

    /// <summary>
    /// A missing input of a call about items whose shape is per item (its leading dimension the batch size) is missing on
    /// every row: the call of the items the cache missed sends it at their batch size, as a present one is cut to their
    /// rows. It was sent at the whole call's, and the service rejected the shape.
    /// </summary>
    [Fact]
    public async Task AMissingPerItemInput_IsSentAtTheBatchOfTheItemsTheCacheMissed()
    {
        var rig = new ServiceCacheRig();
        var client = new Client((call, response) =>
        {
            Assert.True(call.TryGetInput("m", out var input));
            if (!input.IsMissing || input.Shape[0] != call.BatchSize)
            {
                return ServiceStatus.Failed($"input shape [{input.Shape[0]}] for a batch of {call.BatchSize}");
            }

            response.GetValues<float>("p", call.BatchSize).Span.Fill(8);
            return ServiceStatus.Ok;
        });
        var memory = new RequestMemory();
        var context = Context(memory, CancellationToken.None, long.MaxValue);
        static ServiceCall Of(params long[] ids) => new()
        {
            Service = "m",
            Items = ids.Select(EntityKey.FromInt64).ToArray(),
            BatchSize = ids.Length,
            Inputs = new[] { NamedInput.FromMissing("m", TensorElementType.Float32, [ids.Length]) },
            Outputs = new[] { "p" },
        };

        Assert.True((await context.CallAsync(rig.With(client), Of(41, 42), new ServiceResponse(memory))).Succeeded);
        Assert.Equal(1, rig.Apply());
        var status = await context.CallAsync(rig.With(client), Of(41, 43), new ServiceResponse(memory));

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(2, client.Calls); // the second for item 43 alone
        memory.Release();
    }

    /// <summary>A missing input of a call about items has the shape of an input too: per item, or shared with leading dimension 1.</summary>
    [Fact]
    public async Task AMissingInputOfACallAboutItems_IsPerItemOrShared()
    {
        var memory = new RequestMemory();
        var call = new ServiceCall
        {
            Service = "m",
            Items = new[] { EntityKey.FromInt64(1), EntityKey.FromInt64(2) },
            BatchSize = 2,
            Inputs = new[] { NamedInput.FromMissing("v", TensorElementType.Float32, [3]) },
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Context(memory, CancellationToken.None, long.MaxValue).CallAsync(new ServiceCacheRig().Unbound, call, new ServiceResponse(memory)));

        Assert.Contains("Input 'v' has shape [3] in a call about 2 item(s)", error.Message, StringComparison.Ordinal);
        memory.Release();
    }

    /// <summary>
    /// Bindings that share a distributed store share answers only when they answer with the same client and settings: a
    /// binding whose settings changed (a hot reload, another deployment) does not read the answers the earlier one left.
    /// </summary>
    [Fact]
    public async Task BindingsSharingAStore_ShareAnswersOnlyWithTheSameIdentity()
    {
        var store = new FakeCacheStore();
        var query = Query(1, limit: 1, "answer");
        await new ServiceCacheRig(store: store, identity: "before").CallAsync(query);
        await store.WhenWrittenAsync(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var changed = new ServiceCacheRig(store: store, identity: "after");
        changed.Service.Offset = 100;
        var same = new ServiceCacheRig(store: store, identity: "before");
        same.Service.Offset = 100;
        var (_, fromChanged) = await changed.CallAsync(query);
        var (_, fromSame) = await same.CallAsync(query);

        Assert.True(fromChanged.TryGetText("answer", out var a));
        Assert.True(fromSame.TryGetText("answer", out var b));
        Assert.Equal(("a100", "a0"), (a, b));
        Assert.Single(changed.Service.Calls);
        Assert.Empty(same.Service.Calls);
    }

    /// <summary>
    /// A stale distributed record newer than the stale local one is kept for a failure of the service: when the service
    /// fails after the local record has left <c>stale_if_error_ms</c>, the newer one still answers, as it does for a
    /// reader with no local record (review of IO step 4). The local record hid it, and the call failed.
    /// </summary>
    [Fact]
    public async Task ANewerDistributedStaleRecord_AnswersWhenTheOlderLocalOneHasExpired()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new FakeCacheStore();
        var reader = new ServiceCacheRig(ttlMs: 1000, staleIfErrorMs: 5000, store: store);
        var query = Query(1, limit: 1);
        reader.Service.Have = [11];
        var first = await reader.CallAsync(query);
        Assert.Equal([11L], Ids(first.Response));
        Assert.Equal(1, reader.Apply());
        await store.WhenWrittenAsync(1).WaitAsync(Patience, token);

        var other = new ServiceCacheRig(ttlMs: 1000, staleIfErrorMs: 5000, store: store);
        other.Clock.AdvanceMs(3000);
        other.Service.Have = [22];
        var newer = await other.CallAsync(query);
        Assert.Equal([22L], Ids(newer.Response));
        await store.WhenWrittenAsync(2).WaitAsync(Patience, token);

        reader.Clock.AdvanceMs(5000); // the local record is stale, still within its window
        store.BeforeGet = _ =>
        {
            reader.Clock.AdvanceMs(2000); // past it by the time the read answers, the newer one is not
            return ValueTask.CompletedTask;
        };
        reader.Service.Fail = true;
        var (status, response) = await reader.CallAsync(query);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal([22L], Ids(response));
        Assert.NotNull(reader.Last!.DegradedReason);
    }

    /// <summary>
    /// A record whose header says what no response holds (more items than the limit it was asked, items dropped past a
    /// list that was not full) is never read: the service answers. It was read.
    /// </summary>
    [Theory]
    [InlineData("items past the limit asked", 0, 0)]
    [InlineData("dropped past a list not full", 3, 1)]
    public async Task ARecordWithAHeaderNoResponseHolds_IsNeverRead(string what, int limitAsked, int dropped)
    {
        _ = what;
        const int readLimit = 2;
        var token = TestContext.Current.CancellationToken;
        var store = new FakeCacheStore();
        var writer = new ServiceCacheRig(store: store);
        var written = await writer.CallAsync(Query(1, limit: 2));
        Assert.Equal([1L, 2L], Ids(written.Response));
        await store.WhenWrittenAsync(1).WaitAsync(Patience, token);
        Assert.True(store.Gets.TryDequeue(out var keys));
        Assert.True(store.TryGet(keys[0], out var record, out _));

        // The layout list after the prefix: its length, then kind, n, items (2 here), id kind, limit asked, flags, dropped.
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(CacheRecord.PrefixSize(7) + (5 * sizeof(int))), limitAsked);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(CacheRecord.PrefixSize(7) + (7 * sizeof(int))), dropped);
        store.Put(keys[0], record);

        var reader = new ServiceCacheRig(store: store);
        reader.Service.Have = [81];
        var (status, response) = await reader.CallAsync(Query(1, limit: readLimit));

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal([81L], Ids(response));
        Assert.Equal(0, response.Dropped);
        Assert.Single(reader.Service.Calls);
    }

    /// <summary>
    /// A call whose caller cancels while the cache reads it (a host's listener on the cache's metrics, here) is not answered
    /// from the cache: an answer is taken only if the node could still send the call when it is observed, as a service's
    /// answer is. It was answered.
    /// </summary>
    [Fact]
    public async Task ACallCancelledWhileTheCacheReadsIt_IsNotAnsweredFromIt()
    {
        var rig = new ServiceCacheRig();
        var call = Items([71], [0.1f]);
        await rig.CallAsync(call);
        Assert.Equal(1, rig.Apply());
        using var cancellation = new CancellationTokenSource();
        var armed = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, meterListener) =>
            {
                if (instrument.Name == StrataRecMetrics.Names.CacheKeys)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (Volatile.Read(ref armed) == 1)
            {
                cancellation.Cancel();
            }
        });
        listener.Start();
        var memory = new RequestMemory();
        var context = Context(memory, cancellation.Token, long.MaxValue);

        Volatile.Write(ref armed, 1);
        var thrown = await Record.ExceptionAsync(() => context.CallAsync(rig.Bound, call, new ServiceResponse(memory)));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Single(rig.Service.Calls); // the first call's
        memory.Release();
    }

    /// <summary>
    /// Stale values answer a call only if the node could still send it then, as an answer of the tiers alone is: a caller
    /// that cancelled while the distributed tier used up the service's time got them, and the node succeeded Degraded
    /// (review of IO step 4).
    /// </summary>
    [Fact]
    public async Task ACallCancelledWhileTheDistributedTierUsesUpTheServicesTime_IsNotAnsweredWithStaleValues()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new FakeCacheStore();
        var rig = new ServiceCacheRig(ttlMs: 1000, staleIfErrorMs: 5000, store: store, timeoutMs: 100, distributedTimeoutMs: 10000);
        var call = Query(1, limit: 1);
        Assert.True((await rig.CallAsync(call)).Status.Succeeded);
        Assert.Equal(1, rig.Apply());
        await store.WhenWrittenAsync(1).WaitAsync(Patience, token);
        rig.Clock.AdvanceMs(2000); // stale locally, within stale_if_error_ms
        store.BeforeGet = async _ => await Task.Delay(500, token); // past the service's 100 ms

        using var cancellation = new CancellationTokenSource();
        var armed = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, meterListener) =>
            {
                if (instrument.Name == StrataRecMetrics.Names.IoErrors)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var cache = false;
            foreach (var tag in tags)
            {
                cache |= tag.Key == StrataRecTags.IoRole && Equals(tag.Value, IoRoles.Cache);
            }

            if (cache && Volatile.Read(ref armed) == 1)
            {
                cancellation.Cancel(); // as the distributed read times out
            }
        });
        listener.Start();
        var memory = new RequestMemory();
        var context = Context(memory, cancellation.Token, long.MaxValue);

        Volatile.Write(ref armed, 1);
        var response = new ServiceResponse(memory, call.Limit);
        var thrown = await Record.ExceptionAsync(() => context.CallAsync(rig.Bound, call, response));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Null(context.DegradedReason);
        Assert.Single(rig.Service.Calls); // the first call's
        memory.Release();
    }

    private static OperatorContext Context(RequestMemory memory, CancellationToken token, long deadline) => new(new RequestState
    {
        Plan = null!,
        RequestId = "r",
        Inputs = new RequestInputs(),
        Seed = 1,
        Memory = memory,
        Table = null!,
        Bindings = null!,
        Context = [],
        Params = [],
        Features = null!,
        Counters = null!,
        DeadlineTimestamp = deadline,
        Arms = default,
        Variants = [],
    }, "n", Effects.ColumnWriter, new AttemptBudget(token, deadline));

    /// <summary>A service whose answer is a delegate, answering at once; counts its calls.</summary>
    private sealed class Client(Func<ServiceCall, IServiceResponse, ServiceStatus> answer) : IServiceClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(answer(call, response));
        }
    }
}
