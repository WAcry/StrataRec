using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.Hosting.Plans;

/// <summary>
/// The objects the host loaded are still its own (it may edit them to prepare the next load), and an override must
/// build on what is served.
/// </summary>
internal sealed class PlanSource
{
    /// <summary>
    /// The artifacts' options with room for the request around them: an artifact as deep as a file may be
    /// (<see cref="Artifacts.Json.Jsonc.MaxDepth"/>) is a few levels deeper inside the request.
    /// </summary>
    private static readonly JsonSerializerOptions Compact = new(ArtifactSerializer.Options) { WriteIndented = false, MaxDepth = Artifacts.Json.Jsonc.MaxDepth + 8 };

    private readonly byte[] _json;

    private PlanSource(byte[] json) => _json = json;

    public static PlanSource Capture(PlanCompileRequest request) => new(JsonSerializer.SerializeToUtf8Bytes(request, Compact));

    public PlanCompileRequest Read() => JsonSerializer.Deserialize<PlanCompileRequest>(_json, Compact)!;
}
