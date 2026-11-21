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

    /// <summary>
    /// Two nodes asked the same query for 1 and 3 items; a node that replaces them asks for 3. It borrowed the first call
    /// with that digest, the shorter one, and reported a shortfall the bundle could answer (review of IO step 2).
    /// </summary>
    [Fact]
    public void ANodeWithoutACall_BorrowsTheSameInputsAnswerThatAnswersIt_NotTheFirst()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("one", Query(1, limit: 1), "ok", 7));
        run.ServiceCalls.Add(Recorded("three", Query(1, limit: 3), "ok", 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("7,8,9", true), Retrieve(replay, "added", Query(1, limit: 3)));
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// A recorded call that asked for no items says nothing about the items the service has: it does not answer a call
    /// asking for 3, which another call with the same inputs answers, nor, alone, the same node asking for 3 now (a shortfall
    /// the recording cannot tell, <c>limit_exceeds_recording</c>). It answered both with no items and no gap (verification
    /// of IO step 2).
    /// </summary>
    [Fact]
    public void ACallThatAskedForNoItems_AnswersNoCallThatAsksForSome()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("none", Query(1, limit: 0), "ok"));
        run.ServiceCalls.Add(Recorded("three", Query(1, limit: 3), "ok", 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("7,8,9", true), Retrieve(replay, "added", Query(1, limit: 3)));
        Assert.Empty(replay.Gaps);

        var alone = RunReplayTests.Run();
        alone.ServiceCalls.Add(Recorded("none", Query(1, limit: 0), "ok"));
        var raised = RunReplay.Create(alone, "/runs/0");

        Assert.Equal(("", true), Retrieve(raised, "none", Query(1, limit: 3)));
        var gap = Assert.Single(raised.Gaps);
        Assert.Equal(("limit_exceeds_recording", 3), (gap.Reason, gap.Count));
    }

    /// <summary>
    /// Of two partial answers with as much of what a call reads, the one that can tell its items is borrowed: both answered
    /// text <c>a</c> and no items, the second asked for 3 (the service had none), the first for 0 (it says nothing). The
    /// first was borrowed and the replay reported 3 items it could not tell besides the missing <c>b</c> (verification 2 of
    /// IO step 2). Since the review of IO step 3 a call reading outputs is digested with its limit, so the first is not even
    /// a candidate: its text may say it was asked for none.
    /// </summary>
    [Fact]
    public void OfPartialAnswersAsGood_TheOneThatCanTellItsItems_IsBorrowed()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("none", Reading(Query(1, limit: 0), "a"), "ok", texts: new() { ["a"] = "A" }));
        run.ServiceCalls.Add(Recorded("three", Reading(Query(1, limit: 3), "a"), "ok", texts: new() { ["a"] = "A" }));
        var replay = RunReplay.Create(run, "/runs/0");
        var asked = Reading(Query(1, limit: 3), "a", "b");

        var status = replay.Call("added", new IoTarget(IoRoles.Recall, "s"), asked, new ServiceResponse(Memory, asked.Limit), out _);

        Assert.True(status.Succeeded);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("not_recorded", "b"), (gap.Reason, gap.Field));
    }

    /// <summary>The same for outputs: a node reading <c>b</c> borrows the call with the same inputs that has it.</summary>
    [Fact]
    public void ANodeWithoutACall_BorrowsTheSameInputsAnswerWithTheOutputItReads()
    {
        var run = RunReplayTests.Run();
        var asked = Query(1);
        run.ServiceCalls.Add(Recorded("first", Reading(asked, "a"), "ok", texts: new() { ["a"] = "A" }));
        run.ServiceCalls.Add(Recorded("second", Reading(asked, "b"), "ok", texts: new() { ["b"] = "B" }));
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, asked.Limit);

        var status = replay.Call("added", new IoTarget(IoRoles.Recall, "s"), Reading(asked, "b"), response, out _);

        Assert.True(status.Succeeded);
        Assert.True(response.TryGetText("b", out var text));
        Assert.Equal("B", text);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>A node's recorded failure, replayed once, leaves its next call of the same inputs unrecorded: not "inputs changed".</summary>
    [Fact]
    public void ANodesCallsOfTheSameInputsUsedUp_AreNotRecorded_NotChanged()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "error"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "a", Query(1)).Succeeded); // as recorded
        Assert.False(Retrieve(replay, "a", Query(1)).Succeeded);

        Assert.Equal(("not_recorded", 1), (Assert.Single(replay.Gaps).Reason, replay.Gaps[0].Count));
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

    private static ReplayServiceCall Recorded(string node, ServiceCall call, string status, params long[] ids) => Recorded(node, call, status, null, ids);

    private static ReplayServiceCall Recorded(string node, ServiceCall call, string status, Dictionary<string, string>? texts, params long[] ids) => new()
    {
        Texts = texts ?? new(),
        Node = node,
        Service = "s",
        Role = IoRoles.Recall,
        Limit = call.Limit,
        Status = status,
        InputDigests = [ReplayJson.Hex(ServiceInputDigest.Call(call, ServiceInputDigest.NameOrder(call)))],
        Ids = status == "ok" ? RunReplayTests.Json(JsonSerializer.Serialize(ids)) : null,
        Scores = status == "ok" ? [.. ids.Select(_ => 1.0)] : [],
    };

    private static ServiceCall Reading(ServiceCall call, params string[] outputs) => new()
    {
        Service = call.Service, Inputs = call.Inputs, Limit = call.Limit, Outputs = outputs,
    };

    /// <summary>The ids answered, comma-separated, and whether the call succeeded.</summary>
    private static (string Ids, bool Succeeded) Retrieve(RunReplay replay, string node, ServiceCall call)
    {
        var response = new ServiceResponse(Memory, call.Limit);
        var status = replay.Call(node, new IoTarget(IoRoles.Recall, "s"), call, response, out _);
        return (string.Join(",", response.Keys.ToArray().Select(k => k.Int64Value)), status.Succeeded);
    }
}
