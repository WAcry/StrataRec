using StrataRec.Core.Diagnostics;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Execution;

/// <summary>
/// Metric tags of one plan, built once per compiled plan (design 13.1 rule 5) so recording a node outcome passes cached
/// arrays and allocates nothing.
/// </summary>
internal sealed class PlanTelemetry
{
    private PlanTelemetry(KeyValuePair<string, object?>[] pipeline, NodeTelemetry[] nodes)
    {
        Pipeline = pipeline;
        Nodes = nodes;
    }

    /// <summary><c>stratarec.pipeline</c>.</summary>
    public KeyValuePair<string, object?>[] Pipeline { get; }

    /// <summary>Per node variant, indexed by <see cref="CompiledNode.Index"/>.</summary>
    public NodeTelemetry[] Nodes { get; }

    public static PlanTelemetry Create(ExecutionPlan plan)
    {
        var pipeline = plan.PipelineId;
        var nodes = new NodeTelemetry[plan.Nodes.Length];
        var byId = new Dictionary<string, NodeTelemetry>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
        {
            // Variants of one node share its tags; only the output columns may differ.
            nodes[node.Index] = byId.TryGetValue(node.NodeId, out var shared)
                ? shared.WithOutputs(node)
                : byId[node.NodeId] = NodeTelemetry.Create(pipeline, node);
        }

        return new PlanTelemetry([new(StrataRecTags.Pipeline, pipeline)], nodes);
    }

    internal sealed class NodeTelemetry
    {
        private NodeTelemetry(
            string subject,
            KeyValuePair<string, object?>[] tags,
            KeyValuePair<string, object?>[][] outcomes,
            KeyValuePair<string, object?>[][] outputs,
            KeyValuePair<string, object?>[]? model)
        {
            Subject = subject;
            Tags = tags;
            Outcomes = outcomes;
            Outputs = outputs;
            Model = model;
        }

        /// <summary><c>pipeline/node</c>, the subject of windowed failure summaries.</summary>
        public string Subject { get; }

        /// <summary><c>stratarec.pipeline</c>, <c>stratarec.node</c>.</summary>
        public KeyValuePair<string, object?>[] Tags { get; }

        /// <summary>Indexed by <see cref="NodeOutcomeKind"/> (slot 0 unused): the node tags plus <c>stratarec.outcome</c>.</summary>
        public KeyValuePair<string, object?>[][] Outcomes { get; }

        /// <summary>Per entry of <see cref="CompiledNode.Outputs"/>: the node tags plus <c>stratarec.column</c>.</summary>
        public KeyValuePair<string, object?>[][] Outputs { get; }

        /// <summary>The model call tags (<c>stratarec.io.role</c> = <c>model</c>, <c>stratarec.io.target</c>) of model-calling nodes, whose fallbacks are also counted per model.</summary>
        public KeyValuePair<string, object?>[]? Model { get; }

        public static NodeTelemetry Create(string pipeline, CompiledNode node)
        {
            KeyValuePair<string, object?>[] tags = [new(StrataRecTags.Pipeline, pipeline), new(StrataRecTags.Node, node.NodeId)];
            var outcomes = new KeyValuePair<string, object?>[NodeOutcomeNames.MaxKind + 1][];
            for (var i = 1; i < outcomes.Length; i++)
            {
                outcomes[i] = [tags[0], tags[1], new(StrataRecTags.Outcome, NodeOutcomeNames.Name((NodeOutcomeKind)i))];
            }

            return new NodeTelemetry(pipeline + "/" + node.NodeId, tags, outcomes, OutputTags(tags, node), node.Operator.ModelTarget?.Tags);
        }

        public NodeTelemetry WithOutputs(CompiledNode node) =>
            new(Subject, Tags, Outcomes, OutputTags(Tags, node), node.Operator.ModelTarget?.Tags);

        private static KeyValuePair<string, object?>[][] OutputTags(KeyValuePair<string, object?>[] tags, CompiledNode node)
        {
            var outputs = new KeyValuePair<string, object?>[node.Outputs.Length][];
            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = [tags[0], tags[1], new(StrataRecTags.Column, node.Outputs[i].Name)];
            }

            return outputs;
        }
    }
}
