using System.Text.Json;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec run --format text</c> on the home_feed sample: the result for reading, the same items in the same order as
/// the JSON document, and what the request asked for (trace, explained candidates) in replay's layout.
/// </summary>
public sealed class RunTextTests
{
    private static readonly string Sample = HomeFeedTestHost.SampleRoot();

    private static readonly string Request = Path.Combine(Sample, "requests", "user_1001.json");

    [Fact]
    public async Task Text_ListsTheItemsOfTheJsonDocument_OnePerLine_ThenTheTraceAndTheExplanation()
    {
        var json = new StringWriter();
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, Sample, "local", Request, Array.Empty<string>(), false, json, CancellationToken.None,
            deadlineMs: 5000));
        var text = new StringWriter();

        var code = await RunCommand.RunAsync(Tool.Environment, Sample, "local", Request, Array.Empty<string>(), true, text, CancellationToken.None,
            deadlineMs: 5000, explain: new[] { "17" }, format: "text");

        Assert.Equal(0, code);
        var lines = text.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith("request req-1001: succeeded, served by home_feed, ", lines[0], StringComparison.Ordinal);
        Assert.Contains("  manifest  workspace:home_feed/local", lines);

        using var document = JsonDocument.Parse(json.ToString());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var itemLines = lines.SkipWhile(l => !l.StartsWith("  items     ", StringComparison.Ordinal)).Take(items.Count).ToList();
        Assert.Equal(items.Count, itemLines.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].GetProperty("id").GetInt64();
            var category = items[i].GetProperty("columns").GetProperty("category").GetString();
            Assert.Matches($@"^ *{i + 1}\. {id} +category '{category}', primary_source '", itemLines[i][12..]); // past the label column
        }

        Assert.Contains(lines, l => l.StartsWith("  trace     recall: succeeded, ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  columns   ", StringComparison.Ordinal) && l.Contains("score <- ", StringComparison.Ordinal));
        Assert.Contains("  explain   17: dropped by safety_filter", lines);
        Assert.Contains("              not live in 7 other nodes", lines); // the nodes after the filter, in one line
    }

    [Fact]
    public async Task Text_AFailedRequest_SaysItsKindAndError()
    {
        var text = new StringWriter();

        var code = await RunCommand.RunAsync(Tool.Environment, Sample, "local", Request, new[] { "exp_nope/arm" }, false, text, CancellationToken.None,
            deadlineMs: 5000, format: "text");

        Assert.Equal(1, code);
        var lines = text.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith("request req-1001: failed (invalid_request), ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  error     ", lines[1], StringComparison.Ordinal);
        Assert.Contains("exp_nope/arm", lines[1], StringComparison.Ordinal);
        Assert.Contains("  items     none", lines);
    }
}
