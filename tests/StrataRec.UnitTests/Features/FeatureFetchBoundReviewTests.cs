using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>The review of IO step 4 (one bounded wait): what a feature fetch keeps of a connector call that it no longer waits for.</summary>
public sealed class FeatureFetchBoundReviewTests
{
    /// <summary>
    /// A connector whose call the caller cancelled while its task was pending keeps the view it was handed bound to its
    /// own chunk: the wait ended at once, but the connector may still write through it. The fetch pointed the view at the
    /// next chunk (one key here), which it did not even send.
    /// </summary>
    /// <summary>
    /// A context feature whose connector call was abandoned fails without its staging being read: the connector may
    /// still be writing there (review of IO step 5: the value was read, and its list copied, before the failure was seen).
    /// </summary>
    [Fact]
    public async Task AnAbandonedContextConnector_FailsItsFeatures_WithoutItsStagingBeingRead()
    {
        using var caller = new CancellationTokenSource();
        var late = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new double[64 * 1024];
        long afterWriting = -1;
        var connector = new ScriptedConnector((_, _, writer, _) =>
        {
            writer.SetFloat64List(0, 0, payload);
            caller.Cancel();
            afterWriting = GC.GetAllocatedBytesForCurrentThread();
            return new ValueTask<FetchStatus>(late.Task);
        });
        var rig = new FeatureTestRig().Source("test", connector);
        var f = rig.Physical("user.vec", FeatureTestRig.User, ColumnType.List(StorageKind.Float64), "test");
        rig.Build();
        var request = rig.NewRequest([], userId: 1);

        try
        {
            request.Fetcher.StartContextFetch([f], caller.Token);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - afterWriting;
            await request.Fetcher.ContextStoredTask(f);

            Assert.False(late.Task.IsCompleted);
            Assert.Equal(FeatureState.Failed, request.Fetcher.ContextStoredState(f).State);
            Assert.True(afterWriting >= 0 && allocated < 64 * 1024, $"{allocated} B allocated after the abandoned connector wrote a list of 512 KiB");
        }
        finally
        {
            late.TrySetResult(FetchStatus.Ok);
            await request.Fetcher.WhenIoCompleteAsync();
            request.Memory.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConnectorCutOffByTheCaller_KeepsItsWriterBoundToItsOwnChunk(bool cached)
    {
        using var caller = new CancellationTokenSource();
        var finish = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        IFeatureBatchWriter? first = null;
        var connector = new ScriptedConnector((keys, _, writer, _) =>
        {
            Assert.Equal(2, keys.Count);
            first = writer;
            caller.Cancel();
            return new ValueTask<FetchStatus>(finish.Task);
        });
        var rig = new FeatureTestRig();
        if (cached)
        {
            rig.CacheSource("test", connector, maxBatch: 2);
        }
        else
        {
            rig.Source("test", connector, maxBatch: 2);
        }

        var feature = rig.Physical("item.test", FeatureTestRig.Item, ColumnType.Float64, "test");
        rig.Build();
        var request = rig.NewRequest([1, 2, 3]);
        try
        {
            try
            {
                await request.Fetcher.EnsureAsync([feature], caller.Token);
            }
            catch (OperationCanceledException)
            {
                // The fetch may end cancelled; what matters is the view the connector still holds.
            }

            Assert.NotNull(first);
            Assert.Single(connector.Calls);
            Assert.False(finish.Task.IsCompleted);
            Assert.Equal(2, first.KeyCount); // still its own two keys
            first.GetValues<double>(0).Span[1] = 42;
            first.SetValid(0, 1);
        }
        finally
        {
            finish.TrySetResult(FetchStatus.Ok);
            await request.Fetcher.WhenIoCompleteAsync();
            request.Memory.Release();
        }
    }

    /// <summary>
    /// What a connector threw is read once, by the shared wait: the fetch fails with that text. The fetch read the
    /// exception's message again, and one that could not be read twice escaped from the fetch.
    /// </summary>
    [Theory]
    [InlineData("candidate")]
    [InlineData("context")]
    [InlineData("cache")]
    public async Task AConnectorsException_IsReadOnce(string mode)
    {
        var failure = new SingleReadMessageException();
        var connector = new ScriptedConnector((_, _, _, _) => throw failure);
        var rig = new FeatureTestRig();
        if (mode == "cache")
        {
            rig.CacheSource("test", connector);
        }
        else
        {
            rig.Source("test", connector);
        }

        var context = mode == "context";
        var feature = context
            ? rig.Physical("user.test", FeatureTestRig.User, ColumnType.Float64, "test")
            : rig.Physical("item.test", FeatureTestRig.Item, ColumnType.Float64, "test");
        rig.Build();
        var request = rig.NewRequest([1], userId: 7);
        try
        {
            if (context)
            {
                request.Fetcher.StartContextFetch([feature], CancellationToken.None);
                await request.Fetcher.ContextStoredTask(feature);
                Assert.Equal(FeatureState.Failed, request.Fetcher.ContextStoredState(feature).State);
            }
            else
            {
                Assert.Equal(FeatureState.Failed, (await request.EnsureAsync(feature)).State);
            }

            Assert.Equal(1, failure.Reads);
        }
        finally
        {
            request.Memory.Release();
        }
    }

    private sealed class SingleReadMessageException : Exception
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override string Message => Interlocked.Increment(ref _reads) == 1
            ? "The connector is down."
            : throw new InvalidOperationException("The message was read again.");
    }
}
