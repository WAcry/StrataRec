using System.Text.Json;
using StrataRec.Cli.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec run --deadline-ms</c> replaces the request file's deadline: a one-shot process spends its first request
/// compiling the code it runs, so a deadline that fits a warm service may not fit there.
/// </summary>
public sealed class RunCommandDeadlineTests
{
    [Fact]
    public async Task TheDeadlineOption_ReplacesTheRequestFilesDeadline()
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-run-deadline-").FullName;
        try
        {
            // A deadline no request can meet: it is spent before execution starts.
            var request = Path.Combine(directory, "request.json");
            await File.WriteAllTextAsync(request,
                "{ \"pipeline_id\": \"home_feed\", \"request_id\": \"r\", \"inputs\": { \"user_id\": 1001, \"session_id\": \"s-1\", \"surface\": \"home\" }, \"deadline_ms\": 0.001 }");

            Assert.False(await Succeeded(request, deadlineMs: null));
            Assert.True(await Succeeded(request, deadlineMs: HomeFeedTestHost.UntimedMs));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static async Task<bool> Succeeded(string request, int? deadlineMs)
        {
            var output = new StringWriter();
            var code = await RunCommand.RunAsync(HomeFeedTestHost.SampleRoot(), "local", request, Array.Empty<string>(), false, output,
                CancellationToken.None, deadlineMs: deadlineMs);
            using var result = JsonDocument.Parse(output.ToString());
            var succeeded = result.RootElement.GetProperty("succeeded").GetBoolean();
            Assert.Equal(succeeded ? 0 : 1, code); // the exit code is the request's outcome
            return succeeded;
        }
    }
}
