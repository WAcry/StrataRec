using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Features;
using static StrataRec.UnitTests.Pipeline.ServiceCacheRig;

namespace StrataRec.UnitTests.Pipeline;

public sealed class ServiceCacheRecordTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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
        Assert.Equal([81f], Floats(answered, "p"));
        Assert.Single(reader.Service.Calls);
    }

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
        Assert.Single(rig.Service.Calls);
        memory.Release();
    }

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
        Assert.Equal(2, rig.Service.Calls.Count);
    }

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
        Assert.Equal(x.Select(v => v * 10), Floats(response, "p"));
        Assert.Single(rig.Service.Calls);
        Assert.All(new[] { wide.Reads - before.Item1, small.Reads - before.Item2, text.Reads - before.Item3 }, reads => Assert.InRange(reads, 1, 4));
    }

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

    [Fact]
    public async Task AStaleRecordPastItsWindowWhenTheServiceFails_DoesNotAnswer()
    {
        var store = new FakeCacheStore();
        var rig = new ServiceCacheRig(ttlMs: 1000, staleIfErrorMs: 5000, store: store);
        var query = Query(1, limit: 1);
        Assert.True((await rig.CallAsync(query)).Status.Succeeded);
        Assert.Equal(1, rig.Apply());
        rig.Clock.AdvanceMs(2000);
        store.BeforeGet = _ =>
        {
            rig.Clock.AdvanceMs(5000);
            return ValueTask.CompletedTask;
        };
        rig.Service.Fail = true;

        var (status, _) = await rig.CallAsync(query);

        Assert.Equal(ServiceStatus.Failed("m is down"), status);
        Assert.Null(rig.Last!.DegradedReason);
    }

    /// <summary>
    /// A string slot the service left unwritten (null) is served as it is and not cached: a record has no null string.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStringSlotLeftUnwritten_IsServed_AndNotCached(bool aboutItems)
    {
        var rig = new ServiceCacheRig();
        var client = new Client((_, response) =>
        {
            response.GetValues<string>("s", 1).Span[0] = null!;
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
    /// not any item's, so a later call of the items (alone or with others) asks the service again.
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
    /// The items a service added past the limit are counted on a cached call as on the service's own answer, cold and
    /// from the record. A record answering a smaller limit answers as a service keeping to that limit would: the start
    /// of its list, none dropped.
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
    /// A call about no items that reads named outputs is answered only by an answer to its own limit: an output or a
    /// text may say how many items it was asked for. A call that reads only its items still takes a longer list's
    /// start, or an exhausted one.
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
        Assert.Equal(Enumerable.Range(1, Math.Min(3, next)).Select(i => (long)i), Ids(listing));
        Assert.Equal(3, client.Calls);
        memory.Release();
    }

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
        Assert.Equal(2, client.Calls);
        memory.Release();
    }

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

        reader.Clock.AdvanceMs(5000);
        store.BeforeGet = _ =>
        {
            reader.Clock.AdvanceMs(2000);
            return ValueTask.CompletedTask;
        };
        reader.Service.Fail = true;
        var (status, response) = await reader.CallAsync(query);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal([22L], Ids(response));
        Assert.NotNull(reader.Last!.DegradedReason);
    }

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
    /// A call whose caller cancels while the cache reads it (a host's listener on the cache's metrics, here) is not
    /// answered from the cache: an answer is taken only if the node could still send the call when it is observed, as a
    /// service's answer is.
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
        Assert.Single(rig.Service.Calls);
        memory.Release();
    }

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
        rig.Clock.AdvanceMs(2000);
        store.BeforeGet = async _ => await Task.Delay(500, token);

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
        Assert.Single(rig.Service.Calls);
        memory.Release();
    }

    [Fact]
    public async Task ACallAboutItems_ThroughACacheThatKeepsNothing_TakesWhatACallWithoutACacheTakes()
    {
        var options = new LocalFeatureTableOptions
        {
            MaxBytes = 1 << 20, Cache = new CacheTableOptions { TtlMs = CacheSettings.MaxMs, Memory = false, Kept = false, Clock = new ManualClock() },
        };
        var table = new LocalFeatureTable(ServiceCache.Schema("m"), options);
        var cache = new ServiceCache("m", table, table.Cache!.Policy, null, 10_000, "m@1");
        var service = new CountingService();
        var rented = new List<long>();
        foreach (var bound in new[] { new BoundService("m", service, IoRoles.Model, null, null), new BoundService("m", service, IoRoles.Model, null, cache) })
        {
            var pool = new CountingPool();
            var memory = new RequestMemory(pool);
            var response = new ServiceResponse(memory);

            // With reads to share through, as every request an engine runs has: what makes a call about no items take the cache's path.
            var context = Context(memory, TestContext.Current.CancellationToken, long.MaxValue, new RequestReads());
            var status = await context.CallAsync(bound, ServiceCacheRig.Items([11, 22, 33], [1f, 2f, 3f]), response);

            Assert.True(status.Succeeded, status.Error);
            Assert.Equal(3, ServiceCacheRig.Floats(response, "p").Length);
            rented.Add(pool.Rented);
            memory.Release();
        }

        Assert.Equal(2, service.Calls.Count);
        Assert.Equal(rented[0], rented[1]);
    }

    private sealed class CountingPool : IBufferPool
    {
        private long _rented;

        public long Rented => Interlocked.Read(ref _rented);

        public T[] Rent<T>(int minimumLength)
        {
            Interlocked.Add(ref _rented, minimumLength);
            return SharedArrayBufferPool.Instance.Rent<T>(minimumLength);
        }

        public void Return<T>(T[] array, bool clearArray) => SharedArrayBufferPool.Instance.Return(array, clearArray);
    }

    [Fact]
    public async Task ACallThatOutlivesItsRequest_WritesItsRecordUnderTheKeyItReadBy()
    {
        var rig = new ServiceCacheRig();
        var held = new HeldClient(rig.Service);
        var memory = new RequestMemory();
        var call = Context(memory, TestContext.Current.CancellationToken, long.MaxValue)
            .CallAsync(rig.With(held), ServiceCacheRig.Items([1, 2], [0.1f, 0.2f]), new ServiceResponse(memory));
        await held.Entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        memory.Release(); // the request returned: what it rented is its calls' alone
        held.Release.SetResult();
        var first = await call.WaitAsync(Patience, TestContext.Current.CancellationToken);
        rig.Apply();
        var (again, answer) = await rig.CallAsync(ServiceCacheRig.Items([1, 2], [0.1f, 0.2f]));

        Assert.True(first.Succeeded && again.Succeeded, first.Error ?? again.Error);
        Assert.Equal([1f, 2f], Floats(answer, "p"));
        Assert.Single(rig.Service.Calls);
    }

    private sealed class HeldClient(IServiceClient inner) : IServiceClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return await inner.CallAsync(call, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static OperatorContext Context(RequestMemory memory, CancellationToken token, long deadline, RequestReads? reads = null) => new(new RequestState
    {
        Reads = reads,
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
