using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Features.Connectors;

/// <summary>
/// The JSON file a built-in client's <c>data_file</c> setting names (<c>memory@1</c>, <c>memory_list@1</c>), read as
/// strictly as the workspace's own files (<see cref="Jsonc"/>): a fixture with a key written twice is an error, not its
/// last value.
/// </summary>
public static class DataFile
{
    /// <summary>The file's root value and its path.</summary>
    /// <param name="setting">The setting's value: a path, relative to <paramref name="baseDirectory"/> unless rooted.</param>
    /// <exception cref="BindingException">The setting is not a path, or the file cannot be read or is not JSONC; at <c>/data_file</c>.</exception>
    public static (JsonElement Root, string Path) Read(JsonElement setting, string baseDirectory)
    {
        if (setting.ValueKind != JsonValueKind.String || setting.GetString() is not { Length: > 0 } relative)
        {
            throw new BindingException("/data_file", $"'data_file' must be the path of a JSON file; got {setting.GetRawText()}.");
        }

        var path = Path.Combine(baseDirectory, relative); // a rooted path stays as it is
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path); // not ReadAllText: it replaces invalid bytes, and a damaged id reads as another
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BindingException("/data_file", $"'{path}' cannot be read: {ex.Message}", $"a relative path resolves against {baseDirectory}");
        }

        try
        {
            return (Jsonc.ParseElement(bytes, path), path);
        }
        catch (JsoncException ex)
        {
            throw new BindingException("/data_file", $"'{path}' is not JSON: {ex.Message}", ex.Hint);
        }
    }
}
