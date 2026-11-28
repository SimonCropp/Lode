/// <summary>
/// A repository's stashes, for Changes' Stashed changes, as GitHub Desktop has its own: those
/// Lode left on a branch switched off, and any stashed in a terminal or by another tool, each to
/// restore or discard; and every change stashed at once. Runs the git the user has installed, as
/// <see cref="CodeCommands"/> does, so nothing waits on an editor.
/// </summary>
static class GitStashes
{
    /// <summary>
    /// A stash: <paramref name="Name"/> as git names it when read, stash@{0} say, which the next
    /// stash made moves along, so it is acted on by <paramref name="Commit"/>, which stays; the
    /// branch it was made on, null when detached; git's subject for it, "On main: message"; and
    /// when it was made.
    /// </summary>
    public sealed record Stash(string Name, string Commit, string? Branch, string Subject, DateTimeOffset? Made)
    {
        /// <summary>
        /// What git's subject says after the branch: the message it was given, or, made without
        /// one, the commit it was made on, with its subject.
        /// </summary>
        public string Message => SplitSubject(Subject).Message;

        /// <summary>
        /// What the stash is, in words: its message, Lode's own as what they are, changes left on
        /// a branch switched off, and one made without a message as work in progress on the
        /// commit it was made on.
        /// </summary>
        public string Text
        {
            get
            {
                var message = Message;
                if (message.StartsWith(GitBranches.ClashedMarker, StringComparison.Ordinal))
                {
                    return $"Changes left on {message[GitBranches.ClashedMarker.Length..]} that clashed coming back";
                }

                if (message.StartsWith(GitBranches.StashMarker, StringComparison.Ordinal))
                {
                    return $"Changes left on {message[GitBranches.StashMarker.Length..]}";
                }

                if (Subject.StartsWith(wipPrefix, StringComparison.Ordinal))
                {
                    return $"Work in progress on {message}";
                }

                return message;
            }
        }
    }

    // Git's subject for a stash made with a message, and without.
    const string messagePrefix = "On ";
    const string wipPrefix = "WIP on ";

    // The branch git's subject starts with, null for "(no branch)", detached, or none at all, as
    // git stash store gives one it isn't told; then what it says after it.
    static (string? Branch, string Message) SplitSubject(string subject)
    {
        foreach (var prefix in (string[]) [wipPrefix, messagePrefix])
        {
            if (!subject.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // A colon can't be in a branch's name, so the first ": " ends it.
            var end = subject.IndexOf(": ", prefix.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var branch = subject[prefix.Length..end];
            var message = subject[(end + 2)..];
            if (branch == "(no branch)")
            {
                return (null, message);
            }

            return (branch, message);
        }

        return (null, subject);
    }

    /// <summary>
    /// The repository's stashes, newest first, as git lists them now; none where git can't list
    /// them. Shared by every worktree of the repository, as git shares them.
    /// </summary>
    public static IReadOnlyList<Stash> Read(string repo, Cancel cancel)
    {
        if (!MayHaveStashes(repo))
        {
            return [];
        }

        var listed = CodeCommands.GitOutput(repo, cancel, ["--no-optional-locks", "stash", "list", "--format=%gd%x00%H%x00%ct%x00%gs"]);
        if (!listed.Succeeded)
        {
            return [];
        }

        return Parse(listed.Output);
    }

    // Git keeps the stashes as the log of refs/stash, which it writes with the first, so without
    // it there are none, and no git need be started each time Changes reads the files. Kept in a
    // reftable, there is no such file to look for.
    static bool MayHaveStashes(string repo)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git)
            {
                return true;
            }

            var common = CodeCommands.CommonDirectory(git);
            return File.Exists(Path.Combine(common, "logs", "refs", "stash")) ||
                   Directory.Exists(Path.Combine(common, "reftable"));
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The stashes from what git stash list said, a line each: the stash's name, its commit, when
    /// it was made, and git's subject for it, each after a NUL.
    /// </summary>
    internal static List<Stash> Parse(string output)
    {
        var stashes = new List<Stash>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < 4)
            {
                continue;
            }

            DateTimeOffset? made = null;
            if (long.TryParse(fields[2], CultureInfo.InvariantCulture, out var seconds))
            {
                made = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            var subject = string.Join('\0', fields[3..]);
            stashes.Add(new(fields[0], fields[1], SplitSubject(subject).Branch, subject, made));
        }

        return stashes;
    }

    /// <summary>
    /// Restores the stash, as GitHub Desktop's Restore does: its changes put back, untracked
    /// files included, and the stash dropped once they are. Where git couldn't put them back
    /// whole, the stash is kept, and it says so: clashing with what is here, the clashes are
    /// marked in the files, as conflicts for Changes to settle; refused, over changes git would
    /// overwrite say, nothing changed.
    /// </summary>
    public static CodeCommands.RepoResult Restore(string repo, Stash stash, Cancel cancel)
    {
        var applied = CodeCommands.GitToItsEnd(repo, cancel, "stash", "apply", stash.Commit);
        if (!applied.Succeeded)
        {
            var name = NameOf(repo, stash.Commit) ?? stash.Name;
            if (CodeCommands.ConflictedFiles(repo).Length > 0)
            {
                return applied with
                {
                    Output = $"{stash.Text} clashed with what is here, so {name} is kept. The clashes are marked in the files: settle them in Changes, then discard the stash.\n{applied.Output}"
                };
            }

            return applied with
            {
                Output = $"Couldn't restore {stash.Text}, so {name} is kept:\n{applied.Output}"
            };
        }

        // Even cancelled since: restored, the changes would otherwise be here and in the stash
        // both, to be restored twice.
        var dropped = Drop(repo, stash, Cancel.None);
        if (!dropped.Succeeded)
        {
            return dropped with
            {
                Output = $"Restored {stash.Text}, but couldn't drop the stash, so it is still there:\n{dropped.Output}"
            };
        }

        return applied with
        {
            Output = $"Restored {stash.Text}.\n{applied.Output}".Trim()
        };
    }

    /// <summary>
    /// Drops the stash, its changes gone for good.
    /// </summary>
    public static CodeCommands.RepoResult Discard(string repo, Stash stash, Cancel cancel) =>
        Drop(repo, stash, cancel);

    // Drops the stash by the name it has now, found by its commit: a stash made since, here or in
    // another worktree of the repository, which shares them, moves the names along, and the one
    // named when it was read would be another.
    static CodeCommands.RepoResult Drop(string repo, Stash stash, Cancel cancel)
    {
        if (NameOf(repo, stash.Commit) is not { } name)
        {
            return new(repo, false, $"{stash.Text} isn't stashed any more.");
        }

        return CodeCommands.GitToItsEnd(repo, cancel, "stash", "drop", name);
    }

    // The stash's name now, stash@{2} say, by its commit; null where it is no longer stashed.
    static string? NameOf(string repo, string commit)
    {
        var listed = CodeCommands.GitOutput(repo, Cancel.None, ["stash", "list", "--format=%gd%x00%H"]);
        if (!listed.Succeeded)
        {
            return null;
        }

        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length == 2 &&
                fields[1] == commit)
            {
                return fields[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Stashes every change, untracked files included, but not what git ignores, with
    /// <paramref name="message"/>, leaving the files as the last commit has them.
    /// </summary>
    public static CodeCommands.RepoResult StashAll(string repo, string message, Cancel cancel) =>
        CodeCommands.GitToItsEnd(repo, cancel, "stash", "push", "--include-untracked", "-m", message);
}
