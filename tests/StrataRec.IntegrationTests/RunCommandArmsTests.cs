using System.Text.Json;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

public sealed class RunCommandArmsTests
{
    private const string Strong = "exp_quality_weight/strong";
    private const string Control = "exp_quality_weight/control";

    public static TheoryData<string?, string?, string[], string> Matrix() => new()
    {
        { null, null, Array.Empty<string>(), "none" },
        { null, "[]", Array.Empty<string>(), "external" },
        { null, $"[\"{Strong}\"]", new[] { Strong }, "external" },
        { "[]", null, Array.Empty<string>(), "forced" },
        { "[]", "[]", Array.Empty<string>(), "forced" },
        { "[]", $"[\"{Strong}\"]", Array.Empty<string>(), "forced" },
        { $"[\"{Control}\"]", null, new[] { Control }, "forced" },
        { $"[\"{Control}\"]", "[]", new[] { Control }, "forced" },
        { $"[\"{Control}\"]", $"[\"{Strong}\"]", new[] { Control }, "forced" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ForceArmsAndAssignment_InARequestFile_KeepTheirPresence(string? forceArms, string? assignment, string[] expected, string source)
    {
        var (arms, recorded) = await Run(forceArms, assignment, Array.Empty<string>());

        Assert.Equal(expected, arms);
        Assert.Equal(source, recorded);
    }

    [Fact]
    public async Task ArmsOnTheCommandLine_WinOverTheFilesForceArms_EvenAnEmptyOne()
    {
        var (arms, source) = await Run("[]", $"[\"{Strong}\"]", new[] { "exp_mmr_diversity/mmr" });

        Assert.Equal(new[] { "exp_mmr_diversity/mmr" }, arms);
        Assert.Equal("forced", source);
    }

    private static async Task<(string[] Arms, string Source)> Run(string? forceArms, string? assignment, string[] cliArms)
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-run-arms-").FullName;
        try
        {
            var members = new List<string>
            {
                "\"pipeline_id\": \"home_feed\"",
                "\"request_id\": \"req-arms\"",
                "\"inputs\": { \"user_id\": 1001, \"session_id\": \"s-1\", \"surface\": \"home\" }",
                $"\"deadline_ms\": {HomeFeedTestHost.UntimedMs}",
            };
            if (forceArms is not null)
            {
                members.Add("\"force_arms\": " + forceArms);
            }

            if (assignment is not null)
            {
                members.Add("\"assignment\": " + assignment);
            }

            var request = Path.Combine(directory, "request.json");
            await File.WriteAllTextAsync(request, "{ " + string.Join(", ", members) + " }");
            var bundle = Path.Combine(directory, "bundle.json");
            var output = new StringWriter();

            var code = await RunCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "local", request, cliArms, false, output, CancellationToken.None, recordPath: bundle);

            Assert.Equal(0, code);
            using var result = JsonDocument.Parse(output.ToString());
            var arms = result.RootElement.GetProperty("experiments").GetProperty("assigned").EnumerateArray().Select(a => a.GetString()!).ToArray();
            return (arms, ReplayBundleFile.Load(bundle).Request.Assignment.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
