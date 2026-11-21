using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// How a replay answers a service call about no items, such as a recall's (design 13.4): by the digest of its inputs. The
/// node's own recorded call first, as recorded and consumed; else any successful call with the same inputs, consuming
/// nothing; else nothing, reported. A recorded answer answers only the inputs it was for.
/// </summary>
public sealed class ServiceCallReplayTests
{
    private static readonly RequestMemory Memory = new();

    [Fact]
    public void ANodesOwnCalls_AreReplayedInOrder_FailureIncluded_ThenTheSuccessfulAnswerToTheSameInputs()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42));
        run.ServiceCalls.Add(Recorded("a", Query(1), "timeout"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("42", true), Retrieve(replay, "a", Query(1)));
        Assert.Equal(("", false), Retrieve(replay, "a", Query(1))); // the recorded timeout
        Assert.Equal(("42", true), Retrieve(replay, "a", Query(1))); // a third call of the same inputs
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void ANodeWithoutACall_GetsTheAnswerToTheSameInputs_AndTakesItFromNobody()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42, 43));
        run.ServiceCalls.Add(Recorded("b", Query(2), "ok", 7));
        var replay = RunReplay.Create(run, "/runs/0");

        // A node the configuration added, asking what b asked: b's answer, though a's list is longer, and b keeps its own.
        Assert.Equal(("7", true), Retrieve(replay, "added", Query(2)));
        Assert.Equal(("7", true), Retrieve(replay, "b", Query(2)));
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void InputsNoCallHad_AreNotAnswered_AndReportedAsChangedForANodeThatHadACall()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("", false), Retrieve(replay, "a", Query(9)));
        Assert.Equal(("", false), Retrieve(replay, "other", Query(9)));

        Assert.Equal(
            new[] { ("recall", "s", (string?)null, "inputs_changed", 1, "a"), ("recall", "s", null, "not_recorded", 1, "other") },
            replay.Gaps.Select(g => (g.Kind, g.Source, g.Field, g.Reason, g.Count, g.Examples.Single())));
        Assert.Equal("recall s: 1 item(s) or call(s) have other inputs than recorded; not answered", replay.Gaps[0].Description);
    }

    [Fact]
    public void AMissingValue_IsNeitherZeroNorAbsent()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(null), "ok", 42));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "zero", Query(0)).Succeeded);
        Assert.False(Retrieve(replay, "none", new ServiceCall { Service = "s", Limit = 10 }).Succeeded);
        Assert.Equal(("42", true), Retrieve(replay, "missing", Query(null)));
    }

    [Fact]
    public void ARecordedFailure_AnswersOnlyItsOwnNode()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("e", Query(1), "error"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "added", Query(1)).Succeeded);
        Assert.Equal("not_recorded", Assert.Single(replay.Gaps).Reason);
        Assert.False(Retrieve(replay, "e", Query(1)).Succeeded); // as recorded: no new gap
        Assert.Single(replay.Gaps);
    }

    [Fact]
    public void FewerItemsAreThePrefix_AndMoreThanACallStoppedAtItsLimitHadIsAShortfall()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("full", Query(1, limit: 2), "ok", 1, 2));
        run.ServiceCalls.Add(Recorded("short", Query(2, limit: 5), "ok", 3, 4));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("1", true), Retrieve(replay, "full", Query(1, limit: 1)));
        Assert.Equal(("1,2", true), Retrieve(replay, "added", Query(1, limit: 3))); // the limit is not part of the inputs
        Assert.Equal(("3,4", true), Retrieve(replay, "short", Query(2, limit: 9))); // the service had no more

        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("recall", "limit_exceeds_recording", 1, "added"), (gap.Kind, gap.Reason, gap.Count, gap.Examples.Single()));
    }

    [Fact]
    public void ScoresNotOnePerId_AreRejectedAtTheirPointer()
    {
        var run = RunReplayTests.Run();
        var call = Recorded("a", Query(1), "ok", 1, 2);
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Node = call.Node, Service = call.Service, Role = call.Role, Limit = call.Limit, Status = call.Status, InputDigests = call.InputDigests,
            Ids = call.Ids, Scores = [1.0],
        });

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(run, "/runs/0"));

        Assert.Equal("/runs/0/service_calls/0/scores", error.Pointer);
    }

    /// <summary>A recall's call with one query value, <c>q</c>, as <c>MultiSourceRecall@1</c> sends it; null is missing.</summary>
    private static ServiceCall Query(double? q, int limit = 10) => new()
    {
        Service = "s",
        Limit = limit,
        Inputs = new[] { ServiceInputs.Create("q", ColumnType.Float64.AsNullable(), q is { } v ? ScalarValue.FromFloat64(v) : ScalarValue.Missing, Memory) },
    };

    private static ReplayServiceCall Recorded(string node, ServiceCall call, string status, params long[] ids) => new()
    {
        Node = node,
        Service = "s",
        Role = IoRoles.Recall,
        Limit = call.Limit,
        Status = status,
        InputDigests = [ReplayJson.Hex(ServiceInputDigest.Call(call, ServiceInputDigest.NameOrder(call)))],
        Ids = status == "ok" ? RunReplayTests.Json(JsonSerializer.Serialize(ids)) : null,
        Scores = status == "ok" ? [.. ids.Select(_ => 1.0)] : [],
    };

    /// <summary>The ids answered, comma-separated, and whether the call succeeded.</summary>
    private static (string Ids, bool Succeeded) Retrieve(RunReplay replay, string node, ServiceCall call)
    {
        var response = new ServiceResponse(Memory, call.Limit);
        var status = replay.Call(node, new IoTarget(IoRoles.Recall, "s"), call, response);
        return (string.Join(",", response.Keys.ToArray().Select(k => k.Int64Value)), status.Succeeded);
    }
}
