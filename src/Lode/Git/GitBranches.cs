/// <summary>
/// The branches of a repository, for the status bar's drop-up, and switching between them as
/// GitHub Desktop does: changes left on a branch are stashed, and put back on coming back to it
/// from the drop-up. Runs the git the user has installed, as <see cref="CodeCommands"/> does.
/// </summary>
static class GitBranches
{
    /// <summary>
    /// A branch to switch to: a local one, feature say, or one only on a remote, origin/feature,
    /// that no local branch tracks. <paramref name="Committed"/> is when its last commit was
    /// made. <paramref name="Stashed"/> when changes left on it are stashed, waiting for it.
    /// <paramref name="Upstream"/> is the remote branch a local one tracks, origin/feature say.
    /// </summary>
    public sealed record Branch(string Name, bool Remote, DateTimeOffset? Committed, bool Stashed, string? Upstream = null);

    /// <summary>
    /// The branches, grouped as GitHub Desktop groups them: origin's default branch, those
    /// switched to most recently, newest first, then the rest, local before remote, by name.
    /// Each branch is in one group only. <paramref name="Current"/> is the branch checked out,
    /// null when detached. <paramref name="Error"/> says why there are none, when git failed.
    /// </summary>
    public sealed record Listing(string? Current, Branch? Default, IReadOnlyList<Branch> Recent, IReadOnlyList<Branch> Other, string? Error = null)
    {
        public bool IsEmpty => Default == null &&
                               Recent.Count == 0 &&
                               Other.Count == 0;
    }

    /// <summary>
    /// How many recently switched to branches are listed, as GitHub Desktop has.
    /// </summary>
    internal const int RecentCount = 5;

    // Ahead of the branch's name in the message of the stash its changes are left in. A colon
    // can't be in a branch's name, so no branch's name ends another's marker.
    const string stashMarker = "Lode:";

    // How a stash is marked once putting it back has clashed: no longer one to put back.
    const string clashedMarker = "Lode clashed:";

    /// <summary>
    /// The repository's branches, as git has them now, without going to origin: the list is
    /// shown as it opens.
    /// </summary>
    public static Listing Read(string repo, Cancel cancel)
    {
        var refs = CodeCommands.Git(
            repo,
            cancel,
            network: false,
            "for-each-ref",
            "--format=%(HEAD)%00%(refname)%00%(upstream)%00%(committerdate:unix)",
            "refs/heads",
            "refs/remotes");
        if (!refs.Succeeded)
        {
            return new(null, null, [], [], refs.Output);
        }

        // A repository with no commits yet has no reflog, and one never stashed in no stashes,
        // and git fails for each: then there are none.
        var reflog = CodeCommands.Git(repo, cancel, network: false, "log", "-g", "--format=%gs", "-n", "1000", "HEAD", "--");
        var stashes = CodeCommands.Git(repo, cancel, network: false, "stash", "list", "--format=%gs");
        return Parse(
            refs.Output,
            CodeCommands.RecordedDefaultBranch(repo, cancel),
            OutputIfSucceeded(reflog),
            OutputIfSucceeded(stashes));
    }

    static string OutputIfSucceeded(CodeCommands.RepoResult result)
    {
        if (result.Succeeded)
        {
            return result.Output;
        }

        return "";
    }

    /// <summary>
    /// The branches, from what git said:
    /// <paramref name="refs"/>, a line for each branch, local and remote, from for-each-ref,
    /// "*" for the one checked out, its ref, its upstream, and its last commit's time, each
    /// after a NUL;
    /// <paramref name="recordedDefault"/>, the branch origin's HEAD names, as clone recorded it;
    /// <paramref name="reflog"/>, HEAD's reflog subjects, newest first;
    /// and <paramref name="stashes"/>, the stashes' subjects.
    /// </summary>
    internal static Listing Parse(string refs, string? recordedDefault, string reflog, string stashes)
    {
        const string localPrefix = "refs/heads/";
        const string remotePrefix = "refs/remotes/";
        string? current = null;
        var locals = new List<(string Name, DateTimeOffset? Committed, string? Upstream)>();
        var remotes = new List<(string Name, DateTimeOffset? Committed)>();
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 4)
            {
                continue;
            }

            var refName = fields[1];
            var committed = Committed(fields[3]);
            if (refName.StartsWith(localPrefix, StringComparison.Ordinal))
            {
                var name = refName[localPrefix.Length..];
                if (fields[0] == "*")
                {
                    current = name;
                }

                string? upstream = null;
                if (fields[2].StartsWith(remotePrefix, StringComparison.Ordinal))
                {
                    upstream = fields[2][remotePrefix.Length..];
                    tracked.Add(upstream);
                }

                locals.Add((name, committed, upstream));
                continue;
            }

            // origin/HEAD only names origin's default branch, which is listed itself.
            if (refName.StartsWith(remotePrefix, StringComparison.Ordinal) &&
                !refName.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                remotes.Add((refName[remotePrefix.Length..], committed));
            }
        }

        var stashed = StashedBranches(stashes);
        var localNames = locals.Select(_ => _.Name).ToHashSet(StringComparer.Ordinal);

        // A remote branch a local one tracks is that branch, so is listed once, as the local one.
        var branches = locals
            .Select(_ => new Branch(_.Name, false, _.Committed, stashed.Contains(_.Name), _.Upstream))
            .Concat(
                remotes
                    .Where(_ => !tracked.Contains(_.Name))
                    .Select(_ => new Branch(_.Name, true, _.Committed, false)))
            .ToList();

        var defaultBranch = DefaultOf(branches, localNames, recordedDefault);
        var recent = new List<Branch>();
        foreach (var name in SwitchedTo(reflog))
        {
            if (recent.Count == RecentCount)
            {
                break;
            }

            // A detached HEAD's commit, or a branch since deleted, is no branch to switch to.
            if (!localNames.Contains(name) ||
                name == defaultBranch?.Name)
            {
                continue;
            }

            recent.Add(branches.First(_ => !_.Remote && _.Name == name));
        }

        var other = branches
            .Where(_ => _ != defaultBranch &&
                        !recent.Contains(_))
            .ToList();
        return new(current, defaultBranch, recent, other);
    }

    static DateTimeOffset? Committed(string seconds)
    {
        if (long.TryParse(seconds, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    // Origin's default branch as clone recorded it: the local branch of that name, or origin's
    // where there is none yet. Recorded nowhere, a repository made by init say, main or master,
    // whichever there is.
    static Branch? DefaultOf(List<Branch> branches, HashSet<string> localNames, string? recordedDefault)
    {
        if (recordedDefault != null)
        {
            if (localNames.Contains(recordedDefault))
            {
                return branches.First(_ => !_.Remote && _.Name == recordedDefault);
            }

            var remote = $"origin/{recordedDefault}";
            return branches.FirstOrDefault(_ => _.Remote && _.Name == remote);
        }

        foreach (var name in (string[]) ["main", "master"])
        {
            if (localNames.Contains(name))
            {
                return branches.First(_ => !_.Remote && _.Name == name);
            }
        }

        return null;
    }

    // The branches HEAD was switched to, newest first, each once, from the reflog's subjects:
    // "checkout: moving from main to feature", which git switch writes too.
    static IEnumerable<string> SwitchedTo(string reflog)
    {
        const string prefix = "checkout: moving from ";
        const string to = " to ";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in reflog.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var index = line.LastIndexOf(to, StringComparison.Ordinal);
            if (index < prefix.Length)
            {
                continue;
            }

            var name = line[(index + to.Length)..];
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }

    // The branches with changes left on them in a stash, from the stashes' subjects: git starts
    // each with the branch it was made on, "On main: Lode:main".
    static HashSet<string> StashedBranches(string stashes)
    {
        var branches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in stashes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MarkedBranch(line) is { } branch)
            {
                branches.Add(branch);
            }
        }

        return branches;
    }

    static string? MarkedBranch(string subject)
    {
        const string separator = ": " + stashMarker;
        var index = subject.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        return subject[(index + separator.Length)..];
    }

    /// <summary>
    /// Switches to <paramref name="target"/>. With <paramref name="leave"/>, what is uncommitted
    /// on <paramref name="current"/>, untracked files included, is stashed first, to be put back
    /// on coming back to it; should the switch then fail, it is put back at once. Otherwise it
    /// comes along, unless git would have to overwrite it, when git refuses and nothing changes.
    /// A remote branch gets a local branch of its name, tracking it. Arriving on a branch with
    /// changes left on it, they are put back, if nothing else is uncommitted there.
    /// </summary>
    public static CodeCommands.RepoResult Switch(string repo, string? current, Branch target, bool leave, Cancel cancel)
    {
        if (!leave ||
            current == null)
        {
            return Arrive(repo, LocalName(target), SwitchTo(repo, target, cancel), cancel);
        }

        // Stash says it saved nothing, and succeeds, when there was nothing to save after all:
        // then there is nothing to put back either, and popping would take some other stash.
        var before = StashTip(repo);
        var stash = CodeCommands.GitToItsEnd(
            repo,
            cancel,
            "stash",
            "push",
            "--include-untracked",
            "-m",
            stashMarker + current);
        var stashed = StashTip(repo) != before;
        if (!stash.Succeeded)
        {
            var failed = stash with
            {
                Output = $"Couldn't stash the changes, so not switched:\n{stash.Output}"
            };

            // Git keeps the stash before it clears what it stashed from the folder, and has
            // kept it though the clearing then failed, on a file another program has open say.
            if (stashed)
            {
                return PutBack(repo, failed);
            }

            return failed;
        }

        var switched = SwitchTo(repo, target, cancel);
        if (switched.Succeeded)
        {
            return Arrive(repo, LocalName(target), switched, cancel);
        }

        if (!stashed)
        {
            return switched;
        }

        return PutBack(repo, switched);
    }

    // Puts back what was stashed for a switch that then wasn't made, saying so, or that it is
    // still stashed. Even cancelled: the changes would otherwise be left hidden in a stash.
    static CodeCommands.RepoResult PutBack(string repo, CodeCommands.RepoResult failed)
    {
        var restored = CodeCommands.Git(repo, Cancel.None, network: false, "stash", "pop");
        if (restored.Succeeded)
        {
            return failed with
            {
                Output = $"{failed.Output}\nThe changes were put back."
            };
        }

        return failed with
        {
            Output = $"{failed.Output}\nThe changes couldn't be put back, so are still stashed:\n{restored.Output}"
        };
    }

    // The commit the newest stash is, or null with none.
    static string? StashTip(string repo)
    {
        var tip = CodeCommands.Git(repo, "rev-parse", "--verify", "--quiet", "refs/stash");
        if (tip.Succeeded)
        {
            return tip.Output;
        }

        return null;
    }

    static CodeCommands.RepoResult SwitchTo(string repo, Branch target, Cancel cancel)
    {
        if (target.Remote)
        {
            return CodeCommands.GitToItsEnd(repo, cancel, "switch", "--track", target.Name);
        }

        return CodeCommands.GitToItsEnd(repo, cancel, "switch", target.Name);
    }

    // The local branch switching to a branch leaves checked out: origin/feature's is feature.
    internal static string LocalName(Branch branch)
    {
        if (!branch.Remote)
        {
            return branch.Name;
        }

        var slash = branch.Name.IndexOf('/');
        return branch.Name[(slash + 1)..];
    }

    // Puts back the changes left on the branch arrived on, the newest stash of them, when
    // nothing else is uncommitted there, which they could clash with. Not put back, they are
    // still stashed, and it says so.
    static CodeCommands.RepoResult Arrive(string repo, string branch, CodeCommands.RepoResult switched, Cancel cancel)
    {
        if (!switched.Succeeded)
        {
            return switched;
        }

        var stashes = CodeCommands.Git(repo, cancel, network: false, "stash", "list", "--format=%gd%x00%gs");
        if (!stashes.Succeeded)
        {
            return switched;
        }

        string? stash = null;
        foreach (var line in stashes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length == 2 &&
                MarkedBranch(fields[1]) == branch)
            {
                stash = fields[0];
                break;
            }
        }

        if (stash == null)
        {
            return switched;
        }

        var status = CodeCommands.Git(repo, cancel, network: false, "status", "--porcelain");
        if (!status.Succeeded ||
            status.Output.Length > 0)
        {
            return switched with
            {
                Output = $"{switched.Output}\nThe changes left on {branch} are still stashed, in {stash}, as there are changes here already."
            };
        }

        var popped = CodeCommands.GitToItsEnd(repo, cancel, "stash", "pop", stash);
        if (popped.Succeeded)
        {
            return switched with
            {
                Output = $"{switched.Output}\nPut back the changes left on {branch}.\n{popped.Output}".Trim()
            };
        }

        var kept = KeepAside(repo, stash, branch);
        return popped with
        {
            Output = $"{switched.Output}\nSwitched to {branch}, but the changes left on it couldn't all be put back, so they are still stashed, in {kept}:\n{popped.Output}"
        };
    }

    // A stash that clashed with what the branch has now is put back as far as git could, the
    // clashes marked in the files, and git keeps it. Still marked as the branch's, it would be
    // put back again on the next coming back here, once the clashes were settled and committed,
    // to clash all over again, and every time after. So it is kept under a name nothing puts
    // back: the same stash, stored again. One git refused outright, the folder as it was, keeps
    // its mark, to be put back another time. Gives where it is kept, as git names it.
    static string KeepAside(string repo, string stash, string branch)
    {
        var status = CodeCommands.Git(repo, "status", "--porcelain");
        if (status is not
            {
                Succeeded: true,
                Output.Length: > 0
            })
        {
            return stash;
        }

        var commit = CodeCommands.Git(repo, "rev-parse", "--verify", "--quiet", stash);
        if (!commit.Succeeded ||
            !CodeCommands.Git(repo, "stash", "drop", stash).Succeeded)
        {
            return stash;
        }

        var stored = CodeCommands.Git(repo, "stash", "store", "-m", $"On {branch}: {clashedMarker}{branch}", commit.Output);
        if (stored.Succeeded)
        {
            return "stash@{0}";
        }

        // No longer among the stashes, but still in the repository, by its commit.
        return $"the commit {commit.Output}";
    }

    /// <summary>
    /// Makes a branch called <paramref name="name"/> at the commit checked out, and switches to
    /// it. What is uncommitted comes along: it is the same commit.
    /// </summary>
    public static CodeCommands.RepoResult Create(string repo, string name, Cancel cancel) =>
        CodeCommands.GitToItsEnd(repo, cancel, "switch", "-c", name);

    /// <summary>
    /// Renames the local branch <paramref name="name"/> to <paramref name="newName"/>. The branch
    /// it tracks on origin keeps its name.
    /// </summary>
    public static CodeCommands.RepoResult Rename(string repo, string name, string newName, Cancel cancel)
    {
        // Only the case changing is the same name to a file system that ignores case, Windows',
        // so git would say a branch of that name exists already, unless forced.
        var move = "-m";
        if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
        {
            move = "-M";
        }

        return CodeCommands.GitToItsEnd(repo, cancel, "branch", move, name, newName);
    }

    /// <summary>
    /// Deletes <paramref name="branch"/>, as GitHub Desktop does: a local one whether merged or
    /// not, saying the commit it was at, so it can be got back, and, with
    /// <paramref name="alsoOnRemote"/>, the branch it tracks too; one only on a remote, from the
    /// remote. The branch checked out is switched off first, to <paramref name="switchTo"/>.
    /// </summary>
    public static CodeCommands.RepoResult Delete(string repo, Branch branch, bool alsoOnRemote, Branch? switchTo, Cancel cancel)
    {
        if (branch.Remote)
        {
            return DeleteOnRemote(repo, branch.Name, cancel);
        }

        var switched = "";
        if (switchTo != null)
        {
            var result = SwitchTo(repo, switchTo, cancel);
            if (!result.Succeeded)
            {
                return result with
                {
                    Output = $"Couldn't switch off {branch.Name}, so it wasn't deleted:\n{result.Output}"
                };
            }

            switched = result.Output;
        }

        var deleted = CodeCommands.GitToItsEnd(repo, cancel, "branch", "-D", branch.Name);
        if (!deleted.Succeeded ||
            !alsoOnRemote ||
            branch.Upstream == null)
        {
            return deleted with
            {
                Output = $"{switched}\n{deleted.Output}".Trim()
            };
        }

        var remote = DeleteOnRemote(repo, branch.Upstream, cancel);
        return remote with
        {
            Output = $"{switched}\n{deleted.Output}\n{remote.Output}".Trim()
        };
    }

    // origin/feature: feature, deleted from origin.
    static CodeCommands.RepoResult DeleteOnRemote(string repo, string remoteBranch, Cancel cancel)
    {
        var slash = remoteBranch.IndexOf('/');
        return CodeCommands.Git(
            repo,
            cancel,
            network: true,
            "push",
            remoteBranch[..slash],
            "--delete",
            remoteBranch[(slash + 1)..]);
    }

    /// <summary>
    /// Checks <paramref name="branch"/> out in a new worktree at <paramref name="folder"/>: a
    /// folder of its own, sharing the repository, so the branch can be worked on beside the one
    /// checked out here. A remote branch gets a local branch of its name, tracking it.
    /// </summary>
    public static CodeCommands.RepoResult AddWorktree(string repo, Branch branch, string folder, Cancel cancel)
    {
        if (branch.Remote)
        {
            return CodeCommands.GitToItsEnd(repo, cancel, "worktree", "add", "-b", LocalName(branch), "--", folder, branch.Name);
        }

        return CodeCommands.GitToItsEnd(repo, cancel, "worktree", "add", "--", folder, branch.Name);
    }

    /// <summary>
    /// Where a worktree of <paramref name="branch"/> goes unless told otherwise, as GitHub
    /// Desktop names it: beside the repository, its name and the branch's, Lode-feature say.
    /// </summary>
    internal static string WorktreeFolder(string repo, Branch branch)
    {
        var name = $"{Path.GetFileName(repo)}-{LocalName(branch).Replace('/', '-')}";
        return Path.Combine(Path.GetDirectoryName(repo) ?? repo, name);
    }

    /// <summary>
    /// How long ago <paramref name="when"/> was, as GitHub Desktop says it: "15 hours".
    /// </summary>
    internal static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Count((int) span.TotalMinutes, "minute");
        }

        if (span < TimeSpan.FromDays(1))
        {
            return Count((int) span.TotalHours, "hour");
        }

        var days = (int) span.TotalDays;
        if (days < 30)
        {
            return Count(days, "day");
        }

        if (days < 365)
        {
            return Count(days / 30, "month");
        }

        return Count(days / 365, "year");

        static string Count(int count, string unit)
        {
            if (count == 1)
            {
                return $"1 {unit}";
            }

            return $"{count} {unit}s";
        }
    }
}
