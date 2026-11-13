using System.Text.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Models.Onnx;

/// <summary>A model file of a <c>local_onnx@1</c> binding: its absolute path, and the setting that names it, for errors.</summary>
internal readonly record struct OnnxModelFile(string Path, string Pointer);

/// <summary>
/// The settings of a <c>local_onnx@1</c> binding, checked when the client is created, so <c>validate</c> reports a wrong
/// setting at its pointer without reading any file:
/// <code>
/// { "client": "local_onnx@1",
///   "uri": "models/ctr.onnx",                  // the default version: relative to the config root, absolute, or file://
///   "versions": { "v13": "models/ctr_v13.onnx" }, // optional: version id → file, for nodes that name a version
///   "intra_op_threads": 1 }                    // optional: threads one call may use (default 1)
/// </code>
/// </summary>
internal sealed class OnnxSettings
{
    private const string Settings = "uri, versions, intra_op_threads";

    private OnnxSettings(OnnxModelFile model, Dictionary<string, OnnxModelFile> versions, int intraOpThreads)
    {
        Model = model;
        Versions = versions;
        IntraOpThreads = intraOpThreads;
    }

    /// <summary>The file a call without a version runs.</summary>
    public OnnxModelFile Model { get; }

    /// <summary>Version id → file, ordinal.</summary>
    public Dictionary<string, OnnxModelFile> Versions { get; }

    public int IntraOpThreads { get; }

    /// <exception cref="BindingException">A setting is unknown, missing or malformed; at its pointer, with a hint.</exception>
    public static OnnxSettings Parse(BindingContext context)
    {
        foreach (var (name, _) in context.Settings)
        {
            if (name is not ("uri" or "versions" or "intra_op_threads"))
            {
                throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"local_onnx@1 has no setting '{name}'.",
                    $"its settings are {Settings}; 'client' and 'timeout_ms' are the binding's own");
            }
        }

        var model = new OnnxModelFile(context.LocalFile("uri", "ONNX model file"), "/uri");
        var versions = new Dictionary<string, OnnxModelFile>(StringComparer.Ordinal);
        if (context.Settings.TryGetValue("versions", out var map))
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                throw new BindingException("/versions", "'versions' must be an object mapping each version id to its model file.",
                    "write \"versions\": { \"v13\": \"models/ctr_v13.onnx\" }");
            }

            foreach (var version in map.EnumerateObject())
            {
                var pointer = "/versions/" + BindingContext.EscapePointerToken(version.Name);
                if (version.Name.Length == 0 || version.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(version.Value.GetString()))
                {
                    throw new BindingException(pointer, "Each version needs a non-empty id and the path of its model file.",
                        "write \"v13\": \"models/ctr_v13.onnx\"");
                }

                versions[version.Name] = new OnnxModelFile(context.LocalFile(version.Value.GetString()!, pointer, $"ONNX model file of version '{version.Name}'"), pointer);
            }
        }

        var threads = 1;
        if (context.Settings.TryGetValue("intra_op_threads", out var value)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out threads) || threads < 1 || threads > 256))
        {
            throw new BindingException("/intra_op_threads", "'intra_op_threads' must be an integer from 1 to 256.",
                "leave it at 1 (the default) when many requests score at once; raise it only for few, large calls");
        }

        return new OnnxSettings(model, versions, threads);
    }
}
