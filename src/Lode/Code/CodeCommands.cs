/// <summary>
/// Git and NuGet commands for the repositories in a folder under the code folder. Each git
/// command runs git itself, in each repository it finds in turn, so it does what the git the
/// user has installed does, with their config, credentials and SSH keys.
/// </summary>
static class CodeCommands
{
    public sealed record RepoResult(string Repo, bool Succeeded, string Output);

    /// <summary>
    /// The repositories a command acts on: the one the folder is in, at its top or anywhere
    /// below, otherwise every repository up to two levels below it.
    /// Only a repository up to <paramref name="codeFolder"/> counts as one the folder is in:
    /// one above it, such as a home folder kept in git, is not one of the code folder's.
    /// Without a code folder, only the folder itself.
    /// </summary>
    public static List<string> FindRepos(string folder, string? codeFolder = null)
    {
        if (RepoContaining(folder, codeFolder ?? folder) is { } repo)
        {
            return [repo];
        }

        var repos = new List<string>();
        Collect(folder, 2, repos);
        repos.Sort(StringComparer.OrdinalIgnoreCase);
        return repos;
    }

    /// <summary>
    /// The repositories of every one of <paramref name="folders"/>, each once.
    /// </summary>
    public static List<string> FindRepos(IReadOnlyList<string> folders, string? codeFolder = null)
    {
        if (folders is [var folder])
        {
            return FindRepos(folder, codeFolder);
        }

        var repos = folders
            .SelectMany(_ => FindRepos(_, codeFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        repos.Sort(StringComparer.OrdinalIgnoreCase);
        return repos;
    }

    static void Collect(string folder, int depth, List<string> repos)
    {
        if (depth == 0)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception)
        {
            // An unreadable folder holds no repositories we can use.
            return;
        }

        foreach (var child in children)
        {
            if (IsRepo(child))
            {
                repos.Add(child);
                continue;
            }

            Collect(child, depth - 1, repos);
        }
    }

    /// <summary>
    /// The repository <paramref name="folder"/> is in: itself, or the nearest folder above it,
    /// no further up than <paramref name="top"/>, that is one. Null when there is none.
    /// </summary>
    internal static string? RepoContaining(string folder, string top)
    {
        for (var current = folder; current != null; current = Path.GetDirectoryName(current))
        {
            if (IsRepo(current))
            {
                return current;
            }

            if (Locations.Same(current, top))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the repository is on origin's default branch, main say, as clone recorded it.
    /// Read from the files git keeps, as git would take too long for a menu opening. False when
    /// that can't be told: detached, no origin, or a repository made by init and given an origin
    /// later, which has no default branch recorded.
    /// </summary>
    internal static bool OnDefaultBranch(string repo)
    {
        const string local = "ref: refs/heads/";
        try
        {
            if (GitDirectory(repo) is not { } git ||
                RecordedDefaultName(repo) is not { } defaultBranch)
            {
                return false;
            }

            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            return head.StartsWith(local, StringComparison.Ordinal) &&
                   head[local.Length..] == defaultBranch;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The branch origin's HEAD names, main say, read from the file clone recorded it in, without
    /// running git, for the menus to name it. Null where that can't be told: no origin, or a
    /// repository made by init and given an origin later, which has none recorded.
    /// </summary>
    internal static string? RecordedDefaultName(string repo)
    {
        const string remote = "ref: " + originHead;
        try
        {
            if (GitDirectory(repo) is not { } git)
            {
                return null;
            }

            var origin = File.ReadAllText(Path.Combine(CommonDirectory(git), "refs", "remotes", "origin", "HEAD")).Trim();
            if (!origin.StartsWith(remote, StringComparison.Ordinal))
            {
                return null;
            }

            return origin[remote.Length..];
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            return null;
        }
    }

    // The .git folder, or, for a worktree or submodule, the folder its .git file names.
    internal static string? GitDirectory(string repo)
    {
        var git = Path.Combine(repo, ".git");
        if (Directory.Exists(git))
        {
            return git;
        }

        const string prefix = "gitdir: ";
        var text = File.ReadAllText(git).Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(repo, text[prefix.Length..]));
    }

    // Where a worktree's branches and config are: the repository it was added to, as its
    // commondir says.
    internal static string CommonDirectory(string git)
    {
        var common = Path.Combine(git, "commondir");
        if (!File.Exists(common))
        {
            return git;
        }

        return Path.GetFullPath(Path.Combine(git, File.ReadAllText(common).Trim()));
    }

    // A .git folder, or a .git file as a worktree or submodule has.
    internal static bool IsRepo(string folder)
    {
        var git = Path.Combine(folder, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>
    /// Brings in what the branch's own on origin has, as git pull does, merging or rebasing as
    /// the user's git config says. One that would conflict is undone, as Sync fork's merge is, so
    /// that none is left part way through a merge or a rebase; nor is one pulled into that is part
    /// way through one already.
    /// </summary>
    public static Task<List<RepoResult>> Pull(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => Pull(repo, cancel, "nothing was pulled"));

    // A pull git refused before it began, as where it would have to overwrite an uncommitted
    // change, changed nothing, and is left as git said it; one that stopped part way, at a
    // conflict, is undone. Run on a folder of repositories, one left part way would be found only
    // later, its Push refused till it was settled. Part way through a merge or a rebase of the
    // user's own already, which git would refuse to pull into, nothing is pulled: undoing the pull
    // would undo that too, its conflicts settled perhaps, waiting to be committed.
    static RepoResult Pull(string repo, Cancel cancel, string notDone)
    {
        if (Unfinished(repo) is { } before)
        {
            return new(repo, false, PartWay(before, notDone));
        }

        var pull = Git(repo, cancel, network: true, "pull");
        if (pull.Succeeded ||
            Unfinished(repo) is not { } unfinished)
        {
            return pull;
        }

        var undone = Undo(repo, unfinished, Pulling, notDone, "pull it yourself");
        return pull with
        {
            Output = $"{pull.Output}\n{undone}".Trim()
        };

        string Pulling()
        {
            if (TrackedBranch(repo) is { } tracked)
            {
                return $"Pulling {tracked}";
            }

            return "Pulling";
        }
    }

    /// <summary>
    /// Learns what origin has now, its branches and their commits, changing nothing checked
    /// out: how far ahead and behind each branch is follows. In a fork, what upstream has too,
    /// as GitHub Desktop fetches it, so how far behind upstream the branch is follows as well.
    /// </summary>
    public static Task<List<RepoResult>> Fetch(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => Git(repo, cancel, network: true, FetchArguments(repo)));

    static string[] FetchArguments(string repo)
    {
        if (IsFork(repo))
        {
            return ["fetch", "--multiple", "origin", "upstream"];
        }

        return ["fetch"];
    }

    /// <summary>
    /// Whether the repository is a fork: one with an upstream remote, the repository it was made
    /// from, as GitHub Desktop sets one up. Read from its config, as git would take too long for
    /// a menu opening.
    /// </summary>
    internal static bool IsFork(string repo) =>
        RemoteSite.UpstreamUrl(repo) != null;

    /// <summary>
    /// Brings each fork up to date with the repository it was made from, as GitHub Desktop's
    /// Fetch origin, Update from upstream and Push origin do, one after another: learns what
    /// origin and upstream have, brings in what origin has that the branch hasn't, merges
    /// upstream's default branch into the branch checked out, then pushes the branch to origin,
    /// one tracking none to one of its own name there, which it tracks from then on, as Push does.
    /// A branch that has diverged from origin is left as it is, as is one whose merge would
    /// conflict: the merge is undone, so none is left part way through one. Nor is one merged
    /// into that is part way through a merge or a rebase already.
    /// </summary>
    public static Task<List<RepoResult>> SyncFork(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => SyncFork(repo, cancel));

    static RepoResult SyncFork(string repo, Cancel cancel)
    {
        if (WhyNotMerge(repo, "nothing was merged or pushed") is { } why)
        {
            return new(repo, false, why);
        }

        // What git said at each step, as the result's output says it all.
        var said = new List<string>();
        var fetch = Said(Git(repo, cancel, network: true, "fetch", "--multiple", "origin", "upstream"));
        if (!fetch.Succeeded)
        {
            return fetch;
        }

        // Recorded where it had to be asked of upstream, as one added by hand has none, so the
        // state can count how far behind it the branch is from now on.
        var parent = RecordedDefaultBranch(repo, cancel, "upstream");
        if (parent == null)
        {
            var asked = DefaultBranch(repo, cancel, "upstream");
            if (!asked.Succeeded)
            {
                return Said(asked);
            }

            parent = asked.Output;
            Git(repo, "remote", "set-head", "upstream", parent);
        }

        if (CatchUpWithOrigin(repo, cancel) is { } caughtUp)
        {
            var result = Said(caughtUp);
            if (!result.Succeeded)
            {
                return result;
            }
        }

        var from = $"upstream/{parent}";
        var merge = Said(GitToItsEnd(repo, cancel, "merge", "--no-edit", from));
        if (!merge.Succeeded)
        {
            return Said(UndoMerge(repo, from, merge));
        }

        return Said(PushToOrigin(repo, cancel));

        RepoResult Said(RepoResult result)
        {
            said.Add(result.Output);
            return result with
            {
                Output = string.Join('\n', said).Trim()
            };
        }
    }

    // Brings in what the branch's own on origin has that it hasn't, as a fork synced on its
    // site, or pushed to from another machine, has: the push after would be refused otherwise.
    // Only a fast forward. Null with nothing to bring in, or nothing tracked. One that has
    // diverged fails, changing nothing, as there is no telling how it is to be settled.
    static RepoResult? CatchUpWithOrigin(string repo, Cancel cancel)
    {
        if (TrackedBranch(repo) is not { } tracked)
        {
            return null;
        }

        // "ahead<tab>behind".
        var counts = Git(repo, "rev-list", "--left-right", "--count", "HEAD...@{upstream}");
        var parts = counts.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (!counts.Succeeded ||
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var ahead) ||
            !int.TryParse(parts[1], out var behind) ||
            behind == 0)
        {
            return null;
        }

        if (ahead > 0)
        {
            return new(
                repo,
                false,
                $"The branch has diverged from {tracked}: {GitState.CountOf(ahead, "commit")} here aren't there, and {GitState.CountOf(behind, "commit")} there aren't here. Pull, then sync again. Nothing was merged or pushed.");
        }

        return GitToItsEnd(repo, cancel, "merge", "--ff-only", "@{upstream}");
    }

    // The branch the one checked out tracks, origin/main say, or null where it tracks none, or
    // none is checked out, as part way through a rebase.
    static string? TrackedBranch(string repo)
    {
        var tracked = Git(repo, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
        if (tracked is
            {
                Succeeded: true,
                Output.Length: > 0
            })
        {
            return tracked.Output;
        }

        return null;
    }

    // A merge that failed: refused before it began, uncommitted changes in the way say, which
    // changed nothing, or stopped part way, by a conflict, which is undone, the branch and its
    // files as they were before it. Without <paramref name="pushing"/>, a merge that was never to
    // be pushed, it says nothing of pushing, and what git said of one refused.
    internal static RepoResult UndoMerge(string repo, string from, RepoResult merge, bool pushing = true)
    {
        if (!Git(repo, "rev-parse", "--quiet", "--verify", "MERGE_HEAD").Succeeded)
        {
            if (!pushing)
            {
                return merge with
                {
                    Output = $"Couldn't merge {from}, so nothing was merged:\n{merge.Output}".Trim()
                };
            }

            return merge with
            {
                Output = $"Couldn't merge {from}, so nothing was merged or pushed."
            };
        }

        string? notPushed = null;
        if (pushing)
        {
            notPushed = "nothing was pushed";
        }

        return merge with
        {
            Succeeded = false,
            Output = Undo(repo, "merge", () => $"Merging {from}", notPushed, "merge it yourself")
        };
    }

    // A merge or a pull that stopped part way, at a conflict say, undone, the branch and its
    // files as they were before it. What it says starts with what was being done, asked once it
    // is undone, as part way through a rebase no branch is checked out to say what it tracks,
    // and what that leaves not done, where it leaves anything, and ends with the files that would
    // conflict.
    static string Undo(string repo, string unfinished, Func<string> doing, string? notDone, string yourself)
    {
        var conflicted = ConflictedFiles(repo);
        var abort = Git(repo, unfinished, "--abort");
        var undone = $"The {unfinished} was undone: {yourself}, settling the conflicts.";
        if (!abort.Succeeded)
        {
            undone = $"The {unfinished} couldn't be undone: settle the conflicts, or git {unfinished} --abort.\n{abort.Output}";
        }

        var action = doing();
        var so = "";
        if (notDone != null)
        {
            so = $", so {notDone}";
        }

        // Stopped other than by a conflict, as by a commit git couldn't make, there are no files
        // to name.
        if (conflicted.Length == 0)
        {
            return $"{action} stopped part way{so}. {undone}";
        }

        return $"{action} would conflict{so}. {undone}\nIn conflict:\n{conflicted}";
    }

    /// <summary>
    /// Pushes the commits the branch has that the one it tracks hasn't, committing nothing.
    /// </summary>
    public static Task<List<RepoResult>> PushCommits(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => Git(repo, cancel, network: true, "push"));

    /// <summary>
    /// Pulls, then pushes: a branch that has diverged from the one it tracks, which origin would
    /// refuse a push to until it has origin's commits too. A pull that fails pushes nothing, and
    /// one that would conflict is undone, as Pull's is.
    /// </summary>
    public static Task<List<RepoResult>> PullThenPush(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var pull = Pull(repo, cancel, "nothing was pulled or pushed");
                if (!pull.Succeeded)
                {
                    return pull;
                }

                var push = Git(repo, cancel, network: true, "push");
                return push with
                {
                    Output = $"{pull.Output}\n{push.Output}".Trim()
                };
            });

    /// <summary>
    /// Switches to origin's default branch, main say. A repository with anything uncommitted,
    /// staged or not, or a file git doesn't track, fails, listing them, and stays on its branch:
    /// switching would take them onto the default branch, or git would refuse part way.
    /// </summary>
    public static Task<List<RepoResult>> SwitchToDefaultBranch(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var status = Git(repo, cancel, network: false, "status", "--porcelain");
                if (!status.Succeeded)
                {
                    return status;
                }

                if (status.Output.Length > 0)
                {
                    return status with
                    {
                        Succeeded = false,
                        Output = $"Uncommitted changes, so not switched:\n{status.Output}"
                    };
                }

                var branch = DefaultBranch(repo, cancel);
                if (!branch.Succeeded)
                {
                    return branch;
                }

                // Where there is no local branch of that name yet, git makes one tracking
                // origin's.
                return GitToItsEnd(repo, cancel, "switch", branch.Output);
            });

    const string originHead = "refs/remotes/origin/";

    /// <summary>
    /// The branch origin's HEAD names, main say, as clone recorded it, without asking origin.
    /// Null without an origin, or in a repository made by init then given an origin, which has
    /// none recorded. Of <paramref name="remote"/> where given, upstream say, as GitHub Desktop
    /// records it for a fork's.
    /// </summary>
    internal static string? RecordedDefaultBranch(string repo, Cancel cancel, string remote = "origin")
    {
        var head = $"refs/remotes/{remote}/";
        var recorded = Git(repo, cancel, network: false, "symbolic-ref", "--quiet", head + "HEAD");
        if (recorded.Succeeded &&
            recorded.Output.StartsWith(head, StringComparison.Ordinal))
        {
            return recorded.Output[head.Length..];
        }

        return null;
    }

    // The branch origin's HEAD names, as the output of a result that succeeded: as clone
    // recorded it, or, in a repository made by init then given an origin, which has none
    // recorded, as origin says now. Fails, saying why, without an origin, or one with no HEAD.
    // Of another remote where given, upstream say.
    internal static RepoResult DefaultBranch(string repo, Cancel cancel, string name = "origin")
    {
        if (RecordedDefaultBranch(repo, cancel, name) is { } recorded)
        {
            return new(repo, true, recorded);
        }

        var remote = Git(repo, cancel, network: true, "ls-remote", "--symref", name, "HEAD");
        if (!remote.Succeeded)
        {
            return remote with
            {
                Output = $"Couldn't ask {name} for its default branch:\n{remote.Output}"
            };
        }

        // "ref: refs/heads/main<tab>HEAD", then the commit it is at.
        const string prefix = "ref: refs/heads/";
        const string suffix = "\tHEAD";
        foreach (var line in remote.Output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                line.EndsWith(suffix, StringComparison.Ordinal))
            {
                return remote with
                {
                    Output = line[prefix.Length..^suffix.Length]
                };
            }
        }

        return remote with
        {
            Succeeded = false,
            Output = $"No default branch: {name}'s HEAD names no branch."
        };
    }

    /// <summary>
    /// Tags <paramref name="commit"/> with <paramref name="name"/>, as History tags the commit
    /// picked, then pushes the tag to origin. A commit on the branch checked out that origin's
    /// hasn't got goes with the tag, with those before it, as the commit checked out goes with
    /// its tag, so the commit tagged is on origin's branch, not only under the tag. One on
    /// origin's already, or on another branch, or detached, has the tag go alone. One that has a
    /// tag of that name already fails, and keeps the tag it has.
    /// </summary>
    public static Task<List<RepoResult>> Tag(IReadOnlyList<string> repos, string name, string commit, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => TagAndPush(repo, name, commit, null, cancel));

    /// <summary>
    /// Tags the commit each repository has checked out with <paramref name="name"/>, and pushes
    /// the tag to origin. With <paramref name="commitMessage"/>, what is uncommitted is committed
    /// first, as Push commits it, so it is in the tag. The branch's commits origin hasn't got go
    /// with the tag, in the one push, so the commit tagged is on origin's branch, not only under
    /// the tag: both go or neither does. A branch origin hasn't got at all goes too, tracking
    /// origin's from then on. Detached, the tag goes alone. A branch that has diverged from
    /// origin's, as of the last fetch, is left untagged, to be pulled first, and a push that
    /// fails takes the tag off again, so tagging once it is put right finds no tag in the way.
    /// One that has a tag of that name already fails, and keeps the tag it has.
    /// </summary>
    public static Task<List<RepoResult>> Tag(IReadOnlyList<string> repos, string name, IProgress<string>? progress = null, Cancel cancel = default, string? commitMessage = null) =>
        ForEach(repos, progress, cancel, repo => TagAndPush(repo, name, "HEAD", commitMessage, cancel));

    // Tags commit, HEAD or the SHA History picked, and pushes the tag, with what of the branch
    // checked out origin's hasn't got, up to the commit: see BranchToPushWithTag.
    static RepoResult TagAndPush(string repo, string name, string commit, string? commitMessage, Cancel cancel)
    {
        var committed = "";
        if (commitMessage != null &&
            HasChanges(repo))
        {
            var commitResult = CommitAll(repo, commitMessage, "nothing was committed, tagged or pushed", cancel);
            if (!commitResult.Succeeded)
            {
                return commitResult;
            }

            committed = commitResult.Output;
        }

        var (branchRef, newBranch, failed, branch, _) = BranchToPushWithTag(repo, commit);
        if (failed != null)
        {
            return failed with
            {
                Output = $"{committed}\n{failed.Output}".Trim()
            };
        }

        var tag = GitToItsEnd(repo, cancel, "tag", name, commit);
        if (!tag.Succeeded)
        {
            return tag with
            {
                Output = $"{committed}\n{tag.Output}".Trim()
            };
        }

        List<string> arguments = ["push", "origin", $"refs/tags/{name}"];
        if (branchRef != null)
        {
            arguments = ["push", "--atomic", "origin", branchRef, $"refs/tags/{name}"];
            if (newBranch)
            {
                arguments.Insert(1, "--set-upstream");
            }
        }

        var push = Git(repo, cancel, network: true, [.. arguments]);
        if (!push.Succeeded)
        {
            // Not cancellable: a tag left behind would be in the way of tagging again.
            GitToItsEnd(repo, Cancel.None, "tag", "-d", name);
            return push with
            {
                Output = $"{committed}\n{push.Output}\nThe tag was taken off again, as it didn't reach origin.".Trim()
            };
        }

        // --set-upstream tracks only a branch pushed by name, HEAD among them, not a SHA. Not
        // cancellable: the branch is on origin now.
        if (newBranch &&
            commit != "HEAD")
        {
            GitToItsEnd(repo, Cancel.None, "config", $"branch.{branch}.remote", "origin");
            GitToItsEnd(repo, Cancel.None, "config", $"branch.{branch}.merge", $"refs/heads/{branch}");
        }

        return push with
        {
            Output = $"{committed}\nTagged {name}\n{push.Output}".Trim()
        };
    }

    /// <summary>
    /// What to push with a tag of <paramref name="commit"/>, so origin's branch has the commit
    /// too: the commit to the branch on origin the branch checked out tracks, where origin's
    /// hasn't got it, with <paramref name="Ahead"/> commits, it among them, that origin's hasn't
    /// got; or to a branch of the same name, for one tracking none, which is
    /// <paramref name="NewBranch"/>. Nothing, where origin's has the commit already, or detached,
    /// or tracking another remote, or for a commit not on the branch checked out, as History
    /// shows another branch's while comparing. Failed where it has diverged from origin's, which
    /// would refuse the push. <paramref name="Branch"/> is the branch checked out, where one is.
    /// </summary>
    internal readonly record struct WithTag(string? BranchRef, bool NewBranch, RepoResult? Failed, string? Branch = null, int Ahead = 0);

    internal static WithTag BranchToPushWithTag(string repo, string commit = "HEAD")
    {
        var symbolic = Git(repo, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!symbolic.Succeeded)
        {
            return default;
        }

        var branch = symbolic.Output.Trim();
        if (commit != "HEAD" &&
            !Git(repo, "merge-base", "--is-ancestor", commit, "HEAD").Succeeded)
        {
            return new(null, false, null, branch);
        }

        var remote = Git(repo, "config", "--get", $"branch.{branch}.remote");
        var merge = Git(repo, "config", "--get", $"branch.{branch}.merge");
        if (!remote.Succeeded ||
            !merge.Succeeded)
        {
            return new($"{commit}:refs/heads/{branch}", true, null, branch);
        }

        if (remote.Output.Trim() != "origin")
        {
            return new(null, false, null, branch);
        }

        // "ahead<tab>behind", as of the last fetch.
        var counts = Git(repo, "rev-list", "--left-right", "--count", $"{commit}...@{{upstream}}");
        var parts = counts.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (!counts.Succeeded ||
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var ahead) ||
            !int.TryParse(parts[1], out var behind))
        {
            // Origin's branch not fetched yet, or gone: pushed to, it is made again.
            return new($"{commit}:{merge.Output.Trim()}", false, null, branch);
        }

        if (ahead == 0)
        {
            return new(null, false, null, branch);
        }

        if (behind > 0)
        {
            return new(null, false, new(repo, false, $"{branch} and origin's have each got commits the other hasn't, so nothing was tagged or pushed. Pull first."), branch, ahead);
        }

        return new($"{commit}:{merge.Output.Trim()}", false, null, branch, ahead);
    }

    /// <summary>
    /// Whether any of the repositories has anything to commit, for Push to ask for a message
    /// only then.
    /// </summary>
    public static bool AnyChanges(IReadOnlyList<string> repos) =>
        repos.Any(HasChanges);

    // Anything to commit: a change, staged or not, or a file git doesn't track nor ignores, as
    // Push adds them.
    static bool HasChanges(string repo)
    {
        var status = Git(repo, "status", "--porcelain", "--ignore-submodules=dirty");
        return status is
        {
            Succeeded: true,
            Output.Length: > 0
        };
    }

    /// <summary>
    /// History's Revert commit: a new commit, on the branch checked out, undoing what
    /// <paramref name="commit"/> changed, with git's message for it. A merge is undone against
    /// its first parent, the branch it was merged into. The commit is not pushed. One that
    /// conflicts with what has changed since is given up, so the repository is left as it was,
    /// not part way through a revert.
    /// </summary>
    public static Task<List<RepoResult>> Revert(string repo, GitCommit commit, Cancel cancel = default) =>
        ForEach(
            [repo],
            null,
            cancel,
            _ =>
            {
                List<string> arguments = ["revert", "--no-edit"];
                if (commit.Parents.Count > 1)
                {
                    arguments.AddRange(["-m", "1"]);
                }

                arguments.Add(commit.Sha);
                var revert = GitToItsEnd(_, cancel, arguments.ToArray());
                if (revert.Succeeded)
                {
                    return revert;
                }

                // Not cancellable: a revert left part way would be in the way of everything after.
                if (Git(_, "rev-parse", "--quiet", "--verify", "REVERT_HEAD").Succeeded)
                {
                    GitToItsEnd(_, Cancel.None, "revert", "--abort");
                    return revert with
                    {
                        Output = $"{revert.Output}\nThe revert conflicts with what has changed since, so it was given up, and nothing was committed.".Trim()
                    };
                }

                return revert;
            });

    /// <summary>
    /// History's Undo commit, as GitHub Desktop's: <paramref name="commit"/>, the last on the
    /// branch, taken off it, its changes left uncommitted and unstaged, as git reset --mixed
    /// leaves them, and the files as they were, so what was uncommitted already stays. The first
    /// commit leaves the branch with none, its files staged. Nothing is done where the commit
    /// isn't the last any more, or has been pushed, or part way through a merge, which a reset
    /// would give up.
    /// </summary>
    public static Task<List<RepoResult>> UndoCommit(string repo, GitCommit commit, Cancel cancel = default) =>
        ForEach(
            [repo],
            null,
            cancel,
            _ =>
            {
                if (GitChanges.IsMerging(_))
                {
                    return new(_, false, "Nothing was undone: a merge, cherry pick or revert is part way. Commit it or give it up first.");
                }

                if (GitHistory.WhyNotRewrite(_, commit.Sha, cancel) is { } why)
                {
                    return new(_, false, $"Nothing was undone: {why}.");
                }

                // The branch moved, only if it is still at the commit, so a commit made since
                // isn't taken off with it.
                if (commit.Parents.Count == 0)
                {
                    return GitToItsEnd(_, cancel, "update-ref", "-m", "undo commit", "-d", "HEAD", commit.Sha);
                }

                var parent = commit.Parents[0];
                var moved = GitToItsEnd(_, cancel, "update-ref", "-m", $"reset: moving to {parent}", "HEAD", parent, commit.Sha);
                if (!moved.Succeeded)
                {
                    return moved;
                }

                // Not cancellable: with the branch moved, the index has to follow it, or the
                // commit's changes would show staged.
                return GitToItsEnd(_, Cancel.None, "reset", "-q");
            });

    /// <summary>
    /// Deletes the tag <paramref name="name"/> from each repository and from origin. Gone from
    /// one of them already, or never pushed, it goes from the other; in neither, it fails.
    /// </summary>
    public static Task<List<RepoResult>> DeleteTag(IReadOnlyList<string> repos, string name, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var local = GitToItsEnd(repo, cancel, "tag", "-d", name);
                var remote = Git(repo, cancel, network: true, "push", "origin", "--delete", $"refs/tags/{name}");

                // Git speaks English here, as Git asks it to, so what it says can be read. Origin
                // without the tag refuses, as GitHub does, or deletes it all the same with a
                // warning, as origin in a folder does.
                var notLocal = !local.Succeeded && local.Output.Contains("not found", StringComparison.Ordinal);
                var notRemote = remote.Succeeded switch
                {
                    true => remote.Output.Contains("non-existent ref", StringComparison.Ordinal),
                    false => remote.Output.Contains("remote ref does not exist", StringComparison.Ordinal)
                };
                var remoteDeleted = remote.Succeeded &&
                                    !notRemote;
                var succeeded = (local.Succeeded || notLocal) &&
                                (remoteDeleted || notRemote) &&
                                (local.Succeeded || remoteDeleted);
                return remote with
                {
                    Succeeded = succeeded,
                    Output = $"{local.Output}\n{remote.Output}".Trim()
                };
            });

    /// <summary>
    /// Commits every change with the message, new files included, as the Changes pane commits
    /// them, though not what git ignores, then pushes to origin. With no message, where nothing
    /// was uncommitted as it asked, it only pushes. A branch tracking none, as one made here
    /// does, goes to one of its own name on origin, which it tracks from then on.
    /// </summary>
    public static Task<List<RepoResult>> Push(IReadOnlyList<string> repos, string? message, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                if (message == null)
                {
                    return PushToOrigin(repo, cancel);
                }

                var commit = CommitAll(repo, message, "nothing was committed or pushed", cancel);
                if (!commit.Succeeded)
                {
                    return commit;
                }

                var push = PushToOrigin(repo, cancel);
                return push with
                {
                    Output = $"{commit.Output}\n{push.Output}".Trim()
                };
            });

    // Pushes the branch checked out to origin, as git push does, to the branch it tracks there.
    // One tracking none, as a branch made here does, Lode's own among them, goes to a branch of
    // its own name on origin, which it then tracks, as GitHub Desktop publishes one: git would
    // refuse it, having nowhere set to push to, as push.default=simple has it, unless
    // push.autoSetupRemote is set. Detached, there is no branch to push, which git says.
    static RepoResult PushToOrigin(string repo, Cancel cancel)
    {
        var symbolic = Git(repo, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!symbolic.Succeeded)
        {
            return Git(repo, cancel, network: true, "push", "origin");
        }

        var branch = symbolic.Output.Trim();
        if (Git(repo, "config", "--get", $"branch.{branch}.remote").Succeeded)
        {
            return Git(repo, cancel, network: true, "push", "origin");
        }

        var push = Git(repo, cancel, network: true, "push", "--set-upstream", "origin", $"HEAD:refs/heads/{branch}");
        if (!push.Succeeded)
        {
            return push;
        }

        return push with
        {
            Output = $"{push.Output}\nPublished {branch} to origin, as origin/{branch}, which it tracks from now on.".Trim()
        };
    }

    // Commits every change, new files included, as Push and Tag commit them: what went wrong,
    // ending with what was not done, or what the commit said. Nothing to commit is fine: there
    // may still be commits to push. A repository of its own inside, git doesn't track, a clone or
    // a worktree say, is left out, and said to be: git would add it as a link to its commit, a
    // warning all it says of it, and Push send a link to a commit origin hasn't got.
    static RepoResult CommitAll(string repo, string message, string notDone, Cancel cancel)
    {
        if (WhyNotChange(repo, notDone) is { } why)
        {
            return new(repo, false, why);
        }

        var inside = UntrackedIn(repo).Repos;
        var add = GitToItsEnd(repo, cancel, AddAll(inside));
        if (!add.Succeeded)
        {
            return add;
        }

        var commit = GitToItsEnd(repo, cancel, "commit", "-m", message);
        if (!commit.Succeeded &&
            !NothingToCommit(repo))
        {
            return commit;
        }

        var leftOut = inside.Select(_ => $"Left out {_}/, a repository of its own");
        return commit with
        {
            Succeeded = true,
            Output = string.Join('\n', [.. leftOut, commit.Output]).Trim()
        };
    }

    /// <summary>
    /// Git's add of every change, new files included, but for <paramref name="leaving"/>, the
    /// repositories inside as git names them, each left out by a pathspec of its own. Each is
    /// taken as it is named, not as a pattern, whose brackets would leave out more than it, and
    /// as a pathspec even where GIT_LITERAL_PATHSPECS is set, which would otherwise have git
    /// look for a file named ":(exclude,literal)tools".
    /// </summary>
    internal static string[] AddAll(IReadOnlyList<string> leaving)
    {
        if (leaving.Count == 0)
        {
            return ["add", "--all"];
        }

        return ["--no-literal-pathspecs", "add", "--all", "--", ".", .. leaving.Select(_ => $":(exclude,literal){_}")];
    }

    // Why Push is not to commit in a repository, nor Sync fork to merge, or null where they can,
    // ending with what was not done. With files still in conflict, from a pull say, Push would
    // commit them as they are, the markers in them, as the merge settled, and send that. With no
    // branch checked out, as part way through a rebase, or on a tag, either would make a commit
    // no branch has, which there is nowhere to send, and the next switch leaves behind.
    internal static string? WhyNotChange(string repo, string notDone)
    {
        var unmerged = Git(repo, "ls-files", "--unmerged");
        if (unmerged is
            {
                Succeeded: true,
                Output.Length: > 0
            })
        {
            if (Unfinished(repo) is { } unfinished)
            {
                return $"Files in it are still in conflict, from a {unfinished} not finished, so {notDone}. {Settle(unfinished)}";
            }

            return $"Files in it are still in conflict, from a merge or a rebase not finished, so {notDone}.";
        }

        // One not a repository at all is for the commit to say.
        if (!Git(repo, "symbolic-ref", "--quiet", "HEAD").Succeeded &&
            Git(repo, "rev-parse", "--verify", "--quiet", "HEAD").Succeeded)
        {
            return $"No branch is checked out in it, so {notDone}.";
        }

        return null;
    }

    // Why Sync fork, or a merge, squash, rebase or update from the default branch, is not to
    // merge in a repository, or null where it can: as for a change, and where it is part way
    // through a merge or a rebase already, its conflicts settled perhaps, waiting to be
    // committed, which is said first, as part way through a rebase no branch is checked out. Git
    // would refuse to merge into it, and the merge then undone, as one that stopped part way is,
    // would undo the user's own with it. Push, which doesn't merge, commits such a merge, as
    // Commit merge would.
    internal static string? WhyNotMerge(string repo, string notDone)
    {
        if (Unfinished(repo) is { } unfinished)
        {
            return PartWay(unfinished, notDone);
        }

        return WhyNotChange(repo, notDone);
    }

    static string PartWay(string unfinished, string notDone) =>
        $"It is part way through a {unfinished}, so {notDone}. {Settle(unfinished)}";

    // What can be done about what it is part way through, for a refusal to end with. Changes
    // settles and commits a merge, a cherry-pick or a revert, or aborts it, but has no rebase to
    // go on with.
    static string Settle(string unfinished)
    {
        if (unfinished == "rebase")
        {
            return "Go on with it once its conflicts are settled, git rebase --continue, or give it up, git rebase --abort.";
        }

        return "Settle its conflicts in Changes and commit it, or abort it there.";
    }

    /// <summary>
    /// What git is part way through in the repository, as the command that started it is named,
    /// whose --abort undoes it: merge, rebase, cherry-pick or revert. Null where it is part way
    /// through none. Read from the files git keeps while it is, as the Changes pane reads them.
    /// </summary>
    internal static string? Unfinished(string repo)
    {
        try
        {
            if (GitDirectory(repo) is not { } git)
            {
                return null;
            }

            if (Directory.Exists(Path.Combine(git, "rebase-merge")) ||
                Directory.Exists(Path.Combine(git, "rebase-apply")))
            {
                return "rebase";
            }

            if (File.Exists(Path.Combine(git, "MERGE_HEAD")))
            {
                return "merge";
            }

            if (File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD")))
            {
                return "cherry-pick";
            }

            if (File.Exists(Path.Combine(git, "REVERT_HEAD")))
            {
                return "revert";
            }

            return null;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Whether the commit had nothing to commit: everything added, nothing staged. Asked of git,
    // rather than read from what the commit said, which it words one way for a clean folder and
    // another for one with files it doesn't track, as a repository of its own inside is, add
    // being told to leave it out. Nor do changes inside a submodule count, which add leaves too.
    static bool NothingToCommit(string repo)
    {
        var status = Git(repo, "status", "--porcelain", "--untracked-files=no", "--ignore-submodules=dirty");
        return status is
        {
            Succeeded: true,
            Output.Length: 0
        };
    }

    /// <summary>
    /// What the repositories have that git doesn't track yet, nor ignores, for Push's question to
    /// say: how many new files it adds, a folder of them counting each file in it, and the
    /// repositories of their own inside, by their full paths, which it leaves out.
    /// </summary>
    public sealed record Untracked(int NewFiles, IReadOnlyList<string> Repos);

    /// <summary>
    /// What the repositories have that git doesn't track yet, nor ignores, as
    /// <see cref="Untracked"/> has it.
    /// </summary>
    public static Untracked FindUntracked(IReadOnlyList<string> repos)
    {
        var newFiles = 0;
        var inside = new List<string>();
        foreach (var repo in repos)
        {
            var (files, repositories) = UntrackedIn(repo);
            newFiles += files;
            inside.AddRange(repositories.Select(_ => Path.Combine(repo, _.Replace('/', '\\'))));
        }

        return new(newFiles, inside);
    }

    // What git doesn't track in the repository, nor ignores: how many files, and the
    // repositories of their own inside, as git names them, relative to it. Git lists one as a
    // folder, a slash after it, without looking in it, as it lists no other folder; it is one
    // where the folder holds a .git, a repository's folder or a worktree's file.
    static (int Files, List<string> Repos) UntrackedIn(string repo)
    {
        var result = GitOutput(repo, Cancel.None, ["ls-files", "--others", "--exclude-standard", "-z"]);
        if (!result.Succeeded)
        {
            return (0, []);
        }

        var files = 0;
        var repos = new List<string>();
        foreach (var entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = entry.TrimEnd('/');
            if (name.Length < entry.Length &&
                IsRepo(Path.Combine(repo, name.Replace('/', '\\'))))
            {
                repos.Add(name);
                continue;
            }

            files++;
        }

        return (files, repos);
    }

    /// <summary>
    /// What Discard works on in <paramref name="Repo"/>: the whole of it, with no
    /// <paramref name="Folders"/>, or only those folders inside it, by their full paths.
    /// </summary>
    public sealed record Discarding(string Repo, IReadOnlyList<string> Folders);

    /// <summary>
    /// What Discard of <paramref name="folders"/> works on, each repository once. A folder below
    /// the top of a repository is that folder alone, as Purge purges it alone; a repository's own
    /// folder is the whole of it, and a folder holding repositories, the code folder say, the
    /// whole of each, found as Push finds them. A folder inside another picked, or in a
    /// repository picked whole, is discarded with that.
    /// </summary>
    public static List<Discarding> DiscardTargets(IReadOnlyList<string> folders, string? codeFolder = null)
    {
        var whole = new List<string>();
        var inside = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (RepoContaining(folder, codeFolder ?? folder) is { } repo &&
                !Locations.Same(repo, folder))
            {
                if (!inside.TryGetValue(repo, out var picked))
                {
                    picked = [];
                    inside[repo] = picked;
                }

                picked.Add(folder);
                continue;
            }

            whole.AddRange(FindRepos(folder, codeFolder));
        }

        var targets = whole
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(_ => new Discarding(_, []))
            .ToList();
        targets.AddRange(
            inside
                .Where(_ => !whole.Contains(_.Key, StringComparer.OrdinalIgnoreCase))
                .Select(_ => new Discarding(_.Key, Outermost(_.Value))));
        targets.Sort((first, second) => StringComparer.OrdinalIgnoreCase.Compare(first.Repo, second.Repo));
        return targets;
    }

    /// <summary>
    /// Moves the files to the Recycle Bin in one go, giving whether they all went.
    /// </summary>
    public delegate Task<bool> Recycler(IReadOnlyList<string> paths);

    /// <summary>
    /// Throws away every uncommitted change in what each of <paramref name="targets"/> covers,
    /// staged or not, and deletes what git doesn't track, folders included, though not what it
    /// ignores, that being Purge's, nor a repository of its own inside. The files as they are go
    /// to the Recycle Bin first, with <paramref name="recycle"/>, as the Changes pane's Discard
    /// sends them, so each can be got back: those to be put back as the last commit has them, and
    /// those to be deleted. Where they can't all go, nothing is discarded. A whole repository with
    /// nothing committed yet goes back to empty.
    /// </summary>
    public static async Task<List<RepoResult>> Discard(IReadOnlyList<Discarding> targets, Recycler recycle, IProgress<string>? progress = null, Cancel cancel = default)
    {
        var byRepo = targets.ToDictionary(_ => _.Repo, StringComparer.OrdinalIgnoreCase);
        var repos = targets
            .Select(_ => _.Repo)
            .ToList();

        // Found once asked, so a change made while the question waited goes to the Recycle Bin
        // too. Each repository's at once, as git runs in each on its own.
        var found = await Task.Run(
            () => InParallel(
                repos,
                _ => FindDiscarding(byRepo[_], cancel),
                done =>
                {
                    if (repos.Count > 1)
                    {
                        progress?.Report($"Looked in {done} of {repos.Count}");
                    }
                },
                cancel),
            cancel);
        cancel.ThrowIfCancellationRequested();

        var paths = found
            .SelectMany(_ => _.OnDisk)
            .ToList();
        if (paths.Count > 0)
        {
            progress?.Report($"Moving {GitState.CountOf(paths.Count, "file")} to the Recycle Bin");
            if (!await recycle(paths))
            {
                return found
                    .Select(_ => new RepoResult(_.Target.Repo, false, "Not all the files could go to the Recycle Bin, so nothing was discarded. Those that went are there, to restore."))
                    .ToList();
            }
        }

        // Seen through once the files are in the Recycle Bin, cancelled or not: stopped there,
        // a file changed would be missing, not put back as the last commit has it.
        var byFound = found.ToDictionary(_ => _.Target.Repo, StringComparer.OrdinalIgnoreCase);
        return await ForEach(
            found
                .Select(_ => _.Target.Repo)
                .ToList(),
            progress,
            Cancel.None,
            _ => DiscardIn(byFound[_]));
    }

    // What Discard found in a repository: the changes in what it covers, as the Changes pane
    // reads them, null where git couldn't read them, and the files as they are on disk among
    // them, which go to the Recycle Bin. A repository of its own inside is listed as a folder,
    // which git clean leaves, so isn't among them, nor is a file deleted already.
    sealed record Discardable(Discarding Target, GitChanges? Changes, IReadOnlyList<Change> Covered, IReadOnlyList<string> OnDisk);

    static Discardable FindDiscarding(Discarding target, Cancel cancel)
    {
        if (GitChanges.Read(target.Repo, cancel) is not { } changes)
        {
            return new(target, null, [], []);
        }

        var covered = changes.Files
            .Where(_ => target.Folders.Count == 0 ||
                        _.Paths().Any(path => target.Folders.Any(folder => Locations.IsWithin(FullPath(target.Repo, path), folder))))
            .ToList();
        // As the Changes pane's Discard finds them: where a rename was from too, which putting it
        // back writes over, and each once, as two rows can share a path.
        var onDisk = GitChanges.OnDisk(target.Repo, covered);
        return new(target, changes, covered, onDisk);
    }

    static string FullPath(string repo, string path) =>
        Path.Combine(repo, path.Replace('/', '\\'));

    static RepoResult DiscardIn(Discardable found)
    {
        var repo = found.Target.Repo;
        if (found.Changes is not { } changes)
        {
            return new(repo, false, "Git couldn't read its changes, so nothing was discarded.");
        }

        List<string> output = [];
        if (found.OnDisk.Count > 0)
        {
            output.Add($"Moved {GitState.CountOf(found.OnDisk.Count, "file")} as they were to the Recycle Bin");
        }

        if (found.Target.Folders.Count == 0)
        {
            // Rather than restore, which puts back only what is not staged, leaving staged
            // changes for the next Push to commit. It ends a merge part way too.
            var reset = GitToItsEnd(repo, Cancel.None, "reset", "--hard");
            output.Add(reset.Output);
            if (!reset.Succeeded)
            {
                return new(repo, false, Lines(output));
            }

            var clean = Git(repo, "clean", "-f", "-d");
            output.Add(clean.Output);
            return new(repo, clean.Succeeded, Lines(output));
        }

        // Only the files found in the folders are put back, staged changes too, as the Changes
        // pane puts them back: reset has no way to stop at a folder.
        var restore = GitChanges.Restore(repo, found.Covered, changes.HasCommits, Cancel.None);
        output.Add(restore.Output);
        if (!restore.Succeeded)
        {
            return new(repo, false, Lines(output));
        }

        // Run in each folder, clean goes no further than it, and leaves the folder itself, as
        // Purge does.
        foreach (var folder in found.Target.Folders)
        {
            var clean = Git(folder, "clean", "-f", "-d");
            output.Add(clean.Output);
            if (!clean.Succeeded)
            {
                return new(repo, false, Lines(output));
            }
        }

        return new(repo, true, Lines(output));
    }

    static string Lines(IEnumerable<string> lines) =>
        string.Join('\n', lines.Where(_ => _.Length > 0));

    // Several repositories at once, each its own working tree, so a code folder's worth of
    // pulls is not one wait on the network after another. With more than one, reports how many
    // are done. With one, the entry's folder already says. Cancelled, it starts no more of them,
    // and gives those it went through, in the order they were given.
    internal static Task<List<RepoResult>> ForEach(IReadOnlyList<string> repos, IProgress<string>? progress, Cancel cancel, Func<string, RepoResult> action) =>
        Task.Run(
            () => InParallel(
                repos,
                action,
                done =>
                {
                    if (repos.Count > 1)
                    {
                        progress?.Report($"{done} of {repos.Count} done");
                    }
                },
                cancel),
            cancel);

    /// <summary>
    /// How many repositories git is run in at once.
    /// </summary>
    internal const int AtOnce = 6;

    /// <summary>
    /// <paramref name="action"/> for each item, <see cref="AtOnce"/> at a time, giving what each
    /// gave in the order the items are, and <paramref name="done"/> how many are done as each
    /// is. Cancelled, it starts no more, and gives only those it started.
    /// </summary>
    internal static List<T> InParallel<T>(IReadOnlyList<string> items, Func<string, T> action, Action<int>? done = null, Cancel cancel = default)
        where T : class
    {
        var results = new T?[items.Count];
        var finished = 0;
        Parallel.For(
            0,
            items.Count,
            new()
            {
                MaxDegreeOfParallelism = AtOnce
            },
            (index, state) =>
            {
                if (cancel.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }

                results[index] = action(items[index]);
                done?.Invoke(Interlocked.Increment(ref finished));
            });
        return [.. results.OfType<T>()];
    }

    internal static RepoResult Git(string repo, params string[] arguments) =>
        Git(repo, Cancel.None, network: false, arguments);

    /// <param name="cancel">Stops git, and whatever it started, ssh say, where it is: for one
    /// that changes the repository, <see cref="GitToItsEnd"/>.</param>
    /// <param name="network">Whether git may go to a remote, over ssh perhaps.</param>
    internal static RepoResult Git(string repo, Cancel cancel, bool network, params string[] arguments) =>
        Git(repo, cancel, network, null, arguments);

    /// <summary>
    /// Git changing the repository where it is, a commit, a switch or a reset say: not started
    /// once cancelled, but, once started, left to finish. Stopped part way it leaves the index
    /// locked, so that no git works there until the lock is deleted by hand, and the files half
    /// changed. What it gives is what git did, cancelled meanwhile or not: it is the step after
    /// that isn't started.
    /// </summary>
    internal static RepoResult GitToItsEnd(string repo, Cancel cancel, params string[] arguments) =>
        GitToItsEnd(repo, cancel, null, arguments);

    /// <param name="environment">Variables git is run with, GIT_INDEX_FILE say, for a commit
    /// made from an index of its own.</param>
    internal static RepoResult GitToItsEnd(string repo, Cancel cancel, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        if (cancel.IsCancellationRequested)
        {
            return new(repo, false, cancelled);
        }

        return Git(repo, Cancel.None, network: false, null, arguments, environment);
    }

    /// <param name="progress">Each line git writes as it goes, clone's "Receiving objects:  45%"
    /// with --progress say, as it writes it.</param>
    internal static RepoResult Git(string repo, Cancel cancel, bool network, Action<string>? progress, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (cancel.IsCancellationRequested)
        {
            return new(repo, false, cancelled);
        }

        try
        {
            var startInfo = StartInfo(repo, network, arguments, environment);
            using var process = Process.Start(startInfo)!;
            using var stopping = cancel.Register(() => Stop(process));
            // Cancel.None, not the token: cancelled, git is stopped, which ends its output, and
            // what it said till then goes in the details. Given the token, reading would throw
            // instead, and the details would say only that a task was cancelled.
            Task<string> error;
            if (progress == null)
            {
                error = process.StandardError.ReadToEndAsync(Cancel.None);
            }
            else
            {
                error = Task.Run(() => ReadReporting(process.StandardError, progress), Cancel.None);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (cancel.IsCancellationRequested)
            {
                return new(repo, false, $"{output}\n{error.Result}\n{cancelled}".Trim());
            }

            return new(repo, process.ExitCode == 0, $"{output}\n{error.Result}".Trim());
        }
        catch (Exception exception)
        {
            return new(repo, false, exception.Message);
        }
    }

    const string cancelled = "Cancelled";

    // How every git is started: in a hidden window, its output read as UTF-8, as git writes file
    // names whatever the console's code page.
    static ProcessStartInfo StartInfo(string repo, bool network, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Nobody can answer a prompt in a hidden window; fail instead of waiting forever.
        // That stops git's own, and BatchMode stops ssh's: for a key's passphrase, with no
        // agent holding it, or a host whose key is not known yet.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // In English, whatever the user's language: Purge reads clean's "Would remove" lines.
        // File names still come as UTF-8.
        startInfo.Environment["LC_ALL"] = "C";
        if (network &&
            NoSshCommandSet(repo, startInfo))
        {
            startInfo.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        if (environment != null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// What git wrote to its output, kept apart from what it wrote to its error stream, for a
    /// command whose output is parsed: a warning there would read as more of the output.
    /// </summary>
    /// <param name="ExitCode">What git ended with: 1 from diff --no-index when the files differ,
    /// which is what it was asked to find out rather than a failure.</param>
    internal sealed record GitRead(bool Succeeded, string Output, string Error, int ExitCode = 0);

    /// <summary>
    /// Runs git reading its output alone, as <see cref="GitRead"/> has it. <paramref name="input"/>,
    /// when given, is written to git, as git credential fill reads what it is asked for.
    /// <paramref name="encoding"/>, when given, is what the output is read as, in place of UTF-8:
    /// Latin-1, say, for each byte as the character of the same number.
    /// </summary>
    internal static GitRead GitOutput(string repo, Cancel cancel, string[] arguments, string? input = null, Encoding? encoding = null)
    {
        if (cancel.IsCancellationRequested)
        {
            return new(false, "", cancelled);
        }

        try
        {
            var startInfo = StartInfo(repo, network: false, arguments);
            if (input != null)
            {
                startInfo.RedirectStandardInput = true;
                startInfo.StandardInputEncoding = new UTF8Encoding(false);
            }

            using var process = Process.Start(startInfo)!;
            using var stopping = cancel.Register(() => Stop(process));
            var error = process.StandardError.ReadToEndAsync(Cancel.None);
            if (input != null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            string output;
            if (encoding == null)
            {
                output = process.StandardOutput.ReadToEnd();
            }
            else
            {
                // Decoded from the bytes rather than by the reader, which would take bytes at the
                // start that look like a byte order mark to say what the rest are, and drop them.
                using var bytes = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(bytes);
                output = encoding.GetString(bytes.GetBuffer(), 0, (int) bytes.Length);
            }

            process.WaitForExit();
            if (cancel.IsCancellationRequested)
            {
                return new(false, output, cancelled, process.ExitCode);
            }

            return new(process.ExitCode == 0, output, error.Result.Trim(), process.ExitCode);
        }
        catch (Exception exception)
        {
            return new(false, "", exception.Message);
        }
    }

    /// <summary>
    /// The files in conflict, one a line, as diff --name-only lists them: its output alone, as
    /// what it warns of on its error stream, a file whose line endings git would change say, is
    /// no file in conflict.
    /// </summary>
    internal static string ConflictedFiles(string repo) =>
        GitOutput(repo, Cancel.None, ["diff", "--name-only", "--diff-filter=U"]).Output.Trim();

    /// <summary>
    /// Runs git writing what it outputs, byte for byte, to <paramref name="path"/>: a file as a
    /// commit had it, which may not be text. False when git failed, or was cancelled.
    /// </summary>
    internal static bool GitToFile(string repo, string path, Cancel cancel, params string[] arguments)
    {
        if (cancel.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var startInfo = StartInfo(repo, network: false, arguments);
            using var process = Process.Start(startInfo)!;
            using var stopping = cancel.Register(() => Stop(process));
            var error = process.StandardError.ReadToEndAsync(Cancel.None);
            using (var file = File.Create(path))
            {
                process.StandardOutput.BaseStream.CopyTo(file);
            }

            process.WaitForExit();
            _ = error.Result;
            return process.ExitCode == 0 &&
                   !cancel.IsCancellationRequested;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    Win32Exception or
                    InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// What git wrote, as a terminal would show it, giving <paramref name="progress"/> each line
    /// as it comes. Git ends a line with a carriage return to write the next over it, so such a
    /// line is gone once the next one starts. A line from origin may end with what clears the
    /// rest of a terminal's line, which goes too.
    /// </summary>
    internal static string ReadReporting(TextReader reader, Action<string> progress)
    {
        var output = new StringBuilder();
        var line = new StringBuilder();
        var overwrite = false;
        var buffer = new char[1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            foreach (var character in buffer.AsSpan(0, read))
            {
                switch (character)
                {
                    case '\r':
                        Report();
                        overwrite = true;
                        break;
                    case '\n':
                        // After a carriage return, \r\n: the line is reported already.
                        if (!overwrite)
                        {
                            Report();
                        }

                        output.Append(Clean()).Append('\n');
                        line.Clear();
                        overwrite = false;
                        break;
                    default:
                        if (overwrite)
                        {
                            line.Clear();
                            overwrite = false;
                        }

                        line.Append(character);
                        break;
                }
            }
        }

        // The last line, as the terminal would be left showing it.
        if (line.Length > 0)
        {
            if (!overwrite)
            {
                Report();
            }

            output.Append(Clean());
        }

        return output.ToString();

        string Clean() =>
            line.ToString().Replace("\e[K", "").Trim();

        void Report()
        {
            var clean = Clean();
            if (clean.Length > 0)
            {
                progress(clean);
            }
        }
    }

    // The user's own ssh, set in the environment Lode was started with or in git's config, is
    // left as it is: it may be one that asks some other way.
    static bool NoSshCommandSet(string repo, ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.ContainsKey("GIT_SSH_COMMAND") ||
            startInfo.Environment.ContainsKey("GIT_SSH"))
        {
            return false;
        }

        var configured = Git(repo, "config", "--get", "core.sshCommand");
        return !configured.Succeeded ||
               configured.Output.Length == 0;
    }

    static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
            when (
                exception is
                    InvalidOperationException or
                    Win32Exception)
        {
            // Ended by itself meanwhile.
        }
    }

    /// <summary>
    /// What happened: a line saying how many repositories it worked for, then what git said in
    /// each, failures first. With none, <paramref name="none"/>: no forks, say, for Sync fork.
    /// </summary>
    public static ActionLog.Outcome GitOutcome(IReadOnlyList<RepoResult> results, string none = "No git repositories")
    {
        if (results.Count == 0)
        {
            return new(true, none, "");
        }

        var failed = results.Count(_ => !_.Succeeded);
        string summary;
        if (failed == 0)
        {
            summary = results.Count == 1 ? "Done" : $"{results.Count} repositories done";
        }
        else
        {
            summary = $"{failed} of {results.Count} failed";
        }

        var details = new StringBuilder();
        foreach (var result in results.OrderBy(_ => _.Succeeded))
        {
            if (details.Length > 0)
            {
                details.AppendLine();
            }

            var state = result.Succeeded ? "done" : "FAILED";
            details.AppendLine($"{Path.GetFileName(result.Repo)}: {state}");
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                details.AppendLine($"  {line}");
            }
        }

        return new(failed == 0, summary, details.ToString().TrimEnd());
    }

    // Only one update at a time. Two over the same files, as a folder and a repository in it
    // are, would race to write them, and two builds of a solution fight over its obj folder.
    // Purge NuGet cache waits its turn too, as it deletes the packages an update restores.
    internal static SemaphoreSlim NugetGate = new(1, 1);

    /// <summary>
    /// Updates the NuGet packages and dotnet tools of every solution in the folder, with
    /// PackageUpdateLib. Only solutions using central package management are updated. With
    /// build, each solution is built after its update.
    /// </summary>
    public static async Task<ActionLog.Outcome> UpdateNugets(string folder, IProgress<string>? progress = null, bool build = false)
    {
        await NugetGate.WaitAsync();
        UpdateResult result;
        try
        {
            result = await Task.Run(() => PackageUpdater.Run(folder, null, build, ForwardProgress(progress, "")));
        }
        catch (Exception exception)
        {
            return new(false, "Failed", exception.Message);
        }
        finally
        {
            NugetGate.Release();
        }

        return NugetOutcome(folder, result.Changes, result.Problems);
    }

    /// <summary>
    /// Updates every one of <paramref name="folders"/> in turn, as one update. One inside
    /// another is left out, as updating that one updates it too. A folder that can't be updated
    /// is a problem, and the rest are still updated.
    /// </summary>
    public static async Task<ActionLog.Outcome> UpdateNugets(IReadOnlyList<string> folders, IProgress<string>? progress = null, bool build = false)
    {
        folders = Outermost(folders);
        if (folders is [var only])
        {
            return await UpdateNugets(only, progress, build);
        }

        var changes = new List<PackageChange>();
        var problems = new List<UpdateProblem>();
        await NugetGate.WaitAsync();
        try
        {
            for (var index = 0; index < folders.Count; index++)
            {
                var folder = folders[index];
                var updateProgress = ForwardProgress(progress, $"{index + 1} of {folders.Count}: ");
                try
                {
                    var result = await Task.Run(() => PackageUpdater.Run(folder, null, build, updateProgress));
                    changes.AddRange(result.Changes);
                    problems.AddRange(result.Problems);
                }
                catch (Exception exception)
                {
                    problems.Add(new(UpdateProblemKind.Failed, folder, exception.Message));
                }
            }
        }
        finally
        {
            NugetGate.Release();
        }

        return NugetOutcome(CommonFolder(folders), changes, problems);
    }

    static IProgress<UpdateProgress>? ForwardProgress(IProgress<string>? progress, string prefix)
    {
        if (progress == null)
        {
            return null;
        }

        return new Forward<UpdateProgress>(_ => progress.Report(prefix + NugetProgress(_)));
    }

    /// <summary>
    /// The folders, each once, without any inside another of them.
    /// </summary>
    internal static List<string> Outermost(IReadOnlyList<string> folders) =>
        folders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(folder => !folders.Any(other => !Locations.Same(other, folder) &&
                                                   Locations.IsWithin(folder, other)))
            .ToList();

    /// <summary>
    /// The folder holding every one of <paramref name="folders"/>, which an update of them
    /// shows its files relative to, so each starts with the name of the folder it is in.
    /// </summary>
    internal static string CommonFolder(IReadOnlyList<string> folders)
    {
        var common = Path.GetDirectoryName(folders[0]);
        while (common != null &&
               !folders.All(_ => Locations.IsWithin(_, common)))
        {
            common = Path.GetDirectoryName(common);
        }

        return common ?? Path.GetPathRoot(folders[0])!;
    }

    /// <summary>
    /// Where an update has got to, for the panel: the solution or tool manifest, then the
    /// package, which changes fastest and so is last only when there is room.
    /// </summary>
    internal static string NugetProgress(UpdateProgress progress)
    {
        var file = Path.GetFileName(progress.File);
        if (progress.FileCount > 1)
        {
            file = $"{file} ({progress.FileNumber} of {progress.FileCount})";
        }

        if (progress.Package == null)
        {
            return file;
        }

        return $"{file}: {progress.Package}, {progress.PackageNumber} of {progress.PackageCount}";
    }

    // Passes reports straight on, on the thread that made them. Progress<T> would post each to
    // the thread it was made on, which ActionLog's progress does already.
    sealed class Forward<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) =>
            report(value);
    }

    /// <summary>
    /// What an update did, as an outcome: each package it changed, under the file it changed it
    /// in, then anything it skipped or failed at. A solution or tool manifest it could not update
    /// is a failure. Files are relative to the folder.
    /// </summary>
    internal static ActionLog.Outcome NugetOutcome(string folder, IReadOnlyList<PackageChange> changes, IReadOnlyList<UpdateProblem> problems)
    {
        var summary = changes.Count switch
        {
            0 => "Nothing to update",
            1 => "1 package updated",
            _ => $"{changes.Count} packages updated"
        };
        if (problems.Count > 0)
        {
            summary += problems.Count == 1 ? ", 1 problem" : $", {problems.Count} problems";
        }

        var details = new StringBuilder();
        foreach (var file in changes.GroupBy(_ => _.File, StringComparer.OrdinalIgnoreCase))
        {
            details.AppendLine(Path.GetRelativePath(folder, file.Key));
            foreach (var change in file)
            {
                details.AppendLine($"  {Describe(change)}");
            }
        }

        if (problems.Count > 0)
        {
            if (details.Length > 0)
            {
                details.AppendLine();
            }

            foreach (var problem in problems)
            {
                details.AppendLine($"{Path.GetRelativePath(folder, problem.File)}: {problem.Message}");
            }
        }

        var succeeded = !problems.Any(_ => _.IsFailure);
        return new(succeeded, summary, details.ToString().TrimEnd());
    }

    // The file is the line above, so only what changed in it.
    static string Describe(PackageChange change)
    {
        if (change.Kind == PackageChangeKind.Migration)
        {
            return $"{change.Package} {change.From} -> {change.Alternative} {change.To}";
        }

        if (change.Kind == PackageChangeKind.VersionOverride)
        {
            return $"{change.Package} VersionOverride: {change.From} -> {change.To}";
        }

        return $"{change.Package}: {change.From} -> {change.To}";
    }
}