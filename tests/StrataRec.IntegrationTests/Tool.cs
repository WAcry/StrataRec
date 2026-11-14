using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>The stratarec tool's environment (the built-ins, StrataRec.Llm and redis@1), which the tests run the commands with.</summary>
internal static class Tool
{
    public static PlanEnvironment Environment => StrataRec.Cli.Program.CreateEnvironment();
}
