using System.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine;

/// <summary>
/// The <c>stratarec</c> commands (init, validate, build, explain-plan, run, replay, compare, diff, schema, operators;
/// design 10.1, 13.4), run with the components of the binary that calls them. The <c>stratarec</c> tool is one caller,
/// with the components it ships; a host binary is another, with its own, so a workspace that names the host's operators,
/// connectors or clients is validated, explained, run and replayed by the binary that serves it, and <c>schema</c> and
/// <c>operators</c> describe that binary's components.
/// </summary>
/// <remarks>
/// <para>Output is plain text for people or <c>--format json</c> for CI and agents; the exit code is 0 on success, 1 on
/// configuration errors and 2 on usage errors; <c>diff</c> follows diff(1): 0 when nothing differs, 1 when something
/// does, 2 on any error.</para>
/// <para>A host offers them under a word of its own, e.g. <c>dotnet run -- stratarec validate -c config</c>:</para>
/// <code>
/// if (args is ["stratarec", ..])
/// {
///     return await StrataRecCommandLine.InvokeAsync(args[1..], MyEnvironment.Create());
/// }
/// </code>
/// </remarks>
public static class StrataRecCommandLine
{
    /// <summary>Runs one command, as the <c>stratarec</c> tool would with <paramref name="args"/>; returns its exit code.</summary>
    /// <param name="args">The command line after the program's name, e.g. <c>["validate", "-c", "config"]</c>.</param>
    /// <param name="environment">
    /// What the calling binary provides to plans: its operators, connectors, clients, candidate sources, update sources and
    /// cache stores. Each command places it in the workspace it reads (<c>environment with { BaseDirectory = … }</c>: the
    /// <c>-c</c> directory, or none for a release manifest, whose relative paths resolve against the current directory),
    /// so the environment's own <see cref="PlanEnvironment.BaseDirectory"/> is not used.
    /// </param>
    /// <param name="output">Where results go; standard output by default.</param>
    /// <param name="error">Where diagnostics, usage errors and progress go; standard error by default.</param>
    /// <param name="cancellationToken">Cancels the command (as Ctrl-C does).</param>
    public static Task<int> InvokeAsync(
        IReadOnlyList<string> args, PlanEnvironment environment, TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var configuration = new InvocationConfiguration();
        if (output is not null)
        {
            configuration.Output = output;
        }

        if (error is not null)
        {
            configuration.Error = error;
        }

        return CreateRootCommand(environment).Parse(args).InvokeAsync(configuration, cancellationToken);
    }

    /// <summary>
    /// The command tree, for a binary that adds the commands to a command line of its own (as a subcommand) or invokes it
    /// with options of its own. Each command writes to the invocation's <see cref="InvocationConfiguration.Output"/> and
    /// <see cref="InvocationConfiguration.Error"/>.
    /// </summary>
    /// <param name="environment">As for <see cref="InvokeAsync"/>.</param>
    public static RootCommand CreateRootCommand(PlanEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var root = new RootCommand("StrataRec: columnar recommendation pipelines with multi-layer experiments.");
        root.Add(InitCommand.Create());
        root.Add(ValidateCommand.Create(environment));
        root.Add(BuildCommand.Create(environment));
        root.Add(ExplainPlanCommand.Create(environment));
        root.Add(RunCommand.Create(environment));
        root.Add(ReplayCommand.Create(environment));
        root.Add(CompareCommand.Create(environment));
        root.Add(DiffCommand.Create(environment));
        root.Add(SchemaCommand.Create(environment));
        root.Add(OperatorsCommand.Create(environment));
        return root;
    }
}
