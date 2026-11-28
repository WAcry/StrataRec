using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

internal static class Tool
{
    public static PlanEnvironment Environment => StrataRec.Cli.Program.CreateEnvironment();
}
