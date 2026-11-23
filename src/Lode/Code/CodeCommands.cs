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
        const string remote = "ref: " + originHead;
        try
        {
            if (GitDirectory(repo) is not { } git)
            {
                return false;
            }

            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            var origin = File.ReadAllText(Path.Combine(CommonDirectory(git), "refs", "remotes", "origin", "HEAD")).Trim();
            return head.StartsWith(local, StringComparison.Ordinal) &&
                   origin.StartsWith(remote, StringComparison.Ordinal) &&
                   head[local.Length..] == origin[remote.Length..];
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

    public static Task<List<RepoResult>> Pull(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => Git(repo, cancel, network: true, "pull"));

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
    /// upstream's default branch into the branch checked out, then pushes the branch to origin.
    /// A branch that has diverged from origin is left as it is, as is one whose merge would
    /// conflict: the merge is undone, so none is left part way through one.
    /// </summary>
    public static Task<List<RepoResult>> SyncFork(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => SyncFork(repo, cancel));

    static RepoResult SyncFork(string repo, Cancel cancel)
    {
        if (WhyNotChange(repo, "nothing was merged or pushed") is { } why)
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

        return Said(Git(repo, cancel, network: true, "push", "origin"));

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
        var tracked = Git(repo, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
        if (!tracked.Succeeded ||
            tracked.Output.Length == 0)
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
                $"The branch has diverged from {tracked.Output}: {GitState.CountOf(ahead, "commit")} here aren't there, and {GitState.CountOf(behind, "commit")} there aren't here. Pull, then sync again. Nothing was merged or pushed.");
        }

        return GitToItsEnd(repo, cancel, "merge", "--ff-only", "@{upstream}");
    }

    // A merge that failed: refused before it began, uncommitted changes in the way say, which
    // changed nothing, or stopped part way, by a conflict, which is undone, the branch and its
    // files as they were before it.
    static RepoResult UndoMerge(string repo, string from, RepoResult merge)
    {
        if (!Git(repo, "rev-parse", "--quiet", "--verify", "MERGE_HEAD").Succeeded)
        {
            return merge with
            {
                Output = $"Couldn't merge {from}, so nothing was merged or pushed."
            };
        }

        var conflicted = Git(repo, "diff", "--name-only", "--diff-filter=U").Output;
        var abort = Git(repo, "merge", "--abort");
        var undone = "The merge was undone: merge it yourself, settling the conflicts.";
        if (!abort.Succeeded)
        {
            undone = $"The merge couldn't be undone: settle the conflicts, or git merge --abort.\n{abort.Output}";
        }

        // Stopped other than by a conflict, as by a commit git couldn't make, there are no files
        // to name.
        if (conflicted.Length == 0)
        {
            return merge with
            {
                Succeeded = false,
                Output = $"Merging {from} stopped part way, so nothing was pushed. {undone}"
            };
        }

        return merge with
        {
            Succeeded = false,
            Output = $"Merging {from} would conflict, so nothing was pushed. {undone}\nIn conflict:\n{conflicted}"
        };
    }

    /// <summary>
    /// Pushes the commits the branch has that the one it tracks hasn't, committing nothing.
    /// </summary>
    public static Task<List<RepoResult>> PushCommits(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(repos, progress, cancel, repo => Git(repo, cancel, network: true, "push"));

    /// <summary>
    /// Pulls, then pushes: a branch that has diverged from the one it tracks, which origin would
    /// refuse a push to until it has origin's commits too. A pull that fails pushes nothing.
    /// </summary>
    public static Task<List<RepoResult>> PullThenPush(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var pull = Git(repo, cancel, network: true, "pull");
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
    static RepoResult DefaultBranch(string repo, Cancel cancel, string name = "origin")
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
    /// Tags the commit each repository is at with <paramref name="name"/>, or
    /// <paramref name="commit"/> where given, as History tags the commit picked, then pushes the
    /// tag to origin. One that has a tag of that name already fails, and keeps the tag it has.
    /// </summary>
    public static Task<List<RepoResult>> Tag(IReadOnlyList<string> repos, string name, IProgress<string>? progress = null, Cancel cancel = default, string? commit = null) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                string[] arguments = ["tag", name];
                if (commit != null)
                {
                    arguments = ["tag", name, commit];
                }

                var tag = GitToItsEnd(repo, cancel, arguments);
                if (!tag.Succeeded)
                {
                    return tag;
                }

                var push = Git(repo, cancel, network: true, "push", "origin", $"refs/tags/{name}");
                return push with
                {
                    Output = $"Tagged {name}\n{push.Output}".Trim()
                };
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
    /// them, though not what git ignores, then pushes to origin.
    /// </summary>
    public static Task<List<RepoResult>> Push(IReadOnlyList<string> repos, string message, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                if (WhyNotChange(repo, "nothing was committed or pushed") is { } why)
                {
                    return new(repo, false, why);
                }

                var add = GitToItsEnd(repo, cancel, "add", "--all");
                if (!add.Succeeded)
                {
                    return add;
                }

                var commit = GitToItsEnd(repo, cancel, "commit", "-m", message);

                // Nothing to commit is fine: there may still be commits to push.
                if (!commit.Succeeded &&
                    !NothingToCommit(repo))
                {
                    return commit;
                }

                var push = Git(repo, cancel, network: true, "push", "origin");
                return push with
                {
                    Output = $"{commit.Output}\n{push.Output}".Trim()
                };
            });

    // Why Push is not to commit in a repository, nor Sync fork to merge, or null where they can,
    // ending with what was not done. With files still in conflict, from a pull say, Push would
    // commit them as they are, the markers in them, as the merge settled, and send that. With no
    // branch checked out, as part way through a rebase, or on a tag, either would make a commit
    // no branch has, which there is nowhere to send, and the next switch leaves behind.
    static string? WhyNotChange(string repo, string notDone)
    {
        var unmerged = Git(repo, "ls-files", "--unmerged");
        if (unmerged is
            {
                Succeeded: true,
                Output.Length: > 0
            })
        {
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

    // Whether the commit had nothing to commit: everything added, nothing staged. Asked of git,
    // rather than read from what the commit said, which it words one way for a clean folder and
    // another for one with files it doesn't track, which add leaves where it can't add them, a
    // repository of its own inside say. Nor do changes inside a submodule count, which add
    // leaves too.
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
    /// How many files the repositories have that git doesn't track yet, nor ignores: the new
    /// files Push adds, for its question to say. A folder of them counts each file in it.
    /// </summary>
    public static int NewFiles(IReadOnlyList<string> repos) =>
        repos.Sum(_ =>
        {
            var result = Git(_, "ls-files", "--others", "--exclude-standard", "-z");
            if (!result.Succeeded)
            {
                return 0;
            }

            return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        });

    /// <summary>
    /// Throws away every uncommitted change, staged or not, and deletes what git doesn't track,
    /// folders included, though not what it ignores: that is Purge's. A repository with nothing
    /// committed yet goes back to empty. Cannot be undone.
    /// </summary>
    public static Task<List<RepoResult>> Discard(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                // Rather than restore, which puts back only what is not staged, leaving staged
                // changes for the next Push to commit.
                var reset = GitToItsEnd(repo, cancel, "reset", "--hard");
                if (!reset.Succeeded)
                {
                    return reset;
                }

                // Stopped where it is when cancelled, unlike the reset: it locks nothing, and
                // what it has yet to delete is what cancelling is for.
                var clean = Git(repo, cancel, network: false, "clean", "-f", "-d");
                return clean with
                {
                    Output = $"{reset.Output}\n{clean.Output}".Trim()
                };
            });

    // Several repositories at once, each its own working tree, so a code folder's worth of
    // pulls is not one wait on the network after another. With more than one, reports how many
    // are done. With one, the entry's folder already says. Cancelled, it starts no more of them,
    // and gives those it went through, in the order they were given.
    static Task<List<RepoResult>> ForEach(IReadOnlyList<string> repos, IProgress<string>? progress, Cancel cancel, Func<string, RepoResult> action) =>
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
    /// </summary>
    internal static GitRead GitOutput(string repo, Cancel cancel, string[] arguments, string? input = null)
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

            var output = process.StandardOutput.ReadToEnd();
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
    static SemaphoreSlim nugetGate = new(1, 1);

    /// <summary>
    /// Updates the NuGet packages and dotnet tools of every solution in the folder, with
    /// PackageUpdateLib. Only solutions using central package management are updated. With
    /// build, each solution is built after its update.
    /// </summary>
    public static async Task<ActionLog.Outcome> UpdateNugets(string folder, IProgress<string>? progress = null, bool build = false)
    {
        await nugetGate.WaitAsync();
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
            nugetGate.Release();
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
        await nugetGate.WaitAsync();
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
            nugetGate.Release();
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