using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Runtime;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Testing;

/// <summary>
/// What "untimed" means for a copy of the home_feed sample, shared by the integration tests (<c>HomeFeedTestHost</c>,
/// which decides which tests run untimed) and the benchmarks (which measure the pipeline's work, not its fallbacks).
/// </summary>
/// <remarks>
/// The sample keeps realistic budgets (ctr_ranker gives its model 50 ms), and an answer after a budget is a timeout
/// however it arrived (design 4.8): on a slow machine, a run of the sample as written sometimes falls back where another
/// does not. A relaxed copy has no budget a slow machine can hit, and its fallback wiring is as written.
/// </remarks>
internal static class SampleBudgets
{
    /// <summary>
    /// Every budget of a relaxed copy, and the deadline of an untimed request: a minute, longer than any run on any
    /// machine, and short enough that a hung call still fails its test.
    /// </summary>
    public const int UntimedMs = 60_000;

    /// <summary>
    /// Raises every budget in the configuration of the sample copy at <paramref name="root"/> to <see cref="UntimedMs"/>:
    /// each <c>timeout_ms</c> (nodes and experiment patches of them, feature sources, models, candidate sources) and each
    /// <c>deadline_ms</c> (the request files). <c>on_failure</c> and everything else stay as written. The numbers are
    /// replaced in the text, so comments and layout stay and tests can go on editing the copy as text.
    /// </summary>
    /// <remarks>
    /// It never leaves a budget behind silently. It throws on any other <c>*_ms</c> setting but a request's
    /// <c>time_ms</c> (a budget whose untimed value has to be decided here first, such as <c>min_remaining_ms</c>), when it
    /// finds nothing to relax (the sample moved or changed shape), and when the compiled plan still has a node or
    /// fetched-source budget below <see cref="UntimedMs"/> (a fetched source without <c>timeout_ms</c> gets the
    /// framework's default of 50 ms, which must then be written into the sample).
    /// </remarks>
    public static void Relax(string root)
    {
        var untimed = Encoding.UTF8.GetBytes(UntimedMs.ToString(CultureInfo.InvariantCulture));
        var relaxed = 0;
        foreach (var path in Directory.GetFiles(root, "*.json*", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (file.StartsWith("fixtures/", StringComparison.Ordinal) || !(file.EndsWith(".json", StringComparison.Ordinal) || file.EndsWith(".jsonc", StringComparison.Ordinal)))
            {
                continue; // fixtures are data, not configuration
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

    /// <summary>The byte ranges of the values of the budgets in one JSONC file, in order.</summary>
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
            if (!name.EndsWith("_ms", StringComparison.Ordinal) || name == "time_ms") // time_ms: a request's clock, not a budget
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

    /// <summary>What the framework compiled from the relaxed copy: no node or fetched-source budget a slow machine can hit.</summary>
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
            if (group.Supply.Mode == SupplyMode.Fetch && group.Supply.TimeoutMs < UntimedMs)
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
