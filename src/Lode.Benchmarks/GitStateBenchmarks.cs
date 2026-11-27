/// <summary>
/// Reading repositories' state by running git status, as Lode's git actions run git, against
/// reading it in process from the files git keeps, as Lode reads it. One is what the status bar
/// waits on for the folder open; All is the Git column filled for a folder of repositories, six
/// read at a time, as Lode reads them.
/// Small is 30 repositories of 50 files, some changed, some ahead and some behind; Large is one
/// of 20,000 files, a few changed. They are made once, in the temp folder, and kept for the next
/// run. Code is every repository in the folder the LodeBenchmarkCode environment variable names,
/// a real code folder, and is left out without it; both readers only read, and take no lock.
/// Before timing, each set is read by both, which must agree.
/// </summary>
[MemoryDiagnoser]
public class GitStateBenchmarks
{
    const string codeVariable = "LodeBenchmarkCode";

    [Params("git", "InProcess")]
    public string Reader { get; set; } = "git";

    [ParamsSource(nameof(Sets))]
    public string Set { get; set; } = "Small";

    public static IEnumerable<string> Sets()
    {
        yield return "Small";
        yield return "Large";
        if (Environment.GetEnvironmentVariable(codeVariable) != null)
        {
            yield return "Code";
        }
    }

    static readonly GitState unreadable = new(null, null, 0, 0, []);

    List<string> repos = [];
    GitState.Reader read = GitState.FromGit;

    [GlobalSetup]
    public void Setup()
    {
        repos = Repos(Set);
        if (Reader == "InProcess")
        {
            read = InProcessState.FromFiles;
        }

        Agree(repos, strict: Set != "Code");
    }

    // How many changes, as the state is Lode's own type, and so it is used.
    [Benchmark]
    public int One() =>
        read(repos[0], Cancel.None)?.Changes ?? -1;

    [Benchmark]
    public int All() =>
        CodeCommands.InParallel(repos, _ => read(_, Cancel.None) ?? unreadable).Count;

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "GitState");

    static List<string> Repos(string set)
    {
        if (set == "Code")
        {
            var code = Environment.GetEnvironmentVariable(codeVariable)!;
            return CodeCommands.FindRepos(code);
        }

        var folder = Path.Combine(root, set);
        var ready = Path.Combine(folder, "ready");
        if (!File.Exists(ready))
        {
            if (Directory.Exists(folder))
            {
                BenchmarkGit.Delete(folder);
            }

            if (set == "Small")
            {
                for (var index = 0; index < 30; index++)
                {
                    CreateSmall(folder, index);
                }
            }
            else
            {
                CreateLarge(folder);
            }

            File.WriteAllText(ready, "");
        }

        return CodeCommands.FindRepos(folder);
    }

    // Every seventh is a commit behind, every fifth has a commit not pushed, and every third has
    // changes, in that order, as going back a commit throws away what is uncommitted.
    static void CreateSmall(string folder, int index)
    {
        var repo = Create(folder, $"Repo{index:00}", 1, 50);
        if (index % 7 == 0)
        {
            File.WriteAllText(Path.Combine(repo, "behind.txt"), "behind");
            BenchmarkGit.Git(repo, "add", "behind.txt");
            BenchmarkGit.Commit(repo, "Behind");
            BenchmarkGit.Git(repo, "push");
            BenchmarkGit.Git(repo, "reset", "--hard", "HEAD~1");
        }

        if (index % 5 == 0)
        {
            File.WriteAllText(Path.Combine(repo, "ahead.txt"), "ahead");
            BenchmarkGit.Git(repo, "add", "ahead.txt");
            BenchmarkGit.Commit(repo, "Ahead");
        }

        if (index % 3 == 0)
        {
            File.AppendAllText(Path.Combine(repo, "0", "1.txt"), "changed");
            File.AppendAllText(Path.Combine(repo, "0", "2.txt"), "changed");
            File.WriteAllText(Path.Combine(repo, "untracked.txt"), "untracked");
        }
    }

    // 200 folders of 100 files, ten changed and five new.
    static void CreateLarge(string folder)
    {
        var repo = Create(folder, "Repo", 200, 100);
        for (var index = 0; index < 10; index++)
        {
            File.AppendAllText(Path.Combine(repo, $"{index * 20}", "1.txt"), "changed");
        }

        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(repo, $"{index * 40}", "untracked.txt"), "untracked");
        }
    }

    // A repository of folders of files, committed and pushed to a bare origin beside it.
    static string Create(string folder, string name, int folders, int files)
    {
        var origin = Path.Combine(folder, "origins", $"{name}.git");
        Directory.CreateDirectory(origin);
        BenchmarkGit.Git(origin, "init", "--bare", "--initial-branch=main");

        var repo = Path.Combine(folder, name);
        Directory.CreateDirectory(repo);
        BenchmarkGit.Git(repo, "init", "--initial-branch=main");
        for (var folderIndex = 0; folderIndex < folders; folderIndex++)
        {
            var directory = Path.Combine(repo, $"{folderIndex}");
            Directory.CreateDirectory(directory);
            for (var fileIndex = 0; fileIndex < files; fileIndex++)
            {
                File.WriteAllText(Path.Combine(directory, $"{fileIndex}.txt"), $"{folderIndex} {fileIndex}");
            }
        }

        BenchmarkGit.Git(repo, "add", ".");
        BenchmarkGit.Commit(repo, "Initial");
        BenchmarkGit.Git(repo, "remote", "add", "origin", origin);
        BenchmarkGit.Git(repo, "push", "-u", "origin", "main");
        return repo;
    }

    // Timing two readers is only fair if they read the same. A real code folder may hold what
    // they count differently, a submodule moved say, so there that is said rather than thrown.
    static void Agree(List<string> repos, bool strict)
    {
        if (repos.Count == 0)
        {
            throw new("No repositories to read");
        }

        foreach (var repo in repos)
        {
            var git = GitState.FromGit(repo, Cancel.None);
            var inProcess = InProcessState.FromFiles(repo, Cancel.None);
            if (git == inProcess)
            {
                continue;
            }

            var message = $"{repo}: git {git}, in process {inProcess}";
            if (strict)
            {
                throw new(message);
            }

            Console.WriteLine($"// Readers disagree: {message}");
        }
    }
}
