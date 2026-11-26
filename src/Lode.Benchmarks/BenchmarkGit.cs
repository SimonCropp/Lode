/// <summary>
/// Running git to make the repositories benchmarks read, and deleting them again.
/// </summary>
static class BenchmarkGit
{
    public static void Commit(string repo, string message) =>
        Git(repo, "-c", "user.name=Benchmark", "-c", "user.email=benchmark@example.com", "-c", "commit.gpgsign=false", "commit", "-m", message);

    public static void Git(string directory, params string[] arguments)
    {
        var result = CodeCommands.Git(directory, arguments);
        if (!result.Succeeded)
        {
            throw new($"git {string.Join(' ', arguments)} failed in {directory}: {result.Output}");
        }
    }

    public static void Delete(string folder)
    {
        // git marks its object files read-only.
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, true);
    }
}
