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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
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
                return Git(repo, cancel, network: false, "switch", branch.Output);
            });

    const string originHead = "refs/remotes/origin/";

    /// <summary>
    /// The branch origin's HEAD names, main say, as clone recorded it, without asking origin.
    /// Null without an origin, or in a repository made by init then given an origin, which has
    /// none recorded.
    /// </summary>
    internal static string? RecordedDefaultBranch(string repo, Cancel cancel)
    {
        var recorded = Git(repo, cancel, network: false, "symbolic-ref", "--quiet", originHead + "HEAD");
        if (recorded.Succeeded &&
            recorded.Output.StartsWith(originHead, StringComparison.Ordinal))
        {
            return recorded.Output[originHead.Length..];
        }

        return null;
    }

    // The branch origin's HEAD names, as the output of a result that succeeded: as clone
    // recorded it, or, in a repository made by init then given an origin, which has none
    // recorded, as origin says now. Fails, saying why, without an origin, or one with no HEAD.
    static RepoResult DefaultBranch(string repo, Cancel cancel)
    {
        if (RecordedDefaultBranch(repo, cancel) is { } recorded)
        {
            return new(repo, true, recorded);
        }

        var remote = Git(repo, cancel, network: true, "ls-remote", "--symref", "origin", "HEAD");
        if (!remote.Succeeded)
        {
            return remote with
            {
                Output = $"Couldn't ask origin for its default branch:\n{remote.Output}"
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
            Output = "No default branch: origin's HEAD names no branch."
        };
    }

    /// <summary>
    /// Tags the commit each repository is at with <paramref name="name"/>, then pushes the tag to
    /// origin. One that has a tag of that name already fails, and keeps the tag it has.
    /// </summary>
    public static Task<List<RepoResult>> Tag(IReadOnlyList<string> repos, string name, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var tag = Git(repo, cancel, network: false, "tag", name);
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
                var local = Git(repo, cancel, network: false, "tag", "-d", name);
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
    /// The tags in the repositories, the newest first, each once.
    /// </summary>
    public static List<string> Tags(IReadOnlyList<string> repos) =>
        repos
            .SelectMany(
                repo =>
                {
                    var result = Git(repo, "tag", "--sort=-creatordate");
                    if (!result.Succeeded)
                    {
                        return [];
                    }

                    return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Commits every tracked change with the message, then pushes to origin.
    /// </summary>
    public static Task<List<RepoResult>> Push(IReadOnlyList<string> repos, string message, IProgress<string>? progress = null, Cancel cancel = default) =>
        ForEach(
            repos,
            progress,
            cancel,
            repo =>
            {
                var commit = Git(repo, cancel, network: false, "commit", "-am", message);

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

    // Whether commit -a had nothing to commit: no tracked file changed, staged or not. Asked of
    // git, rather than read from what the commit said, which it words one way for a clean
    // folder and another for one with files it doesn't track. Those don't count, as commit -a
    // leaves them, and nor do changes inside a submodule, which it leaves too.
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
                var reset = Git(repo, cancel, network: false, "reset", "--hard");
                if (!reset.Succeeded)
                {
                    return reset;
                }

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
                cancel,
                action,
                done =>
                {
                    if (repos.Count > 1)
                    {
                        progress?.Report($"{done} of {repos.Count} done");
                    }
                }),
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
    internal static List<T> InParallel<T>(IReadOnlyList<string> items, Cancel cancel, Func<string, T> action, Action<int>? done = null)
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

    /// <param name="cancel">Stops git, and whatever it started, ssh say, where it is.</param>
    /// <param name="network">Whether git may go to a remote, over ssh perhaps.</param>
    internal static RepoResult Git(string repo, Cancel cancel, bool network, params string[] arguments)
    {
        if (cancel.IsCancellationRequested)
        {
            return new(repo, false, cancelled);
        }

        try
        {
            // Git writes file names as UTF-8, whatever the console's code page.
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

            using var process = Process.Start(startInfo)!;
            using var stopping = cancel.Register(() => Stop(process));
            // Cancel.None, not the token: cancelled, git is stopped, which ends its output, and
            // what it said till then goes in the details. Given the token, reading would throw
            // instead, and the details would say only that a task was cancelled.
            var error = process.StandardError.ReadToEndAsync(Cancel.None);
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
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Ended by itself meanwhile.
        }
    }

    /// <summary>
    /// What happened: a line saying how many repositories it worked for, then what git said in
    /// each, failures first.
    /// </summary>
    public static ActionLog.Outcome GitOutcome(IReadOnlyList<RepoResult> results)
    {
        if (results.Count == 0)
        {
            return new(true, "No git repositories", "");
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
    static readonly SemaphoreSlim nugetGate = new(1, 1);

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
