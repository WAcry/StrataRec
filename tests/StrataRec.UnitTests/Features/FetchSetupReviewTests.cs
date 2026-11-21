using System.Collections.Immutable;
using System.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The review of 43bdf97: what setting up a fetch may do to its bound. Here with the allocation measurements, which run
/// alone: a test counts the process's live timers.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class FetchSetupReviewTests
{
    /// <summary>
    /// A pool that refuses a staging buffer fails the fetch without leaving its bound behind: no timer and no registration
    /// on the caller's token outlive it (review of 43bdf97: the bound was made before the staging, outside the code that
    /// releases it, and each refusal left a live timer for the request's deadline).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedStagingBuffer_LeavesNoBoundBehind(bool context)
    {
        const int attempts = 32;
        var pool = new RefusingPool();
        var connector = ScriptedConnector.Values((key, _) => (double)key);
        var rig = new FeatureTestRig().Source("test", connector, timeoutMs: 60_000);
        var feature = rig.Physical(context ? "user.pop" : "item.pop", context ? FeatureTestRig.User : FeatureTestRig.Item, ColumnType.Float64, "test");
        rig.Build();
        using var caller = new CancellationTokenSource();
        await RefuseOnce();
        var before = Timer.ActiveCount;

        for (var i = 0; i < attempts; i++)
        {
            await RefuseOnce();
        }

        Assert.Equal(attempts + 1, pool.Refused);
        Assert.Empty(connector.Calls);
        Assert.True(Timer.ActiveCount - before < attempts / 4, $"{Timer.ActiveCount - before} timers outlived {attempts} refused fetches");

        async Task RefuseOnce()
        {
            var request = rig.NewRequest([7], userId: 7, pool: pool, deadlineMs: 60_000);
            try
            {
                pool.Refuse = true;
                await Assert.ThrowsAsync<IOException>(async () =>
                {
                    if (context)
                    {
                        request.Fetcher.StartContextFetch([feature], caller.Token);
                        await request.Fetcher.ContextStoredTask(feature);
                    }
                    else
                    {
                        await request.Fetcher.EnsureAsync([feature], caller.Token);
                    }
                });
            }
            finally
            {
                request.Memory.Release();
            }
        }
    }

    /// <summary>
    /// A hook that is slow to say whether it replays does not move the fetch's deadline: the bound ends when the request's
    /// deadline or the source's timeout comes, taken with the budget (review of 43bdf97: the bound was made from the
    /// budget after the hook was asked, so its time was added, and the connector was called past the request's deadline).
    /// </summary>
    [Fact]
    public async Task ASlowHook_DoesNotMoveTheFetchsDeadline()
    {
        var connector = ScriptedConnector.Values((key, _) => (double)key);
        var rig = new FeatureTestRig().Source("test", connector, timeoutMs: 5000);
        var feature = rig.Physical("item.pop", FeatureTestRig.Item, ColumnType.Float64, "test");
        rig.Build();
        var hook = new SlowHook(TimeSpan.FromMilliseconds(500));
        var request = rig.NewRequest([7], deadlineMs: 250, hook: hook);
        try
        {
            var result = await request.EnsureAsync(feature);

            Assert.Equal(1, hook.Pauses);
            Assert.Empty(connector.Calls);
            Assert.False(hook.Sent);
            Assert.Equal(FeatureState.Failed, result.State);
        }
        finally
        {
            await request.Fetcher.WhenIoCompleteAsync();
            request.Memory.Release();
        }
    }

    private sealed class RefusingPool : IBufferPool
    {
        public bool Refuse { get; set; }

        public int Refused { get; private set; }

        public T[] Rent<T>(int minimumLength)
        {
            if (typeof(T) == typeof(double) && Refuse)
            {
                Refuse = false;
                Refused++;
                throw new IOException("the host's pool refused a staging buffer");
            }

            return SharedArrayBufferPool.Instance.Rent<T>(minimumLength);
        }

        public void Return<T>(T[] array, bool clearArray) => SharedArrayBufferPool.Instance.Return(array, clearArray);
    }

    /// <summary>Takes <paramref name="pause"/> the first time it is asked whether it replays; records what is sent.</summary>
    private sealed class SlowHook(TimeSpan pause) : IFeatureCallHook
    {
        public int Pauses { get; private set; }

        public bool? Sent { get; private set; }

        public bool Replays
        {
            get
            {
                if (Pauses == 0)
                {
                    Pauses++;
                    Thread.Sleep(pause);
                }

                return false;
            }
        }

        public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields) => 1;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values) => Sent = call.Sent;

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();
    }
}
