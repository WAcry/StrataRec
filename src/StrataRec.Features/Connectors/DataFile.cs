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
    /// <summary>The file's root value.</summary>
    /// <exception cref="BindingException">The file cannot be read or is not JSONC; at the setting's pointer.</exception>
    public static JsonElement Read(LocalFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file.Path); // not ReadAllText: it replaces invalid bytes, and a damaged id reads as another
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BindingException(file.Pointer, $"'{file.Path}' cannot be read: {ex.Message}", "a relative path resolves against the configuration directory");
        }

        try
        {
            return Jsonc.ParseElement(bytes, file.Path);
        }
        catch (JsoncException ex)
        {
            throw new BindingException(file.Pointer, $"'{file.Path}' is not JSON: {ex.Message}", ex.Hint);
        }
    }
}
