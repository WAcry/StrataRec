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
          "format": "stratarec.replay_bundle/3",
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

    [Theory]
    [InlineData("/request")]
    [InlineData("/request/pipeline_id")]
    [InlineData("/request/assignment/arms")]
    [InlineData("/runs")]
    [InlineData("/runs/0")]
    [InlineData("/runs/0/nodes")]
    [InlineData("/runs/0/feature_calls/0/fields")]
    [InlineData("/runs/0/feature_calls/0/fields/0")]
    [InlineData("/runs/0/feature_calls/0/fields/0/name")]
    [InlineData("/result/items/0")]
    [InlineData("/result/column_types/score")]
    public void ANullWhereAValueIsRequired_IsRejectedAtItsPointer(string pointer)
    {
        var json = Edit(b =>
        {
            b["request"]!["assignment"]!["arms"] = new JsonArray("e/a");
            b["runs"]![0]!["nodes"] = new JsonArray(JsonNode.Parse("""{ "node": "n", "outcome": "succeeded" }"""));
            var parent = Locate(b, pointer[..pointer.LastIndexOf('/')]);
            var token = pointer[(pointer.LastIndexOf('/') + 1)..];
            if (parent is JsonArray array)
            {
                array[int.Parse(token)] = null;
            }
            else
            {
                parent[token] = null;
            }
        });

        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(json));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains("must not be null", error.Reason);
        Assert.Contains("stratarec schema replay_bundle", error.Hint);
    }

    [Fact]
    public void NullsWhereTheSchemaAllowsThem_AreAccepted()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b =>
        {
            b["result"]!["error"] = null;
            b["result"]!["items"]![0]!["columns"]!["score"] = null; // a missing value
            b["request"]!["inputs"] = JsonNode.Parse("""{ "user_id": { "type": "int64?", "value": null } }""");
            b["runs"]![0]!["feature_calls"]![0]!["error"] = null;
        }));

        Assert.True(ComparableResult.From(bundle.Result, "/result").Columns[0]["score"].IsMissing);
    }

    private static JsonNode Locate(JsonNode root, string pointer)
    {
        var node = root;
        foreach (var token in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonArray array ? array[int.Parse(token)]! : node[token]!;
        }

        return node;
    }

    [Fact]
    public void OtherFormat_IsRejectedAtFormat()
    {
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b => b["format"] = "stratarec.replay_bundle/9")));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("record the request again", error.Hint);
    }

    [Fact]
    public void AnOlderFormat_IsRejectedAtFormat_BeforeItsLayoutIsReadAsThisOne()
    {
        // A version 2 bundle recorded a thrown exception type as a string; version 3 reads an object there.
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b =>
        {
            b["format"] = "stratarec.replay_bundle/2";
            b["runs"]![0]!["model_calls"] = JsonNode.Parse("""
                [{ "seq": 1, "node": "rank", "model": "m", "batch_size": 1, "status": "error", "threw": true,
                   "error_type": "System.InvalidOperationException", "input_digests": ["00"] }]
                """);
        })));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("stratarec.replay_bundle/2", error.Message);
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
