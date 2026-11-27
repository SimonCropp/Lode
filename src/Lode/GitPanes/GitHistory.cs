/// <summary>
/// A commit as History lists it: who made it and when, its subject, its parents, none for the
/// first commit and two for a merge, and the tags on it.
/// </summary>
sealed record GitCommit(
    string Sha,
    IReadOnlyList<string> Parents,
    string Author,
    DateTimeOffset When,
    IReadOnlyList<string> Tags,
    string Subject)
{
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
}

/// <summary>
/// Reads a repository's history for the History pane, with the git the user has installed: the
/// commits of the branch checked out, newest first, a page at a time, and the files each changed.
/// History reads them with <see cref="InProcessHistory"/> first, in process, and with this where
/// that can't.
/// </summary>
static class GitHistory
{
    /// <summary>
    /// How many commits are read at a time: the next page is read as the list nears its end.
    /// </summary>
    public const int Page = 200;

    /// <summary>
    /// The commits from HEAD back, after the first <paramref name="skip"/>. Empty before the first
    /// commit, where git has nothing to log; null when git can't be asked.
    /// </summary>
    public static List<GitCommit>? Read(string repo, int skip, int count, Cancel cancel) =>
        Read(repo, "HEAD", skip, count, cancel);

    /// <summary>
    /// The commits of <paramref name="range"/>, newest first, after the first
    /// <paramref name="skip"/>: HEAD's, or, comparing with a branch, those only one of them has,
    /// main..HEAD say.
    /// </summary>
    public static List<GitCommit>? Read(string repo, string range, int skip, int count, Cancel cancel)
    {
        // Each commit ended by a NUL, its fields apart by a unit separator, which no name or
        // subject has. Signatures not checked, as log.showSignature would have gpg write into
        // the output.
        var result = CodeCommands.GitOutput(
            repo,
            cancel,
            [
                "--no-optional-locks",
                "log",
                "-z",
                "--no-show-signature",
                "--encoding=UTF-8",
                "--format=%H%x1f%P%x1f%aN%x1f%at%x1f%D%x1f%s",
                $"--max-count={count}",
                $"--skip={skip}",
                range,
                "--"
            ]);
        if (result.Succeeded)
        {
            return Parse(result.Output);
        }

        // No commits yet, so no HEAD to log from.
        if (result.Error.Contains("does not have any commits", StringComparison.Ordinal) ||
            result.Error.Contains("bad revision 'HEAD'", StringComparison.Ordinal) ||
            result.Error.Contains("unknown revision", StringComparison.Ordinal))
        {
            return [];
        }

        return null;
    }

    /// <summary>
    /// What git log -z said, in the format <see cref="Read"/> asks for.
    /// </summary>
    internal static List<GitCommit> Parse(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\0'))
        {
            var fields = record.TrimStart('\n').Split('\u001f');
            if (fields.Length < 6 ||
                fields[0].Length == 0)
            {
                continue;
            }

            _ = long.TryParse(fields[3], out var seconds);
            commits.Add(
                new(
                    fields[0],
                    fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    fields[2],
                    DateTimeOffset.FromUnixTimeSeconds(seconds),
                    TagsIn(fields[4]),
                    // A subject with a unit separator of its own is whole again.
                    string.Join('\u001f', fields[5..])));
        }

        return commits;
    }

    // The tags among what points at a commit, as %D lists it: "HEAD -> main, tag: v1.0,
    // origin/main".
    static List<string> TagsIn(string decorations)
    {
        const string prefix = "tag: ";
        return decorations
            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(_ => _.StartsWith(prefix, StringComparison.Ordinal))
            .Select(_ => _[prefix.Length..])
            .ToList();
    }

    /// <summary>
    /// The commits on the branch that the branch it tracks hasn't got yet, to mark as not pushed.
    /// None without a branch tracked.
    /// </summary>
    public static HashSet<string> Unpushed(string repo, Cancel cancel)
    {
        var result = CodeCommands.GitOutput(repo, cancel, ["--no-optional-locks", "rev-list", "@{upstream}..HEAD", "--"]);
        if (!result.Succeeded)
        {
            return [];
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// How far HEAD and <paramref name="branch"/> are apart: the commits HEAD has that the branch
    /// hasn't, and those the branch has that HEAD hasn't. Null when git can't say, the branch
    /// since deleted say.
    /// </summary>
    public static (int Ahead, int Behind)? Apart(string repo, string branch, Cancel cancel)
    {
        var result = CodeCommands.GitOutput(repo, cancel, ["--no-optional-locks", "rev-list", "--left-right", "--count", $"HEAD...{branch}", "--"]);
        var parts = result.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (!result.Succeeded ||
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var ahead) ||
            !int.TryParse(parts[1], out var behind))
        {
            return null;
        }

        return (ahead, behind);
    }

    /// <summary>
    /// The files <paramref name="commit"/> changed, against its first parent, so a merge's are
    /// what it brought into the branch. Null when git can't be asked.
    /// </summary>
    public static List<Change>? FilesOf(string repo, GitCommit commit, Cancel cancel)
    {
        // Given one commit, diff-tree says nothing of a merge: two trees always, or, for the
        // first commit, everything in it.
        string[] arguments;
        if (commit.Parents.Count == 0)
        {
            arguments = ["--no-optional-locks", "diff-tree", "-r", "-z", "--name-status", "-M", "--no-commit-id", "--root", commit.Sha];
        }
        else
        {
            arguments = ["--no-optional-locks", "diff-tree", "-r", "-z", "--name-status", "-M", "--no-commit-id", commit.Parents[0], commit.Sha];
        }

        var result = CodeCommands.GitOutput(repo, cancel, arguments);
        if (!result.Succeeded)
        {
            return null;
        }

        return ParseFiles(result.Output);
    }

    /// <summary>
    /// What git diff-tree --name-status -z said: a letter for what happened to each file, then
    /// its path, or, for a rename or copy, with how alike after the letter, where it was then
    /// where it is.
    /// </summary>
    internal static List<Change> ParseFiles(string output)
    {
        var files = new List<Change>();
        var parts = output.Split('\0');
        for (var index = 0; index < parts.Length; index++)
        {
            var status = parts[index].Trim('\n');
            if (status.Length == 0)
            {
                continue;
            }

            if (status[0] is 'R' or 'C')
            {
                if (index + 2 >= parts.Length)
                {
                    break;
                }

                var old = parts[index + 1];
                var path = parts[index + 2];
                index += 2;
                if (status[0] == 'R')
                {
                    files.Add(new(path, old, GitChange.Renamed));
                    continue;
                }

                files.Add(new(path, old, GitChange.Added));
                continue;
            }

            if (index + 1 >= parts.Length)
            {
                break;
            }

            var changed = parts[index + 1];
            index++;
            var kind = status[0] switch
            {
                'A' => GitChange.Added,
                'D' => GitChange.Deleted,
                'U' => GitChange.Conflicted,
                _ => GitChange.Modified
            };
            files.Add(new(changed, null, kind));
        }

        files.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return files;
    }

    /// <summary>
    /// A commit's whole message, for Copy message. Null when git can't be asked.
    /// </summary>
    public static string? Message(string repo, string sha)
    {
        var result = CodeCommands.GitOutput(repo, Cancel.None, ["--no-optional-locks", "log", "-1", "--no-show-signature", "--encoding=UTF-8", "--format=%B", sha, "--"]);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.Output.TrimEnd();
    }
}
