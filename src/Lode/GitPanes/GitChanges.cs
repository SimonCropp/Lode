/// <summary>
/// A file with changes not yet committed, as the Changes pane lists it, or one a commit changed,
/// as History lists it: its path from the top of the repository, with forward slashes, and, for a
/// rename, where it was. One in conflict says how it is left to settle.
/// </summary>
sealed record Change(string Path, string? OldPath, GitChange Kind, Conflict? Conflict = null)
{
    /// <summary>
    /// A file in conflict whose conflicts are all settled in its text, no markers left, so it can
    /// go in the commit of the merge as it is.
    /// </summary>
    public bool Settled =>
        Kind == GitChange.Conflicted &&
        Conflict is { Manual: false, Markers: 0 };

    /// <summary>
    /// The paths a commit of it takes in: a rename both where it is and where it was, so the old
    /// path goes in the same commit. Edited since, or deleted, it is Modified or Deleted, as what
    /// is on disk says most of a file, and still a rename: committing where it is alone would
    /// leave where it was in the commit, a copy.
    /// </summary>
    public IEnumerable<string> Paths()
    {
        yield return Path;
        if (OldPath != null)
        {
            yield return OldPath;
        }
    }
}

/// <summary>
/// How a file in conflict is left to settle: how many conflicts git marked in its text, and,
/// where one side deleted it, or it isn't text, that its markers can't settle it, so one side's
/// file is to be picked, as GitHub Desktop has a side picked for one.
/// </summary>
sealed record Conflict(int Markers, bool Manual, bool OursDeleted = false, bool TheirsDeleted = false)
{
    /// <summary>
    /// What the row says of it, as GitHub Desktop says it.
    /// </summary>
    public string Note
    {
        get
        {
            if (Manual)
            {
                return "Pick a side";
            }

            if (Markers == 0)
            {
                return "No conflicts left";
            }

            return GitState.CountOf(Markers, "conflict");
        }
    }
}

/// <summary>
/// A file only some of whose changed lines are to be committed, with the patch of those lines
/// against the last commit, as <see cref="LinePatch"/> writes it from the diff
/// <see cref="GitFileDiff.ReadToCommit"/> reads: a character a byte, as Latin-1 reads them.
/// </summary>
sealed record PartialChange(Change File, string Patch);

/// <summary>
/// What is uncommitted in a repository, file by file, for the Changes pane: each file git doesn't
/// track on its own, not the folder it is in, as each is ticked to commit on its own. With the
/// branch, null when detached, whether there is a commit yet, and whether a merge, cherry pick or
/// revert is part way, when git commits everything or nothing.
/// </summary>
/// <param name="MergingFrom">Part way through a merge, the branch being merged in, feature say,
/// as git's message for the merge names it; null where it can't be told.</param>
/// <param name="MergeSummary">Part way through a merge, the first line of git's message for it,
/// to start the summary with.</param>
/// <param name="Head">The commit checked out, null before the first, or where it wasn't read.</param>
/// <param name="Failure">Where git couldn't read what is uncommitted, what it said, with nothing
/// else read; null where it could.</param>
sealed record GitChanges(string? Branch, bool HasCommits, bool Merging, IReadOnlyList<Change> Files, string? MergingFrom = null, string? MergeSummary = null, string? Head = null, string? Failure = null)
{
    public static GitChanges Empty { get; } = new(null, true, false, []);

    /// <summary>
    /// Git couldn't read what is uncommitted, and said <paramref name="why"/>.
    /// </summary>
    public static GitChanges Failed(string why) =>
        Empty with
        {
            Failure = why
        };

    /// <summary>
    /// Whether a file is left to resolve, which git won't commit until it is: one in conflict
    /// with markers still in it, or a side yet to pick.
    /// </summary>
    public bool Conflicted => ConflictsLeft > 0;

    /// <summary>
    /// How many files are left to resolve.
    /// </summary>
    public int ConflictsLeft => Files
        .Count(_ => _ is
        {
            Kind: GitChange.Conflicted,
            Settled: false
        });

    /// <summary>
    /// Reads what is uncommitted. Null when git can't, no git installed say.
    /// </summary>
    public static GitChanges? Read(string repo, Cancel cancel)
    {
        var read = ReadOrWhyNot(repo, cancel);
        if (read.Failure != null)
        {
            return null;
        }

        return read;
    }

    /// <summary>
    /// Reads what is uncommitted, or, where git can't, what it said, as <see cref="Failure"/>,
    /// for the Changes pane to show: no git installed, say, or a repository git refuses as owned
    /// by another user, as it does one on exFAT or a share until it is a safe.directory.
    /// </summary>
    public static GitChanges ReadOrWhyNot(string repo, Cancel cancel)
    {
        // Without --no-optional-locks status writes the index back, and the watch on the code
        // folder hears the index change, has the pane read again, which writes it again.
        var result = CodeCommands.GitOutput(
            repo,
            cancel,
            [
                "--no-optional-locks",
                "status",
                "--porcelain=v2",
                "--branch",
                "-z",
                "--untracked-files=all",
                "--ignore-submodules=dirty"
            ]);
        if (!result.Succeeded)
        {
            var why = result.Error;
            if (why.Length == 0)
            {
                why = $"git status failed, ending with {result.ExitCode}.";
            }

            return Failed(why);
        }

        var merging = IsMerging(repo);
        var parsed = Parse(result.Output, merging);
        var files = parsed.Files
            .Select(_ => WithMarkers(repo, _))
            .ToList();
        var (from, summary) = MergeMessage(repo, merging);
        return parsed with
        {
            Files = files,
            MergingFrom = from,
            MergeSummary = summary
        };
    }

    // A file in conflict with how many conflicts are still marked in it, read from the file as
    // it is: git marks each with a line starting <<<<<<<. One gone, or that isn't text, a NUL in
    // it, markers can't settle: a side is to be picked.
    static Change WithMarkers(string repo, Change change)
    {
        if (change.Conflict is not { Manual: false } conflict)
        {
            return change;
        }

        try
        {
            var path = Path.Combine(repo, change.Path.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                return change with
                {
                    Conflict = conflict with
                    {
                        Manual = true
                    }
                };
            }

            var text = File.ReadAllText(path);
            if (text.Contains('\0'))
            {
                return change with
                {
                    Conflict = conflict with
                    {
                        Manual = true
                    }
                };
            }

            return change with
            {
                Conflict = conflict with
                {
                    Markers = CountMarkers(text)
                }
            };
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return change;
        }
    }

    /// <summary>
    /// How many conflicts git marked in <paramref name="text"/>: lines starting <<<<<<<.
    /// </summary>
    internal static int CountMarkers(string text)
    {
        var count = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    // The branch being merged in and the first line of git's message for the merge, from the
    // message git keeps while it is part way: "Merge branch 'feature'", "Merge branch 'feature'
    // of https://...", or "Merge remote-tracking branch 'origin/main'".
    static (string? From, string? Summary) MergeMessage(string repo, bool merging)
    {
        if (!merging ||
            CodeCommands.GitDirectory(repo) is not { } git)
        {
            return (null, null);
        }

        try
        {
            var path = Path.Combine(git, "MERGE_MSG");
            if (!File.Exists(path))
            {
                return (null, null);
            }

            var first = File.ReadLines(path).FirstOrDefault()?.Trim();
            return (MergedBranch(first), first);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The branch git's message for a merge names, the first quoted, or null.
    /// </summary>
    internal static string? MergedBranch(string? message)
    {
        if (message == null ||
            !message.StartsWith("Merge ", StringComparison.Ordinal))
        {
            return null;
        }

        var start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        var end = message.IndexOf('\'', start + 1);
        if (end < 0)
        {
            return null;
        }

        return message[(start + 1)..end];
    }

    /// <summary>
    /// Whether git is part way through a merge, cherry pick or revert, which it commits whole.
    /// </summary>
    internal static bool IsMerging(string repo)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git)
            {
                return false;
            }

            return File.Exists(Path.Combine(git, "MERGE_HEAD")) ||
                   File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD")) ||
                   File.Exists(Path.Combine(git, "REVERT_HEAD"));
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// What git status --porcelain=v2 --branch -z said, as <see cref="GitState.Parse"/> reads it,
    /// but keeping where a renamed file was, which git gives as the entry after it.
    /// </summary>
    internal static GitChanges Parse(string output, bool merging)
    {
        string? branch = null;
        string? head = null;
        var hasCommits = true;
        var files = new List<Change>();
        var text = output.AsSpan();
        var entries = text.Split('\0');
        while (entries.MoveNext())
        {
            var entry = text[entries.Current];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var oid = entry["# branch.oid ".Length..];
                hasCommits = oid is not "(initial)";
                if (hasCommits)
                {
                    head = oid.ToString();
                }

                continue;
            }

            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = entry["# branch.head ".Length..];
                if (name is not "(detached)")
                {
                    branch = name.ToString();
                }

                continue;
            }

            // "1 XY sub mH mI mW hH hI path", X what is staged, Y what isn't, "." for nothing.
            if (entry.StartsWith("1 ", StringComparison.Ordinal))
            {
                files.Add(new(PathAfter(entry, 8), null, GitState.ChangeOf(entry[2], entry[3])));
                continue;
            }

            // As "1", then how alike before the path, and where it was as the next entry. A copy
            // leaves where it came from as it was.
            if (entry.StartsWith("2 ", StringComparison.Ordinal))
            {
                var path = PathAfter(entry, 9);
                var kind = GitState.ChangeOf(entry[2], entry[3]);
                string? old = null;
                if (entries.MoveNext() &&
                    entry[2] == 'R')
                {
                    old = text[entries.Current].ToString();
                }

                // A copy is a new file, whatever it was copied from.
                if (entry[2] == 'C' &&
                    kind == GitChange.Renamed)
                {
                    kind = GitChange.Added;
                }

                files.Add(new(path, old, kind));
                continue;
            }

            if (entry.StartsWith("u ", StringComparison.Ordinal))
            {
                // "u XY ...": D where a side deleted it, DU deleted by us, UD by them. Taken to
                // have a conflict marked until the file is read, so it isn't settled unread.
                var oursDeleted = entry[2] == 'D';
                var theirsDeleted = entry[3] == 'D';
                files.Add(new(PathAfter(entry, 10), null, GitChange.Conflicted, new(1, oursDeleted || theirsDeleted, oursDeleted, theirsDeleted)));
                continue;
            }

            if (entry.StartsWith("? ", StringComparison.Ordinal))
            {
                files.Add(new(entry[2..].ToString(), null, GitChange.Untracked));
            }
        }

        files.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return new(branch, hasCommits, merging, files, Head: head);
    }

    // The path, after as many fields before it as there are, each followed by a space. The path
    // may have spaces of its own.
    static string PathAfter(CharSpan entry, int fields)
    {
        for (var field = 0; field < fields; field++)
        {
            entry = entry[(entry.IndexOf(' ') + 1)..];
        }

        return entry.ToString();
    }

    /// <summary>
    /// The message git is given: the summary, then, after a blank line, the description.
    /// </summary>
    internal static string Message(string summary, string description)
    {
        summary = summary.Trim();
        description = description.Trim();
        if (description.Length == 0)
        {
            return summary;
        }

        return $"{summary}\n\n{description}";
    }

    /// <summary>
    /// A commit's message as a summary and description, to commit again: its first paragraph, on
    /// one line however many it was written over, as git's subject is, then the rest.
    /// </summary>
    internal static (string Summary, string Description) SplitMessage(string message)
    {
        message = message.ReplaceLineEndings("\n").Trim();
        var subject = message;
        var description = "";
        var end = message.IndexOf("\n\n", StringComparison.Ordinal);
        if (end >= 0)
        {
            subject = message[..end];
            description = message[(end + 2)..].Trim();
        }

        var summary = string.Join(' ', subject.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return (summary, description);
    }

    /// <summary>
    /// Commits <paramref name="ticked"/>, and only them, with <paramref name="message"/>, leaving
    /// whatever else is staged staged. Part way through a merge git commits everything or
    /// nothing, so then it is everything. Each step is let finish once started, as one stopped
    /// part way leaves the index locked.
    /// </summary>
    /// <param name="partial">Files only some of whose lines go in, each with the patch of those
    /// lines against the last commit.</param>
    /// <param name="amend">Whether the commit goes in place of the last, as git commit --amend
    /// makes one: the last commit's files with the ticked ones as they are now, and its author.
    /// With none ticked, only its message changes.</param>
    public static CodeCommands.RepoResult Commit(string repo, IReadOnlyList<Change> ticked, bool merging, string message, Cancel cancel, IReadOnlyList<PartialChange>? partial = null, bool amend = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Lode", "Commits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Git takes the message as it is, # lines too, as it does with -F and no editor.
            var messageFile = Path.Combine(folder, "message.txt");
            File.WriteAllText(messageFile, message, new UTF8Encoding(false));
            if (merging)
            {
                var added = CodeCommands.GitToItsEnd(repo, cancel, "add", "-A");
                if (!added.Succeeded)
                {
                    return added;
                }

                return CodeCommands.GitToItsEnd(repo, cancel, "commit", "-F", messageFile);
            }

            // commit --only takes each path as it is on disk, so one whose removal is staged, with
            // a file there all the same, would go back in, tracked again: those are committed
            // from an index of their own, as some lines are.
            var removed = RemovedButOnDisk(repo, ticked);
            if (partial is { Count: > 0 } ||
                removed.Count > 0)
            {
                return CommitSomeLines(repo, folder, messageFile, ticked, partial ?? [], removed, amend, cancel);
            }

            // Git has to know of a file before a commit of only some paths can take it in. A
            // deleted file, or where a renamed one was, add would refuse: commit takes those as
            // they are, gone.
            var untracked = ticked
                .Where(_ => _.Kind == GitChange.Untracked)
                .Select(_ => _.Path)
                .ToList();
            if (untracked.Count > 0)
            {
                var untrackedFile = PathsFile(folder, "untracked", untracked);
                var added = CodeCommands.GitToItsEnd(
                    repo,
                    cancel,
                    "--literal-pathspecs",
                    "add",
                    $"--pathspec-from-file={untrackedFile}",
                    "--pathspec-file-nul");
                if (!added.Succeeded)
                {
                    return added;
                }
            }

            // --only commits these paths as they are on disk, and none of the others staged,
            // which stay staged. Literal, so a name like [x].txt isn't taken as a pattern, and in
            // a file, as a command line holds only so many. Amending, --only with no paths at all
            // changes only the message, leaving what is staged staged.
            List<string> arguments = ["--literal-pathspecs", "commit", "--only", "-F", messageFile];
            if (amend)
            {
                arguments.Add("--amend");
            }

            if (ticked.Count > 0)
            {
                var pathsFile = PathsFile(folder, "paths", ticked.SelectMany(_ => _.Paths()).ToList());
                arguments.AddRange([$"--pathspec-from-file={pathsFile}", "--pathspec-file-nul"]);
            }

            return CodeCommands.GitToItsEnd(repo, cancel, arguments.ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A commit of whole files and of only some lines of others, as commit --only makes one of
    /// whole files: from an index of its own, the last commit's, with the whole files added to it
    /// and the patches of the lines applied. The real index then takes the commit's version of
    /// each path committed, so what of those files wasn't committed shows as changed still, and
    /// whatever else was staged stays staged. Git's hooks run on the commit as on any. Should the
    /// commit fail, the real index is as it was.
    /// </summary>
    /// <param name="removed">Paths whose removal is staged, with a file on disk all the same,
    /// which go from the commit, the file left on disk, untracked.</param>
    static CodeCommands.RepoResult CommitSomeLines(string repo, string folder, string messageFile, IReadOnlyList<Change> whole, IReadOnlyList<PartialChange> partial, IReadOnlyList<string> removed, bool amend, Cancel cancel)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_INDEX_FILE"] = Path.Combine(folder, "index")
        };
        var head = CodeCommands.GitOutput(repo, cancel, ["rev-parse", "--verify", "--quiet", "HEAD"]);
        string[] readTree = ["read-tree", "--empty"];
        if (head.Succeeded)
        {
            readTree = ["read-tree", "HEAD"];
        }

        var read = CodeCommands.GitToItsEnd(repo, cancel, environment, readTree);
        if (!read.Succeeded)
        {
            return read;
        }

        // Untracked, deleted and renamed alike: add -A takes each as it is on disk, gone or not,
        // but for those removed, which it would take back in.
        var wholePaths = whole
            .SelectMany(_ => _.Paths())
            .Except(removed, StringComparer.Ordinal)
            .ToList();
        if (wholePaths.Count > 0)
        {
            var wholeFile = PathsFile(folder, "whole", wholePaths);
            var added = CodeCommands.GitToItsEnd(
                repo,
                cancel,
                environment,
                "--literal-pathspecs",
                "add",
                "-A",
                $"--pathspec-from-file={wholeFile}",
                "--pathspec-file-nul");
            if (!added.Succeeded)
            {
                return added;
            }
        }

        // From the index alone: the files stay on disk, untracked.
        if (removed.Count > 0)
        {
            var removedFile = PathsFile(folder, "removed", removed);
            var taken = CodeCommands.GitToItsEnd(
                repo,
                cancel,
                environment,
                "--literal-pathspecs",
                "rm",
                "--cached",
                "--quiet",
                "--ignore-unmatch",
                $"--pathspec-from-file={removedFile}",
                "--pathspec-file-nul");
            if (!taken.Succeeded)
            {
                return taken;
            }
        }

        // Git refuses a patch with nothing in it. A character a byte, as the patches were read
        // from git's diffs, so the lines go in with the bytes the files have, whatever their
        // encoding.
        if (partial.Count > 0)
        {
            var patchFile = Path.Combine(folder, "lines.patch");
            File.WriteAllText(patchFile, string.Concat(partial.Select(_ => _.Patch)), Encoding.Latin1);
            var applied = CodeCommands.GitToItsEnd(repo, cancel, environment, "apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", patchFile);
            if (!applied.Succeeded)
            {
                return applied;
            }
        }

        // Amending, the index read from the last commit is what goes in its place.
        string[] commit = ["commit", "-F", messageFile];
        if (amend)
        {
            commit = ["commit", "--amend", "-F", messageFile];
        }

        var committed = CodeCommands.GitToItsEnd(repo, cancel, environment, commit);
        if (!committed.Succeeded)
        {
            return committed;
        }

        // Not cancelled from here: the commit is made, and the real index has to know of it.
        var pathsFile = PathsFile(
            folder,
            "committed",
            [
                .. whole
                    .SelectMany(_ => _.Paths()),
                .. partial.Select(_ => _.File.Path)
            ]);
        var reset = CodeCommands.GitToItsEnd(
            repo,
            Cancel.None,
            "--literal-pathspecs",
            "reset",
            "-q",
            $"--pathspec-from-file={pathsFile}",
            "--pathspec-file-nul");
        if (!reset.Succeeded)
        {
            return reset;
        }

        return committed;
    }

    // The paths of ticked changes whose removal is staged, with a file on disk all the same,
    // which a commit isn't to take in: one taken out of the index alone, by git rm --cached,
    // ignored, say, and where a renamed file was, a new file there since. But not one ticked as
    // a new file too, as a row of the same path shares the tick: that goes in as it is on disk.
    static List<string> RemovedButOnDisk(string repo, IReadOnlyList<Change> ticked)
    {
        var untracked = ticked
            .Where(_ => _.Kind == GitChange.Untracked)
            .Select(_ => _.Path)
            .ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var change in ticked)
        {
            // Deleted with the file on disk can only be its removal staged: deleted on disk
            // alone, it isn't there.
            if (change.Kind == GitChange.Deleted)
            {
                removed.Add(change.Path);
            }

            if (change.OldPath is { } old)
            {
                removed.Add(old);
            }
        }

        return removed
            .Where(_ => !untracked.Contains(_) &&
                        File.Exists(Path.Combine(repo, _.Replace('/', '\\'))))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // A file of paths, each ended by a NUL, for git's --pathspec-from-file.
    static string PathsFile(string folder, string name, IReadOnlyList<string> paths)
    {
        var file = Path.Combine(folder, name);
        var text = new StringBuilder();
        foreach (var path in paths)
        {
            text.Append(path).Append('\0');
        }

        File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
        return file;
    }

    /// <summary>
    /// Settles a file in conflict with one side's file whole, as GitHub Desktop's Use the
    /// modified file from main does: the branch checked out's with <paramref name="ours"/>,
    /// otherwise the branch merged in's. Where that side deleted it, it is deleted.
    /// </summary>
    public static CodeCommands.RepoResult UseSide(string repo, Change change, bool ours, Cancel cancel)
    {
        var deleted = change.Conflict is { } conflict &&
                      (ours ? conflict.OursDeleted : conflict.TheirsDeleted);
        if (deleted)
        {
            return CodeCommands.GitToItsEnd(repo, cancel, "--literal-pathspecs", "rm", "--quiet", "--", change.Path);
        }

        var side = "--theirs";
        if (ours)
        {
            side = "--ours";
        }

        var checkout = CodeCommands.GitToItsEnd(repo, cancel, "--literal-pathspecs", "checkout", side, "--", change.Path);
        if (!checkout.Succeeded)
        {
            return checkout;
        }

        return CodeCommands.GitToItsEnd(repo, cancel, "--literal-pathspecs", "add", "--", change.Path);
    }

    /// <summary>
    /// Takes files in conflict as they are now, settled: as edited, or gone where deleted.
    /// </summary>
    public static CodeCommands.RepoResult MarkResolved(string repo, IReadOnlyList<Change> changes, Cancel cancel) =>
        CodeCommands.GitToItsEnd(repo, cancel, ["--literal-pathspecs", "add", "--all", "--", .. changes.Select(_ => _.Path)]);

    /// <summary>
    /// Gives up the merge, cherry pick or revert part way, the branch and its files as they were
    /// before it, as GitHub Desktop's Abort merge does.
    /// </summary>
    public static CodeCommands.RepoResult Abort(string repo, Cancel cancel)
    {
        var command = "merge";
        if (CodeCommands.GitDirectory(repo) is { } git)
        {
            if (File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD")))
            {
                command = "cherry-pick";
            }
            else if (File.Exists(Path.Combine(git, "REVERT_HEAD")))
            {
                command = "revert";
            }
        }

        return CodeCommands.GitToItsEnd(repo, cancel, command, "--abort");
    }

    /// <summary>
    /// The files on disk that discarding <paramref name="changes"/> writes over or takes away,
    /// for the Recycle Bin to have first, whatever their kind: a file whose change is Deleted
    /// can still be there, taken out of the index alone by git rm --cached, and where a rename
    /// was from can hold a new file. Each once, as two rows can share a path.
    /// </summary>
    public static List<string> OnDisk(string repo, IReadOnlyList<Change> changes) =>
        changes
            .SelectMany(_ => _.Paths())
            .Select(_ => Path.Combine(repo, _.Replace('/', '\\')))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Puts <paramref name="changes"/> back as the last commit has them, and takes files git
    /// doesn't track away. The files as they were go to the Recycle Bin first, by the caller,
    /// so nothing is lost. Before the first commit, there is nothing to put back: what is staged
    /// is only unstaged.
    /// </summary>
    public static CodeCommands.RepoResult Restore(string repo, IReadOnlyList<Change> changes, bool hasCommits, Cancel cancel)
    {
        // Git knows nothing of a file it doesn't track, and would refuse the whole restore for
        // it: the Recycle Bin has already taken it.
        var tracked = changes
            .Where(_ => _.Kind != GitChange.Untracked)
            .SelectMany(_ => _.Paths())
            .ToList();
        if (tracked.Count == 0)
        {
            return new(repo, true, "");
        }

        var folder = Path.Combine(Path.GetTempPath(), "Lode", "Commits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var pathsFile = PathsFile(folder, "paths", tracked);
            if (!hasCommits)
            {
                return CodeCommands.GitToItsEnd(
                    repo,
                    cancel,
                    "--literal-pathspecs",
                    "rm",
                    "--cached",
                    "--quiet",
                    "-r",
                    $"--pathspec-from-file={pathsFile}",
                    "--pathspec-file-nul");
            }

            return CodeCommands.GitToItsEnd(
                repo,
                cancel,
                "--literal-pathspecs",
                "restore",
                "--source=HEAD",
                "--staged",
                "--worktree",
                $"--pathspec-from-file={pathsFile}",
                "--pathspec-file-nul");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Has git ignore <paramref name="path"/>, a file it doesn't track: a line for it alone, from
    /// the top of the repository, at the end of the .gitignore there.
    /// </summary>
    public static void Ignore(string repo, string path)
    {
        var file = Path.Combine(repo, ".gitignore");
        var existing = "";
        if (File.Exists(file))
        {
            existing = File.ReadAllText(file);
        }

        var line = IgnoreLine(path);
        if (existing.Length > 0 &&
            !existing.EndsWith('\n'))
        {
            line = "\n" + line;
        }

        File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// The .gitignore line matching <paramref name="path"/> alone: from the top, so a file of
    /// the same name elsewhere isn't ignored too, and what a pattern would read as wildcards, or
    /// a space git would trim from the end, escaped.
    /// </summary>
    internal static string IgnoreLine(string path)
    {
        var line = new StringBuilder("/");
        foreach (var character in path)
        {
            if (character is '\\' or '*' or '?' or '[')
            {
                line.Append('\\');
            }

            line.Append(character);
        }

        // Git trims spaces after the last that isn't one, an escaped space counting as one, so
        // escaping the last keeps those before it too.
        if (line[^1] == ' ')
        {
            line.Insert(line.Length - 1, "\\");
        }

        return line.ToString();
    }
}
