using StrataRec.Core.Threading;
using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Core;

[Collection(MetricMeasurement.Name)]
public sealed class BoundedCallKeysTests
{
    [Fact]
    public async Task ACallNotSent_CountsNothing_IsRecordedAsNotSent_AndACallSentCountsItsKeys()
    {
        var target = new IoTarget("check", "keys_probe");
        using var keys = new MeasurementHook(StrataRecMetrics.Names.IoKeys, static () => { }, (StrataRecTags.IoTarget, "keys_probe"));
        using var calls = new MeasurementHook(StrataRecMetrics.Names.IoDuration, static () => { }, (StrataRecTags.IoTarget, "keys_probe"));
        var memory = new RequestMemory();

        var passed = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() - 1);
        var unsent = await BoundedCall.RunAsync<int, Keyed>(default, passed, memory, target, CallDeadline.None);
        passed.Release();

        Assert.Equal((CallEnd.BudgetRanOut, false, 0, 0), (unsent.End, unsent.Sent, keys.Calls, calls.Calls));

        var open = new AttemptBudget(CancellationToken.None, long.MaxValue);
        var sent = await BoundedCall.RunAsync<int, Keyed>(default, open, memory, target, CallDeadline.None);
        open.Release();

        Assert.Equal((CallEnd.Answered, true, 1, 1), (sent.End, sent.Sent, keys.Calls, calls.Calls));
    }

    private readonly struct Keyed : IBoundedCall<int>
    {
        public ValueTask<int> Send(CancellationToken token) => ValueTask.FromResult(1);

        public IoOutcome Judge(int value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        public void Pending(Task done)
        {
        }

        public int Keys => 7;

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
        }
    }
}
