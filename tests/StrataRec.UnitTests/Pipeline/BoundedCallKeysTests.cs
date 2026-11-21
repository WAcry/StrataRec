using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// <c>stratarec.io.keys</c> counts the items of a call sent: one whose bound passed before it was sent took no keys to
/// the dependency, and counts its timeout only (it counted 7 keys of a call never sent, verification of IO step 2).
/// </summary>
[Collection(MetricMeasurement.Name)]
public sealed class BoundedCallKeysTests
{
    [Fact]
    public async Task ACallNotSent_CountsNoKeys_ButItsTimeout_AndACallSentCountsItsKeys()
    {
        var target = new IoTarget("check", "keys_probe");
        using var keys = new MeasurementHook(StrataRecMetrics.Names.IoKeys, static () => { }, (StrataRecTags.IoTarget, "keys_probe"));
        using var errors = new MeasurementHook(StrataRecMetrics.Names.IoErrors, static () => { }, (StrataRecTags.IoTarget, "keys_probe"));
        var memory = new RequestMemory();

        var passed = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() - 1);
        var unsent = await BoundedCall.RunAsync<int, Keyed>(default, passed, memory, target, null, "req", null);
        passed.Release();

        Assert.Equal((CallEnd.BudgetRanOut, 0, 1), (unsent.End, keys.Calls, errors.Calls));

        var open = new AttemptBudget(CancellationToken.None, long.MaxValue);
        var sent = await BoundedCall.RunAsync<int, Keyed>(default, open, memory, target, null, "req", null);
        open.Release();

        Assert.Equal((CallEnd.Answered, 1, 1), (sent.End, keys.Calls, errors.Calls));
    }

    /// <summary>A call about 7 items that answers at once.</summary>
    private readonly struct Keyed : IBoundedCall<int>
    {
        public ValueTask<int> Send(CancellationToken token) => ValueTask.FromResult(1);

        public bool Succeeded(int value, out string? error)
        {
            error = null;
            return true;
        }

        public int Keys => 7;

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
        }
    }
}
