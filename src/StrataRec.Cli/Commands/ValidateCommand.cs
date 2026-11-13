using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec validate</c>: every static check CI runs (design 10.1 item 1) — schemas, types, data flow, patch
/// footprints and conflicts, per-arm and combination checks — for every pipeline in an environment.
/// </summary>
internal static class ValidateCommand
{
    public static Command Create()
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var format = CommonOptions.Format();
        var strict = new Option<bool>("--warnings-as-errors") { Description = "Fail on warnings (e.g. experiment interactions) too." };
        var command = new Command("validate", "Run all static checks on a configuration workspace.") { config, environment, pipeline, format, strict };
        command.SetAction(parse => Run(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline), parse.GetValue(format)!,
            parse.GetValue(strict), Console.Out));
        return command;
    }

    internal static int Run(string config, string environment, string? pipeline, string format, bool warningsAsErrors, TextWriter output)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(config, diagnostics);
        var summaries = new List<string>();
        if (!diagnostics.HasErrors)
        {
            var ids = new List<string>();
            if (pipeline is not null)
            {
                ids.Add(pipeline);
            }
            else
            {
                ids.AddRange(workspace.Pipelines.Keys);
            }

            foreach (var id in ids)
            {
                var request = workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
                if (request is null)
                {
                    continue;
                }

                var compilation = PlanCompiler.Compile(request, CommonOptions.PlanEnvironment(workspace));
                diagnostics.AddRange(compilation.Diagnostics);
                CommonOptions.ValidateAssignment(workspace, request, compilation, environment, diagnostics);
                summaries.Add(Summary(id, request, compilation));
            }
        }

        var code = Output.Diagnostics(diagnostics.Items, format, output, warningsAsErrors);
        if (format == "text")
        {
            foreach (var summary in summaries)
            {
                output.WriteLine(summary);
            }
        }

        return code;
    }

    private static string Summary(string id, PlanCompileRequest request, PlanCompilation compilation)
    {
        var report = compilation.Report;
        var variants = 0;
        foreach (var node in report.Nodes)
        {
            variants += node.Variants;
        }

        var status = compilation.Succeeded ? "ok" : "FAILED";
        var exhaustive = report.CombinationCheckExhaustive ? string.Empty : " (targeted)";
        return $"{id}: {status} — {report.Nodes.Count} nodes, {variants} compiled variants, {request.Experiments.Count} experiments, " +
               $"{report.Arms.Count} arms, {report.CombinationsChecked} arm combinations checked{exhaustive}";
    }
}
