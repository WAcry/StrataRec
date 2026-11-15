using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.Hosting.Plans;

/// <summary>
/// The configuration a loaded plan was compiled from, as the engine's own copy (design 13.3): serialized when the plan is
/// loaded, and read back into new objects for each request that compiles a configuration override on it. The objects the
/// host loaded are still its own (it may edit them to prepare the next load), and an override must build on what is
/// served: compiled from them, it could run a configuration nobody published, which a replay on the published one does
/// not reproduce.
/// </summary>
internal sealed class PlanSource
{
    private static readonly JsonSerializerOptions Compact = new(ArtifactSerializer.Options) { WriteIndented = false };

    private readonly byte[] _json;

    private PlanSource(byte[] json) => _json = json;

    public static PlanSource Capture(PlanCompileRequest request) => new(JsonSerializer.SerializeToUtf8Bytes(request, Compact));

    /// <summary>A new copy of the configuration, which nothing else holds.</summary>
    public PlanCompileRequest Read() => JsonSerializer.Deserialize<PlanCompileRequest>(_json, Compact)!;
}
