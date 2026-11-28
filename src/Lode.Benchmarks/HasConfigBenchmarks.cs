/// <summary>
/// Reading a repository's state as Lode reads it, where the user's config has an include on the
/// remotes' urls, as companies have to pick a work email: [includeIf
/// "hasconfig:remote.*.url:https://github.com/work/**"]. Include is that one, or none, for what
/// reading the repository costs without it. The repository is one of 50 files, its origin's url
/// one the include is for, made once, in the temp folder, and kept for the next run; the user's
/// config is one beside it, as GIT_CONFIG_GLOBAL names it for both readers. Before timing, what
/// is read must be what git status reads.
/// </summary>
[MemoryDiagnoser]
public class HasConfigBenchmarks
{
    [Params("none", "hasconfig")]
    public string Include { get; set; } = "none";

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "HasConfig");

    string repo = "";
    string? global;

    [GlobalSetup]
    public void Setup()
    {
        repo = Repo();
        global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, $"{Include}.gitconfig"));
        var git = GitState.FromGit(repo, Cancel.None);
        var read = InProcessState.FromFaster(repo, Cancel.None);
        if (git == null ||
            git != read)
        {
            throw new($"{repo}: git {git}, read {read}");
        }
    }

    [GlobalCleanup]
    public void Cleanup() =>
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);

    // How many changes, as the state is Lode's own type, and so it is used.
    [Benchmark]
    public int Read() =>
        InProcessState.FromFaster(repo, Cancel.None)?.Changes ?? -1;

    static string Repo()
    {
        var repo = Path.Combine(root, "Repo");
        var ready = Path.Combine(root, "ready");
        if (File.Exists(ready))
        {
            return repo;
        }

        if (Directory.Exists(root))
        {
            BenchmarkGit.Delete(root);
        }

        Directory.CreateDirectory(repo);
        BenchmarkGit.Git(repo, "init", "--initial-branch=main");
        for (var index = 0; index < 50; index++)
        {
            File.WriteAllText(Path.Combine(repo, $"{index}.txt"), $"{index}");
        }

        BenchmarkGit.Git(repo, "add", ".");
        BenchmarkGit.Commit(repo, "Initial");
        BenchmarkGit.Git(repo, "remote", "add", "origin", "https://github.com/work/Repo.git");
        File.AppendAllText(Path.Combine(repo, "1.txt"), "changed");
        File.WriteAllText(Path.Combine(root, "none.gitconfig"), "");
        File.WriteAllText(Path.Combine(root, "work.gitconfig"), "[user]\n\temail = work@example.com\n");
        File.WriteAllText(Path.Combine(root, "hasconfig.gitconfig"), "[includeIf \"hasconfig:remote.*.url:https://github.com/work/**\"]\n\tpath = work.gitconfig\n");
        File.WriteAllText(ready, "");
        return repo;
    }
}
