using System.Globalization;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// An experiment's <c>expires_at</c> is checked where a release is decided, <c>validate</c> and <c>build</c> (design
/// 11.7), with a warning in the last 14 days. Loading, running, explaining and replaying configurations never fail on a
/// calendar date: a service restarting after the date must start, and last week's request must still replay.
/// </summary>
public sealed class ExperimentExpiryTests
{
    [Fact]
    public async Task AnExpiredExperiment_FailsValidateAndBuild_ButNotRunOrExplain()
    {
        var root = ManifestStores.Workspace(root => Expire(root, DateTime.UtcNow.AddDays(-1)));

        var validate = new StringWriter();
        Assert.True(1 == ValidateCommand.Run(Tool.Environment, root, "local", null, "text", false, validate), validate.ToString());
        Assert.Contains("EXPERIMENT_EXPIRED", validate.ToString());

        var build = new StringWriter();
        Assert.Equal(1, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, ManifestStores.NewStore(), null, 1, TextWriter.Null, CancellationToken.None, errors: build));
        Assert.Contains("EXPERIMENT_EXPIRED", build.ToString());

        var explained = new StringWriter();
        Assert.Equal(0, await ExplainPlanCommand.RunAsync(Tool.Environment, root, "local", null, "text", new[] { "exp_quality_weight/strong" }, explained, CancellationToken.None));
        Assert.DoesNotContain("EXPIRED", explained.ToString());
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(3, "in 3 days")]
    [InlineData(14, "in 14 days")]
    public void AnExperimentAboutToExpire_IsAWarning_AtValidate(int days, string when)
    {
        var root = ManifestStores.Workspace(root => Expire(root, DateTime.UtcNow.AddDays(days)));

        var output = new StringWriter();
        Assert.Equal(0, ValidateCommand.Run(Tool.Environment, root, "local", null, "text", false, output));
        Assert.Contains("EXPERIMENT_EXPIRING", output.ToString());
        Assert.Contains(when, output.ToString());
        Assert.Equal(1, ValidateCommand.Run(Tool.Environment, root, "local", null, "text", true, TextWriter.Null)); // a strict CI can refuse it
    }

    [Fact]
    public void AnExperimentFurtherOut_IsNotWarnedAbout()
    {
        var root = ManifestStores.Workspace(root => Expire(root, DateTime.UtcNow.AddDays(15)));

        var output = new StringWriter();
        Assert.Equal(0, ValidateCommand.Run(Tool.Environment, root, "local", null, "text", false, output));
        Assert.DoesNotContain("EXPERIMENT_EXPIRING", output.ToString());
    }

    private static void Expire(string root, DateTime date)
    {
        var path = Path.Combine(root, "experiments", "exp_quality_weight.jsonc");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"2099-12-31\"",
            "\"" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal));
    }
}
