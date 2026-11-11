using Serilog;
using Serilog.Core;
using Serilog.Events;

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
    static bool IsRepo(string folder)
    {
        var git = Path.Combine(folder, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    public static Task<List<RepoResult>> Pull(IReadOnlyList<string> repos) =>
        ForEach(repos, repo => Git(repo, "pull"));

    /// <summary>
    /// Commits every tracked change with the message, then pushes to origin.
    /// </summary>
    public static Task<List<RepoResult>> Push(IReadOnlyList<string> repos, string message) =>
        ForEach(
            repos,
            repo =>
            {
                var commit = Git(repo, "commit", "-am", message);

                // Nothing to commit is fine: there may still be commits to push.
                if (!commit.Succeeded && !commit.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                {
                    return commit;
                }

                var push = Git(repo, "push", "origin");
                return push with { Output = $"{commit.Output}\n{push.Output}".Trim() };
            });

    /// <summary>
    /// Throws away changes to tracked files and deletes untracked ones. Cannot be undone.
    /// </summary>
    public static Task<List<RepoResult>> Discard(IReadOnlyList<string> repos) =>
        ForEach(
            repos,
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

    static Task<List<RepoResult>> ForEach(IReadOnlyList<string> repos, Func<string, RepoResult> action) =>
        Task.Run(() => repos.Select(action).ToList());

    static RepoResult Git(string repo, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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
    /// What happened, for a message box: how many succeeded, and the first line of what went
    /// wrong in each that failed.
    /// </summary>
    public static string Summarize(string action, string folder, IReadOnlyList<RepoResult> results)
    {
        if (results.Count == 0)
        {
            return $"No git repositories in {folder}.";
        }

        var failed = results.Where(_ => !_.Succeeded).ToList();
        var builder = new StringBuilder();
        builder.Append($"{action}: {results.Count - failed.Count} of {results.Count} repositories succeeded.");
        foreach (var result in failed)
        {
            var reason = result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "failed";
            builder.Append($"\n\n{Path.GetFileName(result.Repo)}: {reason}");
        }

        return builder.ToString();
    }

    // PackageUpdateLib writes through Serilog's global logger, so only one update at a time.
    static readonly SemaphoreSlim nugetGate = new(1, 1);

    /// <summary>
    /// Updates the NuGet packages and dotnet tools of every solution in the folder, with
    /// PackageUpdateLib. Only solutions using central package management are updated. Returns
    /// a summary for a message box.
    /// </summary>
    public static async Task<string> UpdateNugets(string folder)
    {
        await nugetGate.WaitAsync();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await Task.Run(() => PackageUpdater.Run(folder, null, false));
        }
        catch (Exception exception)
        {
            return $"Update NuGets failed: {exception.Message}";
        }
        finally
        {
            Log.Logger = previous;
            nugetGate.Release();
        }

        return sink.Summary(folder);
    }

    sealed class CollectingSink : ILogEventSink
    {
        readonly List<LogEvent> events = [];

        public void Emit(LogEvent logEvent)
        {
            lock (events)
            {
                events.Add(logEvent);
            }
        }

        public string Summary(string folder)
        {
            lock (events)
            {
                var updated = events.Count(_ => _.MessageTemplate.Text.StartsWith("    Updated", StringComparison.Ordinal));
                var problems = events
                    .Where(_ => _.Level >= LogEventLevel.Warning)
                    .Select(_ => _.RenderMessage().Trim())
                    .ToList();
                var builder = new StringBuilder($"Update NuGets in {folder}: {updated} updated.");
                foreach (var problem in problems)
                {
                    builder.Append($"\n\n{problem}");
                }

                return builder.ToString();
            }
        }
    }
}
