/// <summary>
/// Where a repository's working tree is: the branch it is on, or the commit when detached, how
/// many commits it is ahead of and behind the branch it tracks, as last fetched, and the paths
/// that have changes not yet committed, staged or not, files git doesn't track included.
/// <paramref name="Branch"/> is null when detached; <paramref name="Commit"/> is null before the
/// first commit. <paramref name="Files"/> are in order of their paths.
/// </summary>
sealed record GitState(string? Branch, string? Commit, int Ahead, int Behind, IReadOnlyList<GitFile> Files)
{
    /// <summary>
    /// Reads a repository's state. Null when it can't be read: not a repository git will take,
    /// say, or git not installed.
    /// </summary>
    public delegate GitState? Reader(string repo, Cancel cancel);

    public int Changes => Files.Count;

    // The same files too, not just the same list.
    public bool Equals(GitState? other) =>
        other != null &&
        Branch == other.Branch &&
        Commit == other.Commit &&
        Ahead == other.Ahead &&
        Behind == other.Behind &&
        Files.SequenceEqual(other.Files);

    public override int GetHashCode() =>
        HashCode.Combine(Branch, Commit, Ahead, Behind, Files.Count);

    /// <summary>
    /// The files in order of their paths, as each reader finds them in an order of its own.
    /// </summary>
    internal static List<GitFile> InOrder(IEnumerable<GitFile> files) =>
        files
            .OrderBy(_ => _.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The state as the status bar and the Git column show it: the branch, then what is to commit,
    /// push and pull, each only when there is any.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Branch == null)
        {
            // Only a commit can be checked out detached.
            parts.Add($"Detached at {Commit![..7]}");
        }
        else
        {
            parts.Add(Branch);
        }

        if (Commit == null)
        {
            parts.Add("no commits yet");
        }

        if (Changes > 0)
        {
            parts.Add(CountOf(Changes, "change"));
        }

        if (Ahead > 0)
        {
            parts.Add($"{Ahead} ahead");
        }

        if (Behind > 0)
        {
            parts.Add($"{Behind} behind");
        }

        return string.Join(" · ", parts);
    }

    internal static string CountOf(int count, string noun)
    {
        if (count == 1)
        {
            return $"1 {noun}";
        }

        return $"{count:N0} {noun}s";
    }

    /// <summary>
    /// Asks the git the user has installed, with their config, in one run of git status.
    /// </summary>
    public static GitState? FromGit(string repo, Cancel cancel)
    {
        // Status otherwise takes the index's lock to write back what it learned of the files,
        // and the user's own git commit, run meanwhile, would fail on that lock. Submodules count
        // only when they are at another commit: what is uncommitted inside one is its own. Each
        // entry ends in a NUL, and no path is quoted.
        var result = CodeCommands.Git(
            repo,
            cancel,
            network: false,
            "--no-optional-locks",
            "status",
            "--porcelain=v2",
            "--branch",
            "--ignore-submodules=dirty",
            "-z");
        if (!result.Succeeded)
        {
            return null;
        }

        return Parse(result.Output);
    }

    /// <summary>
    /// What git status --porcelain=v2 --branch -z said: headers starting "# branch.", then an
    /// entry for each path that has changed, "1" for one changed, "2" renamed or copied, with
    /// where it was as the next entry, "u" unmerged and "?" one git doesn't track, a folder of
    /// them being one, ending in a slash. Each ends in a NUL. After the last, a line break and
    /// whatever git warned of, which CodeCommands.Git adds.
    /// </summary>
    internal static GitState Parse(string output)
    {
        string? commit = null;
        string? branch = null;
        var ahead = 0;
        var behind = 0;
        var files = new List<GitFile>();
        var entries = output.Split('\0');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                commit = entry["# branch.oid ".Length..];
                if (commit == "(initial)")
                {
                    commit = null;
                }

                continue;
            }

            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = entry["# branch.head ".Length..];
                if (branch == "(detached)")
                {
                    branch = null;
                }

                continue;
            }

            // "+2 -1": ahead, then behind. Missing without an upstream, or one that has gone.
            if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = entry["# branch.ab ".Length..].Split(' ');
                if (counts is [var plus, var minus])
                {
                    _ = int.TryParse(plus.TrimStart('+'), out ahead);
                    _ = int.TryParse(minus.TrimStart('-'), out behind);
                }

                continue;
            }

            // "1 XY sub mH mI mW hH hI path", X what is staged, Y what isn't, "." for nothing.
            if (entry.StartsWith("1 ", StringComparison.Ordinal))
            {
                files.Add(new(PathAfter(entry, 8), ChangeOf(entry[2], entry[3])));
                continue;
            }

            // As "1", then how alike before the path, and where it was as the next entry.
            if (entry.StartsWith("2 ", StringComparison.Ordinal))
            {
                files.Add(new(PathAfter(entry, 9), ChangeOf(entry[2], entry[3])));
                index++;
                continue;
            }

            if (entry.StartsWith("u ", StringComparison.Ordinal))
            {
                files.Add(new(PathAfter(entry, 10), GitChange.Conflicted));
                continue;
            }

            if (entry.StartsWith("? ", StringComparison.Ordinal))
            {
                files.Add(new(entry[2..].TrimEnd('/'), GitChange.Untracked));
            }
        }

        return new(branch, commit, ahead, behind, InOrder(files));
    }

    // The path, after as many fields before it as there are, each followed by a space. The path
    // may have spaces of its own.
    static string PathAfter(string entry, int fields)
    {
        var start = 0;
        for (var field = 0; field < fields; field++)
        {
            start = entry.IndexOf(' ', start) + 1;
        }

        return entry[start..];
    }

    // What isn't staged says most of a file, as it is what is on disk.
    static GitChange ChangeOf(char staged, char unstaged)
    {
        if (unstaged == 'D')
        {
            return GitChange.Deleted;
        }

        if (unstaged != '.')
        {
            return GitChange.Modified;
        }

        return staged switch
        {
            'A' => GitChange.Added,
            'R' or 'C' => GitChange.Renamed,
            'D' => GitChange.Deleted,
            _ => GitChange.Staged
        };
    }
}

/// <summary>
/// A path that has changed, from the top of the repository, with forward slashes. A folder git
/// doesn't track, and so nothing in it, is one, without the slash git ends it with.
/// </summary>
sealed record GitFile(string Path, GitChange Change);

/// <summary>
/// What has happened to a path, as the Git column says it. Modified has changes not yet staged,
/// whatever is staged as well; the rest have only staged changes, but for Untracked, which git
/// doesn't track, and Conflicted, which a merge left to be resolved.
/// </summary>
enum GitChange
{
    Modified,
    Staged,
    Added,
    Renamed,
    Deleted,
    Untracked,
    Conflicted
}
