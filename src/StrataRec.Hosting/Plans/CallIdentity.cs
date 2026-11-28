using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Hashing;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Plans;

internal sealed class CallIdentity
{
    private CallIdentity(string baseline, string[] experiments, PipelineInterface shape, long shapeHash)
    {
        Baseline = baseline;
        Experiments = experiments;
        Shape = shape;
        ShapeHash = shapeHash;
    }

    public string Baseline { get; }

    /// <summary>By the index of the plan's experiment (<see cref="ExecutionPlan.Experiments"/>): the digest of its artifact.</summary>
    public string[] Experiments { get; }

    /// <summary>The plan's interface: a record is decoded as a result of it.</summary>
    public PipelineInterface Shape { get; }

    /// <summary>
    /// A hash of what a result of the plan holds: part of every key, so a record is read only as a result of the output
    /// it was written for, also by a cache that names a <c>version</c> and so does not follow the configuration.
    /// </summary>
    public long ShapeHash { get; }

    public static CallIdentity Of(PlanCompileRequest source, ExecutionPlan plan)
    {
        var features = new JsonObject();
        foreach (var feature in plan.Features.Features)
        {
            var name = feature.Definition.Name;
            if (source.Catalog.Features.TryGetValue(name, out var fetched))
            {
                features[name] = ArtifactSerializer.ToNode(fetched);
            }
            else if (source.Catalog.DerivedFeatures.TryGetValue(name, out var derived))
            {
                features[name] = ArtifactSerializer.ToNode(derived);
            }
        }

        var tables = new JsonArray();
        foreach (var table in source.Tables)
        {
            tables.Add(ArtifactSerializer.ToNode(table));
        }

        var baseline = JsonDigest.Of(new JsonObject
        {
            ["pipeline"] = ArtifactSerializer.ToNode(source.Pipeline),
            ["entities"] = ArtifactSerializer.ToNode(source.Catalog.Entities),
            ["features"] = features,
            ["tables"] = tables,
            ["values"] = plan.ValuesIdentity,
        });

        var experiments = new string[plan.Experiments.Length];
        foreach (var experiment in plan.Experiments)
        {
            experiments[experiment.Index] = string.Empty;
            foreach (var placed in source.Experiments)
            {
                if (placed.Experiment.ExperimentId == experiment.Id)
                {
                    experiments[experiment.Index] = JsonDigest.Of(ArtifactSerializer.ToNode(placed.Experiment));
                    break;
                }
            }
        }

        var shape = PipelineInterface.Of(plan);
        var hash = default(StableHash.Builder);
        try
        {
            hash.Add(shape.KeyType == Core.Keys.EntityKeyType.String);
            hash.Add(shape.Columns.Length);
            foreach (var column in shape.Columns)
            {
                hash.Add(column.Name);
                hash.Add(column.Type.ToString());
            }

            hash.Add(shape.Values.Length);
            foreach (var value in shape.Values)
            {
                hash.Add(value.Name);
                hash.Add(value.Type.ToString());
            }

            return new CallIdentity(baseline, experiments, shape, unchecked((long)hash.Finish()));
        }
        finally
        {
            hash.Dispose();
        }
    }
}
