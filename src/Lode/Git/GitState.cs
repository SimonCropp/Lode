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

    /// <summary>
    /// Whether the branch tracks one, on origin say, which <see cref="Ahead"/> and
    /// <see cref="Behind"/> count against. Without one, both are 0.
    /// </summary>
    public bool Upstream { get; init; }

    /// <summary>
    /// Whether Git push would have nothing to do: no change to a file git tracks, staged or not,
    /// for it to commit, and no commit the branch it tracks hasn't got. Files git doesn't track
    /// don't count, as the commit leaves them. Never without a branch tracked, as the push could
    /// be what makes it.
    /// </summary>
    public bool NothingToPush =>
        Upstream &&
        Ahead == 0 &&
        Files.All(_ => _.Change == GitChange.Untracked);

    // The same files too, not just the same list.
    public bool Equals(GitState? other) =>
        other != null &&
        Branch == other.Branch &&
        Upstream == other.Upstream &&
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
    /// What a part of <see cref="Describe"/> says: the branch, or the commit when detached, that
    /// there are no commits yet, and what is to commit, push and pull. The status bar acts on
    /// each when clicked.
    /// </summary>
    public enum PartKind
    {
        Branch,
        NoCommits,
        Changes,
        Ahead,
        Behind
    }

    public sealed record Part(PartKind Kind, string Text);

    /// <summary>
    /// The parts of the state, in the order <see cref="Describe"/> has them, each only when there
    /// is any.
    /// </summary>
    public List<Part> Parts()
    {
        var parts = new List<Part>();
        if (Branch == null)
        {
            // Only a commit can be checked out detached.
            parts.Add(new(PartKind.Branch, $"Detached at {Commit![..7]}"));
        }
        else
        {
            parts.Add(new(PartKind.Branch, Branch));
        }

        if (Commit == null)
        {
            parts.Add(new(PartKind.NoCommits, "no commits yet"));
        }

        if (Changes > 0)
        {
            parts.Add(new(PartKind.Changes, CountOf(Changes, "change")));
        }

        if (Ahead > 0)
        {
            parts.Add(new(PartKind.Ahead, $"{Ahead} ahead"));
        }

        if (Behind > 0)
        {
            parts.Add(new(PartKind.Behind, $"{Behind} behind"));
        }

        return parts;
    }

    /// <summary>
    /// The state as the status bar and the Git column show it: the branch, then what is to commit,
    /// push and pull, each only when there is any.
    /// </summary>
    public string Describe() =>
        string.Join(" · ", Parts().Select(_ => _.Text));

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
        var upstream = false;
        var files = new List<GitFile>();

        // Each entry where it is in the output, so the only strings made are those kept: a
        // repository with many changes has as many entries.
        var text = output.AsSpan();
        var entries = text.Split('\0');
        while (entries.MoveNext())
        {
            var entry = text[entries.Current];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var oid = entry["# branch.oid ".Length..];
                if (oid is not "(initial)")
                {
                    commit = oid.ToString();
                }

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

            if (entry.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = true;
                continue;
            }

            // "+2 -1": ahead, then behind. Missing without an upstream, or one that has gone.
            if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = entry["# branch.ab ".Length..];
                var space = counts.IndexOf(' ');
                if (space >= 0 &&
                    counts.LastIndexOf(' ') == space)
                {
                    _ = int.TryParse(counts[..space].TrimStart('+'), out ahead);
                    _ = int.TryParse(counts[(space + 1)..].TrimStart('-'), out behind);
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
                entries.MoveNext();
                continue;
            }

            if (entry.StartsWith("u ", StringComparison.Ordinal))
            {
                files.Add(new(PathAfter(entry, 10), GitChange.Conflicted));
                continue;
            }

            if (entry.StartsWith("? ", StringComparison.Ordinal))
            {
                files.Add(new(entry[2..].TrimEnd('/').ToString(), GitChange.Untracked));
            }
        }

        return new(branch, commit, ahead, behind, InOrder(files))
        {
            Upstream = upstream
        };
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
