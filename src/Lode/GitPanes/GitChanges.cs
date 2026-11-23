/// <summary>
/// A file with changes not yet committed, as the Changes pane lists it, or one a commit changed,
/// as History lists it: its path from the top of the repository, with forward slashes, and, for a
/// rename, where it was.
/// </summary>
sealed record Change(string Path, string? OldPath, GitChange Kind)
{
    /// <summary>
    /// The paths a commit of it takes in: a rename both where it is and where it was, so the old
    /// path goes in the same commit.
    /// </summary>
    public IEnumerable<string> Paths()
    {
        yield return Path;
        if (OldPath != null &&
            Kind == GitChange.Renamed)
        {
            yield return OldPath;
        }
    }
}

/// <summary>
/// A file only some of whose changed lines are to be committed, with the patch of those lines
/// against the last commit, as <see cref="LinePatch"/> writes it.
/// </summary>
sealed record PartialChange(Change File, string Patch);

/// <summary>
/// What is uncommitted in a repository, file by file, for the Changes pane: each file git doesn't
/// track on its own, not the folder it is in, as each is ticked to commit on its own. With the
/// branch, null when detached, whether there is a commit yet, and whether a merge, cherry pick or
/// revert is part way, when git commits everything or nothing.
/// </summary>
sealed record GitChanges(string? Branch, bool HasCommits, bool Merging, IReadOnlyList<Change> Files)
{
    public static GitChanges Empty { get; } = new(null, true, false, []);

    /// <summary>
    /// Whether a file is left to resolve, which git won't commit until it is.
    /// </summary>
    public bool Conflicted => Files.Any(_ => _.Kind == GitChange.Conflicted);

    /// <summary>
    /// Reads what is uncommitted. Null when git can't, no git installed say.
    /// </summary>
    public static GitChanges? Read(string repo, Cancel cancel)
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
            return null;
        }

        return Parse(result.Output, IsMerging(repo));
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
        var hasCommits = true;
        var files = new List<Change>();
        var text = output.AsSpan();
        var entries = text.Split('\0');
        while (entries.MoveNext())
        {
            var entry = text[entries.Current];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                hasCommits = entry["# branch.oid ".Length..] is not "(initial)";
                continue;
            }

            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = entry["# branch.head ".Length..];
                if (head is not "(detached)")
                {
                    branch = head.ToString();
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
                files.Add(new(PathAfter(entry, 10), null, GitChange.Conflicted));
                continue;
            }

            if (entry.StartsWith("? ", StringComparison.Ordinal))
            {
                files.Add(new(entry[2..].ToString(), null, GitChange.Untracked));
            }
        }

        files.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return new(branch, hasCommits, merging, files);
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
    /// Commits <paramref name="ticked"/>, and only them, with <paramref name="message"/>, leaving
    /// whatever else is staged staged. Part way through a merge git commits everything or
    /// nothing, so then it is everything. Each step is let finish once started, as one stopped
    /// part way leaves the index locked.
    /// </summary>
    /// <param name="partial">Files only some of whose lines go in, each with the patch of those
    /// lines against the last commit.</param>
    public static CodeCommands.RepoResult Commit(string repo, IReadOnlyList<Change> ticked, bool merging, string message, Cancel cancel, IReadOnlyList<PartialChange>? partial = null)
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

            if (partial is { Count: > 0 })
            {
                return CommitSomeLines(repo, folder, messageFile, ticked, partial, cancel);
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
            // a file, as a command line holds only so many.
            var pathsFile = PathsFile(folder, "paths", ticked.SelectMany(_ => _.Paths()).ToList());
            return CodeCommands.GitToItsEnd(
                repo,
                cancel,
                "--literal-pathspecs",
                "commit",
                "--only",
                "-F",
                messageFile,
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
    /// A commit of whole files and of only some lines of others, as commit --only makes one of
    /// whole files: from an index of its own, the last commit's, with the whole files added to it
    /// and the patches of the lines applied. The real index then takes the commit's version of
    /// each path committed, so what of those files wasn't committed shows as changed still, and
    /// whatever else was staged stays staged. Git's hooks run on the commit as on any. Should the
    /// commit fail, the real index is as it was.
    /// </summary>
    static CodeCommands.RepoResult CommitSomeLines(string repo, string folder, string messageFile, IReadOnlyList<Change> whole, IReadOnlyList<PartialChange> partial, Cancel cancel)
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

        if (whole.Count > 0)
        {
            // Untracked, deleted and renamed alike: add -A takes each as it is on disk, gone or not.
            var wholeFile = PathsFile(folder, "whole", [.. whole.SelectMany(_ => _.Paths())]);
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

        var patchFile = Path.Combine(folder, "lines.patch");
        File.WriteAllText(patchFile, string.Concat(partial.Select(_ => _.Patch)), new UTF8Encoding(false));
        var applied = CodeCommands.GitToItsEnd(repo, cancel, environment, "apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", patchFile);
        if (!applied.Succeeded)
        {
            return applied;
        }

        var committed = CodeCommands.GitToItsEnd(repo, cancel, environment, "commit", "-F", messageFile);
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
