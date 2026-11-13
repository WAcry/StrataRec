using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Hosting.Replay;

/// <summary>Reads and writes replay bundles (design 13.4) as JSON files.</summary>
public static class ReplayBundleFile
{
    /// <summary>Serializes a bundle with the artifact serializer (snake_case, nulls omitted).</summary>
    public static string Serialize(ReplayBundle bundle) => JsonSerializer.Serialize(bundle, ArtifactSerializer.Options);

    public static void Save(ReplayBundle bundle, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(bundle), new UTF8Encoding(false));
    }

    /// <summary>Parses a bundle; a document that does not match the schema or format is reported with its JSON pointer.</summary>
    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format.</exception>
    public static ReplayBundle Parse(string json, string? sourceName = null)
    {
        ReplayBundle bundle;
        try
        {
            bundle = ArtifactSerializer.Deserialize<ReplayBundle>(json, sourceName);
        }
        catch (ArtifactFormatException ex)
        {
            throw new ReplayBundleException(ArtifactSerializer.JsonPathToPointer(ex.JsonPath), ex.InnerException?.Message ?? ex.Message,
                "run 'stratarec schema replay_bundle' for the JSON Schema of a bundle");
        }

        if (bundle.Format != ReplayBundle.CurrentFormat)
        {
            throw new ReplayBundleException("/format", $"unsupported format '{bundle.Format}'",
                $"this build reads {ReplayBundle.CurrentFormat}; record the request again with this build");
        }

        RequiredValues.Check(bundle);
        return bundle;
    }

    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ReplayBundle Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>
    /// Rejects an explicit null wherever the bundle model declares a non-nullable value: an object, a string, an array or
    /// map, or an entry of one. The deserializer checks that required properties are present, not that they are non-null
    /// (and leaves a null where a property has a default), so without this a null surfaces as a crash deep in a replay.
    /// </summary>
    /// <remarks>
    /// Walks the model by reflection with its nullable annotations, so a property added to the model is checked without
    /// being listed here. Runs once per parsed bundle, never on a request path.
    /// </remarks>
    private static class RequiredValues
    {
        private const string Hint = "the value is required and cannot be null; run 'stratarec schema replay_bundle' for the JSON Schema of a bundle";

        public static void Check(ReplayBundle bundle) => CheckObject(bundle, string.Empty, new NullabilityInfoContext());

        private static void CheckObject(object value, string pointer, NullabilityInfoContext context)
        {
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
                Check(property.GetValue(value), context.Create(property), pointer + "/" + JsonPointer.Escape(name), context);
            }
        }

        private static void Check(object? value, NullabilityInfo declared, string pointer, NullabilityInfoContext context)
        {
            if (value is null)
            {
                // Nullable<T> and annotated references may be null; everything else is required.
                if (!declared.Type.IsValueType && declared.ReadState == NullabilityState.NotNull)
                {
                    throw new ReplayBundleException(pointer, "must not be null", Hint);
                }

                return;
            }

            switch (value)
            {
                case string or ValueType:
                    return;
                case IDictionary map:
                    var valueType = declared.GenericTypeArguments[1];
                    foreach (DictionaryEntry entry in map)
                    {
                        Check(entry.Value, valueType, pointer + "/" + JsonPointer.Escape((string)entry.Key), context);
                    }

                    return;
                case IList list:
                    var elementType = declared.GenericTypeArguments[0];
                    for (var i = 0; i < list.Count; i++)
                    {
                        Check(list[i], elementType, pointer + "/" + i.ToString(CultureInfo.InvariantCulture), context);
                    }

                    return;
                default:
                    CheckObject(value, pointer, context);
                    return;
            }
        }
    }

    /// <summary>Bundle files of a path: the file itself, or every <c>*.json</c> file of a directory in ordinal order.</summary>
    public static List<string> Find(string path)
    {
        var files = new List<string>();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json"))
            {
                files.Add(file);
            }

            files.Sort(StringComparer.Ordinal);
        }
        else
        {
            files.Add(path);
        }

        return files;
    }
}
