using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Review 80: a host's metric listener that throws when a feature call completes loses its measurement. The call is
/// recorded with its outcome and values and the fill gets them: the call site completed the call (metrics, health, span)
/// before recording it, so the throw left a call that had answered unrecorded, outstanding in a recording, and threw the
/// listener's exception out of the fill. Both ways a call is found answered: at once, and after a wait.
/// </summary>
[Collection(MetricMeasurement.Name)]
public sealed class CallSiteListenerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFetchWhoseListenerThrows_IsRecorded_AndFillsItsRows(bool synchronously)
    {
        var connector = synchronously
            ? ScriptedConnector.Values((key, _) => key * 1.5)
            : new ScriptedConnector(async (keys, fields, writer, _) =>
            {
                await Task.Yield();
                ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.5);
                return FetchStatus.Ok;
            });
        var rig = new FeatureTestRig().Source("review80_store", connector);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "review80_store");
        rig.Build();
        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.SourceDuration, (StrataRecTags.Source, "review80_store"));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1, 2], hook: hook);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(3.0, request.Value(score, 1).AsFloat64());
        var call = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Ok, call.Outcome);
        Assert.Equal(new double?[] { 1.5, 3.0 }, call.Values);
        Assert.Equal(1, listener.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }
}
