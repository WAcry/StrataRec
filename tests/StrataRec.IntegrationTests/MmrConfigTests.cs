using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class MmrConfigTests
{
    [Theory]
    [InlineData("2.0")]
    [InlineData("-0.1")]
    public void AFixedLambdaOutsideZeroToOne_IsAnError(string lambda)
    {
        var error = SingleError(Mmr($$"""{ "lambda": {{lambda}} }"""));

        Assert.Equal(("MMR_LAMBDA", "/nodes/mmr/config/lambda"), (error.Code, error.Pointer));
        Assert.NotNull(error.Hint);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 2.0)]
    public void ALambdaParameterNotDeclaredWithinZeroToOne_IsAnError(double? min, double? max)
    {
        var error = SingleError(Mmr("""{ "lambda_param": "l" }""", new ParamSpec { Type = "float64", Min = min, Max = max }));

        Assert.Equal(("PARAM_RANGE", "/nodes/mmr/config/lambda_param"), (error.Code, error.Pointer));
        Assert.Contains("\"min\": 0, \"max\": 1", error.Hint);
    }

    [Fact]
    public void ALambdaParameterDeclaredWithinZeroToOne_Compiles()
    {
        var compilation = PlanCompiler.Compile(Mmr("""{ "lambda_param": "l" }""", new ParamSpec { Type = "float64", Min = 0.2, Max = 1 }).Request,
            HomeFeedTestHost.Load().Environment);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }

    private static ConfigDiagnostic SingleError((PlanCompileRequest Request, PlanEnvironment Environment) setup) =>
        Assert.Single(PlanCompiler.Compile(setup.Request, setup.Environment).Diagnostics, d => d.IsError);

    private static (PlanCompileRequest Request, PlanEnvironment Environment) Mmr(string lambda, ParamSpec? parameter = null)
    {
        var host = HomeFeedTestHost.Load();
        var config = JsonNode.Parse(lambda)!.AsObject();
        config["score_col"] = "score.final";
        config["embedding_col"] = "item.embedding";
        host.Request.Pipeline.Nodes["mmr"] = new NodeSpec { Order = 560, Op = "MmrDiversifier@1", Config = config };
        if (parameter is not null)
        {
            host.Request.Pipeline.ParamSchema["l"] = parameter;
            host.Request.Pipeline.Params["l"] = 0.5;
        }

        return (host.Request, host.Environment);
    }
}
