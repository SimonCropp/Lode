/// <summary>
/// Git purge: deletes every file and folder git doesn't track, ignored ones included (bin, obj,
/// node_modules and the like), as git clean -xdf would, leaving repositories nested inside
/// alone, and what Options keeps. They go to the Recycle Bin, unless Options says to delete them
/// outright.
/// </summary>
static class GitPurge
{
    /// <summary>
    /// Something to delete: <paramref name="Name"/> as git shows it, relative to the folder
    /// purged, with a slash after a folder, and its full <paramref name="Path"/>.
    /// </summary>
    public sealed record Entry(string Name, string Path);

    /// <summary>
    /// What git found to delete in a folder, or, as <paramref name="Error"/>, why it couldn't look.
    /// </summary>
    public sealed record Found(string Folder, IReadOnlyList<Entry> Entries, string? Error);

    /// <summary>
    /// A path that wasn't deleted, and why.
    /// </summary>
    public sealed record Failure(string Path, string Reason);

    /// <summary>
    /// Deletes the paths, reporting how far it has got, and says which it couldn't.
    /// </summary>
    public delegate Task<IReadOnlyList<Failure>> Remover(IReadOnlyList<string> paths, IProgress<string>? progress);

    /// <summary>
    /// The folders a purge works on: <paramref name="folder"/> alone when it is in a repository,
    /// at its top or anywhere below; otherwise every repository under it, however deep. Only
    /// repositories up to <paramref name="codeFolder"/> count: one above it, such as a home
    /// folder kept in git, would make everything in the code folder untracked.
    /// </summary>
    public static List<string> Folders(string folder, string codeFolder)
    {
        if (CodeCommands.RepoContaining(folder, codeFolder) != null)
        {
            return [folder];
        }

        var repos = new List<string>();
        Collect(folder, repos);
        repos.Sort(StringComparer.OrdinalIgnoreCase);
        return repos;
    }

    /// <summary>
    /// The folders a purge of every one of <paramref name="folders"/> works on, each once,
    /// without any that purging another already covers.
    /// </summary>
    public static List<string> Folders(IReadOnlyList<string> folders, string codeFolder)
    {
        if (folders is [var folder])
        {
            return Folders(folder, codeFolder);
        }

        var all = folders
            .SelectMany(_ => Folders(_, codeFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = all
            .Where(inner => !all.Any(outer => Covers(outer, inner)))
            .ToList();
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // Purging outer purges inner too when inner is inside it with no repository between them,
    // since a purge leaves nested repositories alone. Two purges of the same files would each
    // find, and try to delete, what the other had.
    static bool Covers(string outer, string inner)
    {
        if (Locations.Same(outer, inner) ||
            !Locations.IsWithin(inner, outer))
        {
            return false;
        }

        for (var current = inner; !Locations.Same(current, outer); current = Path.GetDirectoryName(current)!)
        {
            if (CodeCommands.IsRepo(current))
            {
                return false;
            }
        }

        return true;
    }

    // Links aren't followed, so a junction back up the tree can't send the walk round forever.
    static EnumerationOptions folderOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };

    static void Collect(string folder, List<string> repos)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder, "*", folderOptions).ToList();
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
        {
            // An unreadable folder holds no repositories we can use.
            return;
        }

        foreach (var child in children)
        {
            if (CodeCommands.IsRepo(child))
            {
                repos.Add(child);
                continue;
            }

            Collect(child, repos);
        }
    }

    /// <summary>
    /// What git clean -xd would delete from the folder: every file and folder git doesn't track,
    /// ignored ones included, but no repository nested inside, nor what matches a pattern of
    /// <paramref name="keep"/>, as .gitignore has them. A folder holding nothing tracked is one
    /// entry, unless it holds something kept, when what else it holds is listed instead.
    /// </summary>
    public static Found Find(string folder, IReadOnlyList<string>? keep = null)
    {
        // With -x git uses none of the ignore rules but those it is given, and leaves what they
        // match: a folder whole, or a file in a folder it would otherwise delete whole, the rest
        // of which it then deletes around it.
        List<string> arguments = ["-c", "core.quotepath=off", "clean", "-xdn"];
        foreach (var pattern in keep ?? [])
        {
            arguments.Add($"--exclude={pattern}");
        }

        var result = CodeCommands.Git(folder, [.. arguments]);
        if (!result.Succeeded)
        {
            return new(folder, [], result.Output);
        }

        return new(folder, Parse(folder, result.Output), null);
    }

    /// <summary>
    /// git clean's dry run, run in <paramref name="folder"/>: a "Would remove" line for each
    /// entry, relative to the folder. Run in a folder git ignores, the entries start "./", after
    /// a line saying it won't remove the folder itself. With core.quotepath off, git quotes a
    /// name only for a quote, backslash or control character, none of which Windows allows.
    /// </summary>
    internal static List<Entry> Parse(string folder, string output)
    {
        const string prefix = "Would remove ";
        var entries = new List<Entry>();

        // Each line where it is in the output, so the only strings made are the names.
        var text = output.AsSpan();
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[prefix.Length..].TrimEnd('\r');
            if (rest.StartsWith("./", StringComparison.Ordinal))
            {
                rest = rest[2..];
            }

            var name = rest.ToString();
            var path = Path.GetFullPath(Path.Combine(folder, name.TrimEnd('/')));

            // Never anything but what is in the folder.
            if (Locations.Same(path, folder) ||
                !Locations.IsWithin(path, folder))
            {
                continue;
            }

            entries.Add(new(name, path));
        }

        return entries;
    }

    /// <summary>
    /// Purges <paramref name="folders"/>, to the Recycle Bin unless
    /// <paramref name="permanent"/>. <paramref name="owner"/> is the window the shell's progress
    /// dialog belongs to. Once what will go is known, <paramref name="confirm"/>, if given, is
    /// asked, with <see cref="Question"/>, and nothing goes unless it says yes. What matches a
    /// pattern of <paramref name="keep"/>, as Options has them, is left.
    /// </summary>
    public static Task<ActionLog.Outcome> Run(IReadOnlyList<string> folders, bool permanent, IntPtr owner, IProgress<string>? progress = null, Func<string, bool>? confirm = null, IReadOnlyList<string>? keep = null)
    {
        var remove = ToRecycleBin(owner);
        if (permanent)
        {
            remove = Outright;
        }

        return Run(folders, remove, permanent, progress, confirm, keep);
    }

    /// <summary>
    /// Finds what git doesn't track in each folder, asks, then deletes all of it in one go with
    /// <paramref name="remove"/>.
    /// </summary>
    internal static async Task<ActionLog.Outcome> Run(IReadOnlyList<string> folders, Remover remove, bool permanent, IProgress<string>? progress = null, Func<string, bool>? confirm = null, IReadOnlyList<string>? keep = null)
    {
        // Several folders looked in at once, as git runs in each on its own.
        var found = await Task.Run(() => CodeCommands.InParallel(
            folders,
            _ => Find(_, keep),
            done =>
            {
                if (folders.Count > 1)
                {
                    progress?.Report($"Looked in {done} of {folders.Count}");
                }
            }));

        var paths = found
            .SelectMany(_ => _.Entries)
            .Select(_ => _.Path)
            .ToList();
        if (paths.Count > 0 &&
            confirm != null)
        {
            progress?.Report($"Measuring {Items(paths.Count)}");
            var size = await Task.Run(() => paths.Sum(Size));
            if (!confirm(Question(found, size, permanent, keep)))
            {
                return new(false, "Cancelled", "");
            }
        }

        IReadOnlyList<Failure> failures = [];
        if (paths.Count > 0)
        {
            failures = await remove(paths, progress);
        }

        return Outcome(found, failures, permanent);
    }

    // How many entries the question lists before saying how many more there are.
    const int listed = 10;

    // Folders builds and package managers make, and make again: what is in one, or is one, is
    // named after the rest.
    static HashSet<string> buildOutput = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "node_modules",
        "packages",
        "dist",
        "build",
        "out",
        "target",
        "artifacts",
        "TestResults",
        "coverage",
        "__pycache__",
        ".gradle",
        ".next"
    };

    static bool IsBuildOutput(string name) =>
        name
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(buildOutput.Contains);

    /// <summary>
    /// What a purge asks, once its dry run has found what will go: how many items, how big, the
    /// first of them by name, where they go, what is kept, and the folders git couldn't look in.
    /// Git lists them by name, so bin and obj folders by the dozen can come before a file someone
    /// wants, a .env Options doesn't keep, say, which is then never named: what isn't build output
    /// is named first.
    /// </summary>
    internal static string Question(IReadOnlyList<Found> found, long size, bool permanent, IReadOnlyList<string>? keep = null)
    {
        var withEntries = found.Where(_ => _.Entries.Count > 0).ToList();
        var entries = withEntries
            .SelectMany(_ => _.Entries.Select(entry => (_.Folder, entry.Name)))
            .OrderBy(_ => IsBuildOutput(_.Name))
            .ToList();
        var folders = withEntries
            .Select(_ => _.Folder)
            .ToList();
        var question = new StringBuilder();
        question.AppendLine($"Delete {Items(entries.Count)}, {Formatting.Size(size)}, that git doesn't track, ignored ones included, in {CodeMenu.Describe(folders)}?");
        question.AppendLine();
        foreach (var (folder, name) in entries.Take(listed))
        {
            if (folders.Count > 1)
            {
                question.AppendLine($"    {Path.GetFileName(folder)}/{name}");
                continue;
            }

            question.AppendLine($"    {name}");
        }

        if (entries.Count > listed)
        {
            question.AppendLine($"    and {entries.Count - listed:N0} more");
        }

        question.AppendLine();
        if (permanent)
        {
            question.Append("They are deleted permanently, as set in Options. This cannot be undone.");
        }
        else
        {
            question.Append("They go to the Recycle Bin.");
        }

        if (keep is { Count: > 0 })
        {
            question.AppendLine();
            question.AppendLine();
            question.Append($"Kept, as set in Options: {string.Join(' ', keep)}");
        }

        var errors = found.Count(_ => _.Error != null);
        if (errors > 0)
        {
            question.AppendLine();
            question.AppendLine();
            question.Append($"Git couldn't look in {Count(errors, "folder")}, which stays as it is.");
        }

        return question.ToString();
    }

    // How much a file, or all a folder holds, takes up. Links aren't followed, as they are
    // deleted as links.
    static long Size(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", sizeOptions)
                    .Sum(_ => _.Length);
            }

            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
        {
            // Unreadable, so not counted.
        }

        return 0;
    }

    static EnumerationOptions sizeOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    // As one shell operation, so there is one progress dialog for the lot. Lode has asked
    // already, so the shell doesn't, except to warn before deleting outright something too big
    // for the Recycle Bin.
    static Remover ToRecycleBin(IntPtr owner) =>
        (paths, progress) =>
        {
            progress?.Report($"Moving {Items(paths.Count)} to the Recycle Bin");
            return FileOperations.OnStaThread<IReadOnlyList<Failure>>(() =>
            {
                ShellFileOperation.Outcome outcome;
                try
                {
                    outcome = ShellFileOperation.Run(
                        owner,
                        ShellFileOperation.Kind.Delete,
                        paths,
                        null,
                        ShellFileOperation.AllowUndo |
                        ShellFileOperation.RecycleOnDelete |
                        ShellFileOperation.NoConfirmation |
                        ShellFileOperation.WantNukeWarning);
                }
                catch (Exception exception)
                {
                    // None of it done, which each says, so what was found is still listed.
                    return paths
                        .Select(_ => new Failure(_, exception.Message))
                        .ToList();
                }

                // What has gone since the dry run, removed by a build say, is as good as
                // purged.
                var done = outcome.Changes
                    .Select(_ => _.Source)
                    .Concat(outcome.Missing)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return paths
                    .Where(_ => !done.Contains(_))
                    .Select(_ => new Failure(_, "Not moved to the Recycle Bin"))
                    .ToList();
            });
        };

    // Straight through the file system: much faster than the shell for the thousands of small
    // files obj and node_modules hold.
    static Task<IReadOnlyList<Failure>> Outright(IReadOnlyList<string> paths, IProgress<string>? progress) =>
        Task.Run<IReadOnlyList<Failure>>(() =>
        {
            var failures = new List<Failure>();
            for (var index = 0; index < paths.Count; index++)
            {
                progress?.Report($"Deleting {index + 1} of {paths.Count}");
                try
                {
                    DeleteOutright(paths[index]);
                }
                catch (Exception exception)
                    when (
                        exception is
                            IOException or
                            UnauthorizedAccessException)
                {
                    failures.Add(new(paths[index], exception.Message));
                }
            }

            return failures;
        });

    /// <summary>
    /// Deletes a file or folder outright, not to the Recycle Bin, read-only files and all, as git
    /// leaves its objects.
    /// </summary>
    internal static void DeleteOutright(string path)
    {
        if (Directory.Exists(path))
        {
            Delete(new DirectoryInfo(path));
            return;
        }

        // Gone already counts as done.
        if (File.Exists(path))
        {
            Delete(new FileInfo(path));
        }
    }

    // Read-only files, as some tools leave them, go too. A link to a folder is deleted, not
    // what it points at.
    static void Delete(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
        }

        if (entry is DirectoryInfo folder &&
            !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            DeleteContents(folder);
        }

        entry.Delete();
    }

    static EnumerationOptions contentOptions = new()
    {
        AttributesToSkip = 0
    };

    // Carries on past a file that won't go, such as one in use, so as much goes as can; then
    // fails with the first problem, as the folder can't go.
    static void DeleteContents(DirectoryInfo folder)
    {
        Exception? first = null;
        foreach (var child in folder.EnumerateFileSystemInfos("*", contentOptions))
        {
            try
            {
                Delete(child);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
                first ??= exception;
            }
        }

        if (first != null)
        {
            ExceptionDispatchInfo.Throw(first);
        }
    }

    /// <summary>
    /// What happened: how many items went and where, how many didn't, and in which folders git
    /// failed; then, folder by folder, what was deleted, folders with a problem first.
    /// </summary>
    internal static ActionLog.Outcome Outcome(IReadOnlyList<Found> found, IReadOnlyList<Failure> failures, bool permanent)
    {
        var failed = failures.ToDictionary(
            _ => _.Path,
            _ => _.Reason,
            StringComparer.OrdinalIgnoreCase);
        var total = found.Sum(_ => _.Entries.Count);
        var errors = found.Count(_ => _.Error != null);

        var where = "moved to the Recycle Bin";
        if (permanent)
        {
            where = "deleted";
        }

        var parts = new List<string>();
        if (total > failed.Count)
        {
            parts.Add($"{Items(total - failed.Count)} {where}");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} couldn't be");
        }

        if (errors > 0)
        {
            parts.Add($"git failed in {Count(errors, "folder")}");
        }

        if (parts.Count == 0)
        {
            return new(true, "Nothing to purge", "");
        }

        var summary = string.Join(", ", parts);
        summary = char.ToUpperInvariant(summary[0]) + summary[1..];

        var details = new StringBuilder();
        foreach (var folder in found.OrderBy(_ => _.Error == null &&
                                                  _.Entries.All(entry => !failed.ContainsKey(entry.Path))))
        {
            if (details.Length > 0)
            {
                details.AppendLine();
            }

            var name = Path.GetFileName(folder.Folder);
            if (folder.Error != null)
            {
                details.AppendLine($"{name}: FAILED");
                foreach (var line in folder.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    details.AppendLine($"  {line}");
                }

                continue;
            }

            var entries = folder.Entries;
            var notDeleted = entries.Count(_ => failed.ContainsKey(_.Path));
            var state = Items(entries.Count);
            if (entries.Count == 0)
            {
                state = "nothing to purge";
            }
            else if (notDeleted > 0)
            {
                state = $"{notDeleted} of {Items(entries.Count)} couldn't be {where}";
            }

            details.AppendLine($"{name}: {state}");
            foreach (var entry in entries.OrderBy(_ => !failed.ContainsKey(_.Path)))
            {
                if (failed.TryGetValue(entry.Path, out var reason))
                {
                    details.AppendLine($"  {entry.Name}: {reason}");
                    continue;
                }

                details.AppendLine($"  {entry.Name}");
            }
        }

        return new(
            failed.Count == 0 &&
            errors == 0,
            summary,
            details.ToString().TrimEnd());
    }

    static string Items(int count) =>
        Count(count, "item");

    static string Count(int count, string noun)
    {
        if (count == 1)
        {
            return $"1 {noun}";
        }

        return $"{count:N0} {noun}s";
    }
}
