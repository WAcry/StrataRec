using StrataRec.Core.Diagnostics;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Execution;

/// <summary>
/// Metric tags of one plan, built once per compiled plan so recording a node outcome passes cached
/// arrays and allocates nothing.
/// </summary>
internal sealed class PlanTelemetry
{
    private PlanTelemetry(KeyValuePair<string, object?>[] pipeline, NodeTelemetry[] nodes)
    {
        Pipeline = pipeline;
        Nodes = nodes;
    }

    public KeyValuePair<string, object?>[] Pipeline { get; }

    public NodeTelemetry[] Nodes { get; }

    public static PlanTelemetry Create(ExecutionPlan plan)
    {
        var pipeline = plan.PipelineId;
        var nodes = new NodeTelemetry[plan.Nodes.Length];
        var byId = new Dictionary<string, NodeTelemetry>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
        {
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
            KeyValuePair<string, object?>[][] requestOutputs)
        {
            Subject = subject;
            Tags = tags;
            Outcomes = outcomes;
            Outputs = outputs;
            RequestOutputs = requestOutputs;
        }

        /// <summary><c>pipeline/node</c>, the subject of windowed failure summaries.</summary>
        public string Subject { get; }

        public KeyValuePair<string, object?>[] Tags { get; }

        public KeyValuePair<string, object?>[][] Outcomes { get; }

        public KeyValuePair<string, object?>[][] Outputs { get; }

        public KeyValuePair<string, object?>[][] RequestOutputs { get; }

        public static NodeTelemetry Create(string pipeline, CompiledNode node)
        {
            KeyValuePair<string, object?>[] tags = [new(StrataRecTags.Pipeline, pipeline), new(StrataRecTags.Node, node.NodeId)];
            var outcomes = new KeyValuePair<string, object?>[NodeOutcomeNames.MaxKind + 1][];
            for (var i = 1; i < outcomes.Length; i++)
            {
                outcomes[i] = [tags[0], tags[1], new(StrataRecTags.Outcome, NodeOutcomeNames.Name((NodeOutcomeKind)i))];
            }

            return new NodeTelemetry(pipeline + "/" + node.NodeId, tags, outcomes, OutputTags(tags, node), RequestOutputTags(tags, node));
        }

        public NodeTelemetry WithOutputs(CompiledNode node) =>
            new(Subject, Tags, Outcomes, OutputTags(Tags, node), RequestOutputTags(Tags, node));

        private static KeyValuePair<string, object?>[][] RequestOutputTags(KeyValuePair<string, object?>[] tags, CompiledNode node)
        {
            var outputs = new KeyValuePair<string, object?>[node.RequestOutputs.Length][];
            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = [tags[0], tags[1], new(StrataRecTags.Column, "req." + node.RequestOutputs[i].Name)];
            }

            return outputs;
        }

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
