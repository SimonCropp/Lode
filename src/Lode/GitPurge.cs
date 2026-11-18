/// <summary>
/// Git purge: deletes every file and folder git doesn't track, ignored ones included (bin, obj,
/// node_modules and the like), as git clean -xdf would, leaving repositories nested inside
/// alone. They go to the Recycle Bin, unless Options says to delete them outright.
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
    static readonly EnumerationOptions folderOptions = new()
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
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
    /// ignored ones included, but no repository nested inside. A folder holding nothing tracked
    /// is one entry.
    /// </summary>
    public static Found Find(string folder)
    {
        var result = CodeCommands.Git(folder, "-c", "core.quotepath=off", "clean", "-xdn");
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
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = line[prefix.Length..].TrimEnd('\r');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

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
    /// dialog belongs to.
    /// </summary>
    public static Task<ActionLog.Outcome> Run(IReadOnlyList<string> folders, bool permanent, IntPtr owner, IProgress<string>? progress = null)
    {
        var remove = ToRecycleBin(owner);
        if (permanent)
        {
            remove = Outright;
        }

        return Run(folders, remove, permanent, progress);
    }

    /// <summary>
    /// Finds what git doesn't track in each folder, then deletes all of it in one go with
    /// <paramref name="remove"/>.
    /// </summary>
    internal static async Task<ActionLog.Outcome> Run(IReadOnlyList<string> folders, Remover remove, bool permanent, IProgress<string>? progress = null)
    {
        var found = await Task.Run(
            () => folders
                .Select((folder, index) =>
                {
                    if (folders.Count > 1)
                    {
                        progress?.Report($"Looking in {index + 1} of {folders.Count}: {Path.GetFileName(folder)}");
                    }

                    return Find(folder);
                })
                .ToList());

        var paths = found
            .SelectMany(_ => _.Entries)
            .Select(_ => _.Path)
            .ToList();
        IReadOnlyList<Failure> failures = [];
        if (paths.Count > 0)
        {
            failures = await remove(paths, progress);
        }

        return Outcome(found, failures, permanent);
    }

    // As one shell operation, so there is one progress dialog for the lot. Lode has asked
    // already, so the shell doesn't, except to warn before deleting outright something too big
    // for the Recycle Bin.
    static Remover ToRecycleBin(IntPtr owner) =>
        (paths, progress) =>
        {
            progress?.Report($"Moving {Items(paths.Count)} to the Recycle Bin");
            return FileOperations.OnStaThread<IReadOnlyList<Failure>>(
                () =>
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
        Task.Run<IReadOnlyList<Failure>>(
            () =>
            {
                var failures = new List<Failure>();
                for (var index = 0; index < paths.Count; index++)
                {
                    progress?.Report($"Deleting {index + 1} of {paths.Count}");
                    try
                    {
                        DeleteOutright(paths[index]);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new(paths[index], exception.Message));
                    }
                }

                return failures;
            });

    static void DeleteOutright(string path)
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

    static readonly EnumerationOptions contentOptions = new()
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        var failed = failures.ToDictionary(_ => _.Path, _ => _.Reason, StringComparer.OrdinalIgnoreCase);
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
        foreach (var folder in found.OrderBy(_ => _.Error == null && _.Entries.All(entry => !failed.ContainsKey(entry.Path))))
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

            var notDeleted = folder.Entries.Count(_ => failed.ContainsKey(_.Path));
            var state = Items(folder.Entries.Count);
            if (folder.Entries.Count == 0)
            {
                state = "nothing to purge";
            }
            else if (notDeleted > 0)
            {
                state = $"{notDeleted} of {Items(folder.Entries.Count)} couldn't be {where}";
            }

            details.AppendLine($"{name}: {state}");
            foreach (var entry in folder.Entries.OrderBy(_ => !failed.ContainsKey(_.Path)))
            {
                if (failed.TryGetValue(entry.Path, out var reason))
                {
                    details.AppendLine($"  {entry.Name}: {reason}");
                    continue;
                }

                details.AppendLine($"  {entry.Name}");
            }
        }

        return new(failed.Count == 0 && errors == 0, summary, details.ToString().TrimEnd());
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
