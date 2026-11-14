using System.CommandLine;
using StrataRec.Artifacts.Model;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec init</c>: writes a new workspace (<see cref="InitTemplates"/>) that validates without warnings and runs
/// offline, and prints the commands to try next, so that a new pipeline starts from a small working one rather than
/// from a pruned copy of the sample.
/// </summary>
/// <remarks>
/// It never deletes and, without <c>--force</c>, never writes into a directory that has anything in it but hidden entries:
/// a mistyped directory must not end up with a workspace scattered over its files, while a repository just made with
/// <c>git init</c> (<c>.git</c>, <c>.gitignore</c>, an editor's settings) is where a workspace is meant to start. With
/// <c>--force</c> it writes its own files over files of the same names and leaves every other file as it is.
/// </remarks>
internal static class InitCommand
{
    public static Command Create()
    {
        var directory = new Argument<string?>("directory")
        {
            Description = "Where to create the workspace (created if missing); default: the current directory. It must be empty (hidden entries such as .git aside) unless --force.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var pipeline = new Option<string>("--pipeline", "-p")
        {
            Description = "Id of the pipeline to create; it also names the catalog and the release files.",
            DefaultValueFactory = _ => InitTemplates.DefaultPipeline,
        };
        var force = new Option<bool>("--force")
        {
            Description = "Write into a directory that is not empty: files of the same names are overwritten, nothing is deleted.",
        };
        var command = new Command("init", "Create a minimal workspace that validates and runs: a pipeline (recall, model, final score, top k), " +
                                          "an experiment, local bindings to fixtures and a request. Exits with 2 on usage errors, such as a directory " +
                                          "that is not empty without --force.")
        {
            directory, pipeline, force,
        };
        command.SetAction(parse => Run(parse.GetValue(directory), parse.GetValue(pipeline)!, parse.GetValue(force), Console.Out, Console.Error));
        return command;
    }

    /// <param name="directory">The directory as the user typed it; null for the current directory.</param>
    internal static int Run(string? directory, string pipeline, bool force, TextWriter output, TextWriter errors)
    {
        var shown = Shown(directory);
        var root = Path.GetFullPath(string.IsNullOrEmpty(directory) ? "." : directory);
        var label = shown == "." ? root : shown; // what diagnostics name: "." would say little
        var diagnostics = new DiagnosticBag();
        if (!IsValidId(pipeline))
        {
            diagnostics.Error("INIT_PIPELINE_ID", "--pipeline", string.Empty, $"'{pipeline}' is not a valid pipeline id.",
                "ids start with a letter or '_' and contain letters, digits, '_', '.' or '-', e.g. --pipeline home_feed");
            return Fail(diagnostics, errors);
        }

        var files = InitTemplates.Files(pipeline);
        if (File.Exists(root))
        {
            diagnostics.Error("INIT_NOT_A_DIRECTORY", label, string.Empty, "This is a file, not a directory.", "pass the directory to create the workspace in");
            return Fail(diagnostics, errors);
        }

        var existing = new List<string>(); // the files init would overwrite
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                existing.Add(file.Path);
            }
        }

        if (!force && Directory.Exists(root) && InTheWay(root, existing) is { } blocking)
        {
            diagnostics.Error("INIT_DIRECTORY_NOT_EMPTY", label, string.Empty, $"The directory is not empty: '{blocking}' is in the way.",
                existing.Count > 0
                    ? $"init into a new or empty directory, or pass --force to overwrite the {Plural(existing.Count, "file")} of the workspace " +
                      "that exist here (other files stay as they are; nothing is deleted)"
                    : "init into a new or empty directory, or pass --force to add the workspace's files to this one (no file here is overwritten or deleted)");
            return Fail(diagnostics, errors);
        }

        // A path of the workspace that a directory takes, or a directory a file takes: reported before anything is written.
        foreach (var file in files)
        {
            if (Blocked(root, file.Path) is { } blocked)
            {
                diagnostics.Error("INIT_PATH_TAKEN", label, string.Empty, $"'{blocked}' is in the way of {file.Path}.", $"move or rename '{blocked}' and run init again");
                return Fail(diagnostics, errors);
            }
        }

        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Error("INIT_WRITE_FAILED", Join(shown, file.Path), string.Empty, ex.Message, "check that the directory is writable");
                return Fail(diagnostics, errors);
            }
        }

        // Validate and explain-plan take every pipeline (or the only one): with another pipeline in the directory, name ours.
        var select = OtherPipelines(root, InitTemplates.PipelinePath(pipeline)) ? $" -p {pipeline}" : string.Empty;
        var where = shown == "." ? "the current directory" : shown;
        output.WriteLine($"Created a StrataRec workspace in {where}:");
        var width = 0;
        foreach (var file in files)
        {
            width = Math.Max(width, Join(shown, file.Path).Length);
        }

        foreach (var file in files)
        {
            var note = existing.Contains(file.Path) ? " (overwritten)" : string.Empty;
            output.WriteLine($"  {Join(shown, file.Path).PadRight(width)}  {file.Description}{note}");
        }

        output.WriteLine("Each file starts with a comment on what it is and what to change first.");

        var config = Quote(shown);
        var request = Quote(Join(shown, InitTemplates.RequestPath));
        output.WriteLine();
        output.WriteLine("Next:");
        output.WriteLine("  # static checks: types, data flow, every experiment arm");
        output.WriteLine($"  stratarec validate -c {config}{select}");
        output.WriteLine($"  # run the request (assigned to {InitTemplates.ExperimentId}/{InitTemplates.AssignedArm}); --trace adds each node's outcome");
        output.WriteLine($"  stratarec run -c {config} -r {request} --trace");
        output.WriteLine("  # the same request in the control arm (--arms overrides the request's assignment)");
        output.WriteLine($"  stratarec run -c {config} -r {request} --arms {InitTemplates.ExperimentId}/{InitTemplates.ControlArm}");
        output.WriteLine("  # the compiled plan: nodes in order, the experiment and the parameters its arms set");
        output.WriteLine($"  stratarec explain-plan -c {config}{select}");
        return 0;
    }

    /// <summary>
    /// The rule of experiment ids. The engine does not restrict pipeline ids, but init writes this one into file names
    /// and JSON strings, which the rule keeps plain: no separators, no escapes.
    /// </summary>
    internal static bool IsValidId(string id)
    {
        if (id.Length == 0 || !(char.IsAsciiLetter(id[0]) || id[0] == '_'))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What to name in the way of an init without --force: a file it would overwrite, else the first entry that is not
    /// hidden; null when there is none (init writes no hidden file, so a hidden entry is never overwritten).
    /// </summary>
    private static string? InTheWay(string root, List<string> existing)
    {
        if (existing.Count > 0)
        {
            return existing[0];
        }

        var entries = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith('.'))
            {
                entries.Add(name + (Directory.Exists(entry) ? "/" : string.Empty));
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        entries.Sort(StringComparer.Ordinal);
        return entries[0];
    }

    /// <summary>A directory where the file would go, or a file where one of its directories would; null when the path is free.</summary>
    private static string? Blocked(string root, string relative)
    {
        if (Directory.Exists(Path.Combine(root, relative)))
        {
            return relative + "/";
        }

        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        while (slash > 0)
        {
            var parent = relative[..slash];
            if (File.Exists(Path.Combine(root, parent)))
            {
                return parent;
            }

            slash = relative.IndexOf('/', slash + 1);
        }

        return null;
    }

    private static bool OtherPipelines(string root, string ours)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "pipelines")))
        {
            var name = Path.GetFileName(file);
            if ((name.EndsWith(".jsonc", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal))
                && !string.Equals(name, Path.GetFileName(ours), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The directory as typed, without trailing separators; <c>.</c> for the current directory.</summary>
    private static string Shown(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return ".";
        }

        var trimmed = directory.TrimEnd('/', Path.DirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? directory : trimmed; // "/" and "C:\" stay as they are
    }

    /// <summary>A workspace path as the user would type it from where they ran init.</summary>
    private static string Join(string shown, string relative)
    {
        var native = relative.Replace('/', Path.DirectorySeparatorChar);
        return shown == "." ? native : Path.Join(shown, native);
    }

    /// <summary>Quotes an argument a shell would split or expand.</summary>
    private static string Quote(string argument)
    {
        foreach (var c in argument)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '\\' or ':' or '+' or '@' or '=' or ','))
            {
                return "\"" + argument + "\"";
            }
        }

        return argument;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static int Fail(DiagnosticBag diagnostics, TextWriter errors)
    {
        Output.Diagnostics(diagnostics.Items, "text", errors);
        return 2;
    }
}
