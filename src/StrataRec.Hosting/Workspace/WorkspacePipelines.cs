using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Workspace;

/// <summary>
/// A workspace's pipelines as one of them may call the others (<see cref="IPipelineDirectory"/>): a called pipeline is
/// compiled when first asked for, as its release has it (its catalog, bindings and experiments), without loading
/// anything. Its experiments cannot change its interface, but an arm may add a call, and a cycle is one whichever arms
/// its calls are in; each arm is not validated on its own again, which its own compile does.
/// </summary>
/// <remarks>
/// One per pipeline being compiled, linked to the one of the pipeline that called for it: the pipelines being compiled
/// from the first to this one are how a cycle is found, and what was found of a pipeline is kept for the whole compile.
/// </remarks>
internal sealed class WorkspacePipelines : IPipelineDirectory
{
    private readonly ConfigWorkspace _workspace;
    private readonly string _environment;
    private readonly string[] _compiling;
    private readonly Dictionary<(string PipelineId, PlanEnvironment Environment), PipelineLookup> _found;

    public WorkspacePipelines(ConfigWorkspace workspace, string environment, string pipelineId)
        : this(workspace, environment, [pipelineId], new Dictionary<(string, PlanEnvironment), PipelineLookup>())
    {
    }

    private WorkspacePipelines(
        ConfigWorkspace workspace, string environment, string[] compiling, Dictionary<(string PipelineId, PlanEnvironment Environment), PipelineLookup> found)
    {
        _workspace = workspace;
        _environment = environment;
        _compiling = compiling;
        _found = found;
    }

    public PipelineLookup Find(string pipelineId, string caller, PlanEnvironment environment)
    {
        if (!_workspace.Pipelines.ContainsKey(pipelineId))
        {
            return new PipelineLookup(PipelineLookupStatus.Unknown, Detail: NameSuggestions.DidYouMean(pipelineId, _workspace.Pipelines.Keys));
        }

        var at = Array.IndexOf(_compiling, pipelineId);
        if (at >= 0)
        {
            return new PipelineLookup(PipelineLookupStatus.Cycle, Detail: string.Join(" → ", _compiling.AsSpan(at).ToArray()) + " → " + pipelineId);
        }

        lock (_found)
        {
            // Kept for the environment it was compiled with: a request compiled again by a binary with other operators
            // asks again.
            if (!_found.TryGetValue((pipelineId, environment), out var lookup))
            {
                _found[(pipelineId, environment)] = lookup = Compile(pipelineId, environment);
            }

            return lookup;
        }
    }

    private PipelineLookup Compile(string pipelineId, PlanEnvironment environment)
    {
        var diagnostics = new DiagnosticBag();
        var request = _workspace.CreateCompileRequest(pipelineId, _environment, diagnostics, today: null, extraExperiments: null,
            new WorkspacePipelines(_workspace, _environment, [.. _compiling, pipelineId], _found), called: true);
        var compilation = request is null ? null : PlanCompiler.Compile(request, environment);
        if (compilation?.Plan is { } plan)
        {
            return new PipelineLookup(PipelineLookupStatus.Known, PipelineInterface.Of(plan));
        }

        if (compilation is not null)
        {
            diagnostics.AddRange(compilation.Diagnostics);
        }

        foreach (var diagnostic in diagnostics.Items)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                return new PipelineLookup(PipelineLookupStatus.Failed, Detail: $"{diagnostic.Code} at {diagnostic.Artifact}{diagnostic.Pointer}: {diagnostic.Message}");
            }
        }

        return new PipelineLookup(PipelineLookupStatus.Failed, Detail: "it has errors.");
    }
}
