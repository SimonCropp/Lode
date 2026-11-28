/// <summary>
/// What Push on a code folder does before it asks: whether any repository has anything to
/// commit, for it to ask for a message only then, and how many new files there are, for the
/// question to say. Across 50 repositories, every tenth with a change and a new file, the rest
/// with nothing to push. Cached is with GitCache knowing each of them, fresh, as it does once the
/// code folder has been shown; uncached is with none known. The repositories are made once, in
/// the temp folder, and kept for the next run.
/// </summary>
[MemoryDiagnoser]
public class PushBenchmarks
{
    [Params(false, true)]
    public bool Cached { get; set; }

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "Push");

    List<string> repos = [];

    [GlobalSetup]
    public void Setup()
    {
        repos = Repos();
        if (!Cached)
        {
            return;
        }

        GitCache.Watch(root);

        // Read again where the watch heard something meanwhile: just made, Windows brings a
        // folder's entry up to date only once the folder is next opened, as reading it does.
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var repo in repos.Where(_ => !Fresh(_)))
            {
                var before = GitCache.Changes(repo);
                GitCache.Store(repo, GitState.FromGit(repo, Cancel.None), null, before);
            }

            Thread.Sleep(1000);
        }

        var fresh = repos.Count(Fresh);
        if (fresh != repos.Count)
        {
            throw new($"Only {fresh} of {repos.Count} repositories are known fresh");
        }
    }

    static bool Fresh(string repo) =>
        GitCache.TryGet(repo, out _, out _, out var fresh) &&
        fresh;

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Cached)
        {
            GitCache.Reset();
        }
    }

    // How many new files, and whether there is anything to commit, as both are used.
    [Benchmark]
    public int Asking()
    {
        var found = CodeCommands.FindToCommit(repos);
        return found.Untracked.NewFiles + (found.Changed ? 1 : 0);
    }

    static List<string> Repos()
    {
        var ready = Path.Combine(root, "ready");
        if (!File.Exists(ready))
        {
            if (Directory.Exists(root))
            {
                BenchmarkGit.Delete(root);
            }

            for (var index = 0; index < 50; index++)
            {
                Create(index);
            }

            File.WriteAllText(ready, "");
        }

        return CodeCommands.FindRepos(root);
    }

    // A repository of a few files, committed and pushed to a bare origin beside it. Every tenth,
    // the last of each ten, has a file changed and a new one.
    static void Create(int index)
    {
        var name = $"Repo{index:00}";
        var origin = Path.Combine(root, "origins", $"{name}.git");
        Directory.CreateDirectory(origin);
        BenchmarkGit.Git(origin, "init", "--bare", "--initial-branch=main");

        var repo = Path.Combine(root, name);
        Directory.CreateDirectory(repo);
        BenchmarkGit.Git(repo, "init", "--initial-branch=main");
        for (var file = 0; file < 10; file++)
        {
            File.WriteAllText(Path.Combine(repo, $"{file}.txt"), $"{file}");
        }

        BenchmarkGit.Git(repo, "add", ".");
        BenchmarkGit.Commit(repo, "Initial");
        BenchmarkGit.Git(repo, "remote", "add", "origin", origin);
        BenchmarkGit.Git(repo, "push", "-u", "origin", "main");
        if (index % 10 == 9)
        {
            File.AppendAllText(Path.Combine(repo, "0.txt"), "changed");
            File.WriteAllText(Path.Combine(repo, "new.txt"), "new");
        }
    }
}
