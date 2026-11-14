using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Samples.Host.Components;

namespace StrataRec.Samples.Host;

/// <summary>
/// What this binary provides to plans: the built-in operators, connectors and clients, plus the sample's own. A workspace
/// names each by its type (<c>"op": "CategoryBoost@1"</c>, <c>"connector": "kv_store@1"</c>, <c>"client": "logistic@1"</c>,
/// <c>"client": "trending@1"</c>), so a binary without them rejects the workspace at load.
/// </summary>
/// <remarks>
/// The service (<c>AddStrataRec</c>) and the configuration commands (<c>dotnet run -- stratarec …</c>) both start from
/// here, so what <c>validate</c> accepts is exactly what the service runs. Neither needs a directory from it: each places
/// the environment in the workspace it reads, where relative file settings of bindings (<c>data_file</c>) resolve.
/// </remarks>
public static class SampleEnvironment
{
    public static PlanEnvironment Create() => new()
    {
        Operators = OperatorRegistry.CreateDefault().Add(new CategoryBoostFactory()),
        Connectors = FeatureConnectorRegistry.CreateDefault().Add(new KeyValueFeatureConnectorFactory()),
        Models = ModelClientRegistry.CreateDefault().Add(new LogisticModelClientFactory()),
        CandidateSources = CandidateSourceRegistry.CreateDefault().Add(new TrendingCandidateSourceFactory()),
    };
}
