/// <summary>
/// Git and NuGet commands for the repositories in a folder under the code folder. Each git
/// command does what the matching script in the code folder does (pull.ps1, push.ps1,
/// discard.ps1), to every repository it finds.
/// </summary>
static class CodeCommands
{
    public sealed record RepoResult(string Repo, bool Succeeded, string Output);

    /// <summary>
    /// The repositories a command acts on: the folder itself when it is one, otherwise every
    /// repository up to two levels below it, as the scripts find them.
    /// </summary>
    public static List<string> FindRepos(string folder)
    {
        if (IsRepo(folder))
        {
            return [folder];
        }

        var repos = new List<string>();
        Collect(folder, 2, repos);
        repos.Sort(StringComparer.OrdinalIgnoreCase);
        return repos;
    }

    /// <summary>
    /// The repositories of every one of <paramref name="folders"/>, each once.
    /// </summary>
    public static List<string> FindRepos(IReadOnlyList<string> folders)
    {
        if (folders is [var folder])
        {
            return FindRepos(folder);
        }

        var repos = folders
            .SelectMany(FindRepos)
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

    // A .git folder, or a .git file as a worktree or submodule has.
    internal static bool IsRepo(string folder)
    {
        var git = Path.Combine(folder, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    public static Task<List<RepoResult>> Pull(IReadOnlyList<string> repos, IProgress<string>? progress = null) =>
        ForEach(repos, progress, repo => Git(repo, "pull"));

    /// <summary>
    /// Commits every tracked change with the message, then pushes to origin.
    /// </summary>
    public static Task<List<RepoResult>> Push(IReadOnlyList<string> repos, string message, IProgress<string>? progress = null) =>
        ForEach(
            repos,
            progress,
            repo =>
            {
                var commit = Git(repo, "commit", "-am", message);

                // Nothing to commit is fine: there may still be commits to push.
                if (!commit.Succeeded && !NothingToCommit(repo))
                {
                    return commit;
                }

                var push = Git(repo, "push", "origin");
                return push with { Output = $"{commit.Output}\n{push.Output}".Trim() };
            });

    // Whether commit -a had nothing to commit: no tracked file changed, staged or not. Asked of
    // git, rather than read from what the commit said, which it words one way for a clean
    // folder and another for one with files it doesn't track. Those don't count, as commit -a
    // leaves them, and nor do changes inside a submodule, which it leaves too.
    static bool NothingToCommit(string repo)
    {
        var status = Git(repo, "status", "--porcelain", "--untracked-files=no", "--ignore-submodules=dirty");
        return status.Succeeded && status.Output.Length == 0;
    }

    /// <summary>
    /// Throws away changes to tracked files and deletes untracked ones. Cannot be undone.
    /// </summary>
    public static Task<List<RepoResult>> Discard(IReadOnlyList<string> repos, IProgress<string>? progress = null) =>
        ForEach(
            repos,
            progress,
            repo =>
            {
                var restore = Git(repo, "restore", ".");
                if (!restore.Succeeded)
                {
                    return restore;
                }

                var clean = Git(repo, "clean", "-f");
                return clean with { Output = $"{restore.Output}\n{clean.Output}".Trim() };
            });

    // With more than one repository, reports which it is on. With one, the entry's folder
    // already says.
    static Task<List<RepoResult>> ForEach(IReadOnlyList<string> repos, IProgress<string>? progress, Func<string, RepoResult> action) =>
        Task.Run(
            () => repos
                .Select((repo, index) =>
                {
                    if (repos.Count > 1)
                    {
                        progress?.Report($"{index + 1} of {repos.Count}: {Path.GetFileName(repo)}");
                    }

                    return action(repo);
                })
                .ToList());

    internal static RepoResult Git(string repo, params string[] arguments)
    {
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
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(startInfo)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new(repo, process.ExitCode == 0, $"{output}\n{error.Result}".Trim());
        }
        catch (Exception exception)
        {
            return new(repo, false, exception.Message);
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
            .Where(folder => !folders.Any(other => !Locations.Same(other, folder) && Locations.IsWithin(folder, other)))
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
