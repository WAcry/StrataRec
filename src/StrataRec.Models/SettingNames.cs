using System.Text.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Models;

/// <summary>The settings rules the built-in clients share: a list of names, an unknown setting.</summary>
internal static class SettingNames
{
    /// <summary>
    /// The names of an array setting (<c>"inputs": ["a", "b"]</c>), in order: each a non-empty string, none twice, each
    /// problem at its item's pointer. Whether the list may be empty is the client's to say.
    /// </summary>
    public static List<string> Read(JsonElement array, string setting, string hint)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var pointer = "/" + setting + "/" + names.Count;
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw new BindingException(pointer, $"'{setting}' item {names.Count} must be a non-empty name, got {item.GetRawText()}.", hint);
            }

            if (!seen.Add(name))
            {
                throw new BindingException(pointer, $"'{name}' is listed twice in '{setting}'.", "list each name once");
            }

            names.Add(name);
        }

        return names;
    }
}
