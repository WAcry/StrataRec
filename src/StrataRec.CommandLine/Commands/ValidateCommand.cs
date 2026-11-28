using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

internal static class ValidateCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var format = CommonOptions.Format();
        var strict = new Option<bool>("--warnings-as-errors") { Description = "Fail on warnings (e.g. experiment interactions) too." };
        var command = new Command("validate",
            "Run all static checks on a configuration workspace. Exits with 0 when there are no errors, 1 when there are (or warnings, with --warnings-as-errors).") { config, environment, pipeline, format, strict };
        command.SetAction(parse => Run(provided, parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline), parse.GetValue(format)!,
            parse.GetValue(strict), parse.InvocationConfiguration.Output));
        return command;
    }

    internal static int Run(PlanEnvironment provided, string config, string environment, string? pipeline, string format, bool warningsAsErrors, TextWriter output)
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

            // What a service loading these pipelines serves: their fallback pipelines too, checked against them, and the
            // pipelines their nodes call with theirs (as a workspace load brings them).
            ids = workspace.WithFallbacks(ids, diagnostics, out _);
            var listed = new HashSet<string>(ids, StringComparer.Ordinal);
            var schemas = new Dictionary<string, RequestSchema>(StringComparer.Ordinal);
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var request = workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
                if (request is null)
                {
                    continue;
                }

                var compilation = PlanCompiler.Compile(request, CommonOptions.ForRoot(provided, workspace.Root));
                diagnostics.AddRange(compilation.Diagnostics);
                summaries.Add(Summary(id, request, compilation));
                if (compilation.Plan is { } plan)
                {
                    schemas[id] = plan.RequestSchema;
                    var called = new List<string>();
                    foreach (var other in plan.Calls)
                    {
                        if (!listed.Contains(other) && workspace.Pipelines.ContainsKey(other))
                        {
                            called.Add(other);
                        }
                    }

                    foreach (var other in workspace.WithFallbacks(called, diagnostics, out _))
                    {
                        if (listed.Add(other))
                        {
                            ids.Add(other);
                        }
                    }
                }
            }

            workspace.CheckFallbackInputs(ids, id => schemas.GetValueOrDefault(id), diagnostics);
        }

        SchemaFiles.Check(config, provided, diagnostics);

        var code = Output.Diagnostics(diagnostics.Items, format, output, warningsAsErrors, DiagnosticLocator.ForWorkspace(config));
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
        return $"{id}: {status} — {Output.Count(report.Nodes.Count, "node")}, {Output.Count(variants, "compiled variant")}, " +
               $"{Output.Count(request.Experiments.Count, "experiment")}, {Output.Count(report.Arms.Count, "arm")}, " +
               $"{Output.Count(report.CombinationsChecked, "arm combination")} checked{exhaustive}";
    }
}
