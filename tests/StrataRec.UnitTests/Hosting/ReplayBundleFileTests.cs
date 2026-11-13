using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Hosting;

/// <summary>Malformed replay bundles are rejected with the JSON pointer of the problem and a hint (design 13.4).</summary>
public sealed class ReplayBundleFileTests
{
    private const string Minimal = """
        {
          "format": "stratarec.replay_bundle/2",
          "request": { "pipeline_id": "p", "request_id": "r", "seed": 1, "time_ms": 0, "deadline_ms": 100, "assignment": { "source": "none" } },
          "manifest": { "id": "m" },
          "runs": [
            {
              "pipeline_id": "p", "manifest_id": "m", "role": "primary",
              "feature_calls": [
                { "seq": 0, "source": "item_store", "entity": "item", "shape": "many", "keys": [1, 2], "status": "ok",
                  "fields": [{ "name": "score", "type": "float64" }], "values": { "score": [0.5, null] } }
              ],
              "sampling": { "logs": "none" },
              "result": { "succeeded": true }
            }
          ],
          "result": { "served_by": "p", "succeeded": true, "column_types": { "score": "float64" }, "items": [{ "id": 1, "rank": 1, "columns": { "score": 0.5 } }] }
        }
        """;

    [Fact]
    public void MinimalBundle_Parses_AndItsRunIndexes()
    {
        var bundle = ReplayBundleFile.Parse(Minimal);

        Assert.Equal(ReplayBundle.CurrentFormat, bundle.Format);
        var replay = RunReplay.Create(bundle.Runs[0], "/runs/0");
        Assert.Empty(replay.Gaps);
        Assert.Single(ComparableResult.From(bundle.Result, "/result").Ids);
    }

    [Fact]
    public void NonFiniteCandidateScores_RoundTripAsTheirNames()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["candidate_calls"] = JsonNode.Parse(
            """[{ "seq": 1, "node": "recall", "source": "s", "limit": 3, "status": "ok", "ids": [1, 2, 3], "scores": ["NaN", "Infinity", 0.5] }]""")));
        bundle.Runs[0].CandidateCalls[0].Scores.Add(double.NegativeInfinity);

        var json = ReplayBundleFile.Serialize(bundle);

        Assert.Contains("\"-Infinity\"", json);
        Assert.Equal(new[] { double.NaN, double.PositiveInfinity, 0.5, double.NegativeInfinity }, ReplayBundleFile.Parse(json).Runs[0].CandidateCalls[0].Scores);
    }

    [Fact]
    public void OtherFormat_IsRejectedAtFormat()
    {
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b => b["format"] = "stratarec.replay_bundle/9")));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("record the request again", error.Hint);
    }

    [Fact]
    public void UnknownProperty_IsRejectedWithItsPointer_AndPointsToTheSchema()
    {
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b => b["request"]!["sede"] = 1)));

        Assert.StartsWith("/request", error.Pointer);
        Assert.Contains("stratarec schema replay_bundle", error.Hint);
    }

    [Fact]
    public void ValueThatDoesNotFitItsRecordedType_IsRejectedAtTheValue()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["values"]!["score"]![1] = "high"));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/values/score/1", error.Pointer);
    }

    [Fact]
    public void ValuesOfTheWrongLength_AreRejectedAtTheField()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["values"]!["score"] = new JsonArray(0.5)));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/values/score", error.Pointer);
    }

    [Fact]
    public void UnknownStatus_IsRejectedWithTheAcceptedSpellings()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["status"] = "fine"));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/status", error.Pointer);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void ResultColumnWithoutAType_IsRejectedAtTheColumn()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["result"]!["column_types"] = new JsonObject()));

        var error = Assert.Throws<ReplayBundleException>(() => ComparableResult.From(bundle.Result, "/result"));

        Assert.Equal("/result/items/0/columns/score", error.Pointer);
    }

    private static string Edit(Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(Minimal)!;
        edit(node);
        return node.ToJsonString();
    }
}
