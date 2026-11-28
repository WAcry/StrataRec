using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Runtime;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Testing;

internal static class SampleBudgets
{
    /// <summary>
    /// Every budget of a relaxed copy, and the deadline of an untimed request: a minute, longer than any run on any
    /// machine, and short enough that a hung call still fails its test.
    /// </summary>
    public const int UntimedMs = 60_000;

    /// <summary>
    /// Raises every budget in the configuration of the sample copy at <paramref name="root"/> to <see cref="UntimedMs"/>:
    /// each <c>timeout_ms</c> (nodes and experiment patches of them, feature sources, services) and each
    /// <c>deadline_ms</c> (the request files). <c>on_failure</c> and everything else stay as written. The numbers are
    /// replaced in the text, so comments and layout stay and tests can go on editing the copy as text.
    /// </summary>
    public static void Relax(string root)
    {
        var untimed = Encoding.UTF8.GetBytes(UntimedMs.ToString(CultureInfo.InvariantCulture));
        var relaxed = 0;
        foreach (var path in Directory.GetFiles(root, "*.json*", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (file.StartsWith("fixtures/", StringComparison.Ordinal) || file.StartsWith("schemas/", StringComparison.Ordinal)
                || !(file.EndsWith(".json", StringComparison.Ordinal) || file.EndsWith(".jsonc", StringComparison.Ordinal)))
            {
                continue; // fixtures are data and schemas describe the files, neither is configuration
            }

            var text = File.ReadAllBytes(path);
            var budgets = FindBudgets(text, file);
            if (budgets.Count == 0)
            {
                continue;
            }

            using var edited = new MemoryStream(text.Length + (budgets.Count * untimed.Length));
            var at = 0;
            foreach (var (start, length) in budgets)
            {
                edited.Write(text, at, start - at);
                edited.Write(untimed);
                at = start + length;
            }

            edited.Write(text, at, text.Length - at);
            File.WriteAllBytes(path, edited.ToArray());
            relaxed += budgets.Count;
        }

        if (relaxed == 0)
        {
            throw new InvalidOperationException($"No timeout_ms or deadline_ms in the configuration under {root}: has the home_feed sample moved or changed shape?");
        }

        CheckCompiledBudgets(root);
    }

    private static List<(int Start, int Length)> FindBudgets(byte[] text, string file)
    {
        var offset = text.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var reader = new Utf8JsonReader(text.AsSpan(offset), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var budgets = new List<(int, int)>();
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString()!;
            // time_ms: a request's clock; ttl_ms and stale_if_error_ms: how long a cache serves a value. None is a budget:
            // a slow machine cannot exceed them into a fallback, at most into a fetch the cache would have saved.
            if (!name.EndsWith("_ms", StringComparison.Ordinal) || name is "time_ms" or "ttl_ms" or "stale_if_error_ms")
            {
                continue;
            }

            var where = $"{file}:{LineOf(text, offset + (int)reader.TokenStartIndex)} \"{name}\"";
            if (name is not ("timeout_ms" or "deadline_ms"))
            {
                throw new InvalidOperationException(
                    $"{where} is a timing setting that SampleBudgets.Relax does not relax yet: decide its untimed value there.");
            }

            reader.Read();
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new InvalidOperationException($"{where} is not a number; SampleBudgets.Relax cannot relax it.");
            }

            budgets.Add((offset + (int)reader.TokenStartIndex, reader.ValueSpan.Length));
        }

        return budgets;
    }

    private static int LineOf(byte[] text, int position)
    {
        var line = 1;
        for (var i = 0; i < position; i++)
        {
            line += text[i] == (byte)'\n' ? 1 : 0;
        }

        return line;
    }

    private static void CheckCompiledBudgets(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);
        if (request is null || diagnostics.HasErrors)
        {
            throw new InvalidOperationException("The relaxed home_feed sample does not load:\n" + string.Join("\n", diagnostics.Items));
        }

        var compilation = PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = root });
        if (compilation.Plan is not { } plan)
        {
            throw new InvalidOperationException("The relaxed home_feed sample does not compile:\n" + string.Join("\n", compilation.Diagnostics));
        }

        var tight = new List<string>();
        foreach (var node in plan.Nodes)
        {
            if (node.TimeoutMs < UntimedMs)
            {
                tight.Add($"node '{node.NodeId}' ({node.Pointer}): timeout_ms {node.TimeoutMs}");
            }
        }

        foreach (var group in plan.Features.Groups)
        {
            if (group.Supply.Mode is SupplyMode.Fetch or SupplyMode.Cache && group.Supply.TimeoutMs < UntimedMs)
            {
                tight.Add($"feature source '{group.SourceName}': timeout {group.Supply.TimeoutMs} ms (without timeout_ms, the framework's default)");
            }
        }

        if (tight.Count > 0)
        {
            throw new InvalidOperationException("The relaxed home_feed sample still has budgets a slow machine can hit:\n" + string.Join("\n", tight));
        }
    }
}
