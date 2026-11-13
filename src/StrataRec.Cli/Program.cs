using System.CommandLine;
using StrataRec.Cli.Commands;

namespace StrataRec.Cli;

/// <summary>
/// <c>stratarec</c>: validate, build, explain and run configurations locally (design 10.1, 13.4). Output is plain text
/// for humans or <c>--format json</c> for CI and agents; the exit code is 0 on success, 1 on configuration errors and
/// 2 on usage errors.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args) => CreateRootCommand().Parse(args).InvokeAsync();

    internal static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("StrataRec: columnar recommendation pipelines with multi-layer experiments.");
        root.Add(ValidateCommand.Create());
        root.Add(BuildCommand.Create());
        root.Add(ExplainPlanCommand.Create());
        root.Add(RunCommand.Create());
        root.Add(ReplayCommand.Create());
        root.Add(CompareCommand.Create());
        root.Add(SchemaCommand.Create());
        root.Add(OperatorsCommand.Create());
        return root;
    }
}
