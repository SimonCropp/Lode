/// <summary>
/// Reading a repository's history for the History pane by running git, as Lode does, against
/// reading it in process with LibGit2Sharp.
/// Page is the first 200 commits, newest first, as History lists them when it opens. LaterPage is
/// 200 after the first 2,000, as the list scrolled far down reads. Unpushed is the commits not
/// pushed yet, which get an arrow. Start is all History waits on as it opens: the first page,
/// those not pushed, and the files the newest commit changed, read one after another as History
/// reads them, git run for each, or the repository opened for each.
/// Small is a repository of 60 commits of 50 files; Large is one of 10,000 commits of 2,000 files,
/// merging a branch every 50 commits and tagged every 100. Each has three commits not pushed, and
/// is packed as git gc leaves a repository, its commit graph written. They are made once, with git
/// fast-import, in the temp folder, and kept for the next run. Real is the repository the
/// LodeBenchmarkRepo environment variable names, and is left out without it; both readers only
/// read, and take no lock. Before timing, each set is read by both, which must agree.
/// </summary>
[MemoryDiagnoser]
public class HistoryBenchmarks
{
    const string repoVariable = "LodeBenchmarkRepo";
    const int laterSkip = 2_000;

    [Params("git", "LibGit2Sharp")]
    public string Reader { get; set; } = "git";

    [ParamsSource(nameof(Sets))]
    public string Set { get; set; } = "Small";

    public static IEnumerable<string> Sets()
    {
        yield return "Small";
        yield return "Large";
        if (Environment.GetEnvironmentVariable(repoVariable) != null)
        {
            yield return "Real";
        }
    }

    string repo = "";
    bool libGit;

    [GlobalSetup]
    public void Setup()
    {
        repo = Repo(Set);
        libGit = Reader == "LibGit2Sharp";
        Agree(repo, strict: Set != "Real");
    }

    [Benchmark]
    public int Page() =>
        ReadCommits(0)?.Count ?? -1;

    [Benchmark]
    public int LaterPage() =>
        ReadCommits(laterSkip)?.Count ?? -1;

    [Benchmark]
    public int Unpushed() =>
        ReadUnpushed()?.Count ?? -1;

    // As History reads them as it opens, one after another, the newest commit's files once the
    // commits are read. How many commits, not pushed, and files there are, so all of it is used.
    [Benchmark]
    public int Start()
    {
        var commits = ReadCommits(0);
        var unpushed = ReadUnpushed();
        List<Change>? files = null;
        if (commits is [var newest, ..])
        {
            files = ReadFiles(newest);
        }

        return (commits?.Count ?? 0) + (unpushed?.Count ?? 0) + (files?.Count ?? 0);
    }

    List<GitCommit>? ReadCommits(int skip)
    {
        if (libGit)
        {
            return LibGitHistory.Read(repo, skip, GitHistory.Page, Cancel.None);
        }

        return GitHistory.Read(repo, skip, GitHistory.Page, Cancel.None);
    }

    HashSet<string>? ReadUnpushed()
    {
        if (libGit)
        {
            return LibGitHistory.Unpushed(repo, Cancel.None);
        }

        return GitHistory.Unpushed(repo, Cancel.None);
    }

    List<Change>? ReadFiles(GitCommit commit)
    {
        if (libGit)
        {
            return LibGitHistory.FilesOf(repo, commit, Cancel.None);
        }

        return GitHistory.FilesOf(repo, commit, Cancel.None);
    }

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "History");

    static string Repo(string set)
    {
        if (set == "Real")
        {
            return Environment.GetEnvironmentVariable(repoVariable)!;
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
                Create(folder, commits: 60, folders: 5, files: 10);
            }
            else
            {
                Create(folder, commits: 10_000, folders: 20, files: 100);
            }

            File.WriteAllText(ready, "");
        }

        return Path.Combine(folder, "Repo");
    }

    // The history made by git fast-import, far quicker than a commit at a time, pushed to a bare
    // origin beside it, then three more commits not pushed, each changing a file.
    static void Create(string folder, int commits, int folders, int files)
    {
        var origin = Path.Combine(folder, "origin.git");
        Directory.CreateDirectory(origin);
        BenchmarkGit.Git(origin, "init", "--bare", "--initial-branch=main");

        var repo = Path.Combine(folder, "Repo");
        Directory.CreateDirectory(repo);
        BenchmarkGit.Git(repo, "init", "--initial-branch=main");
        Import(repo, History(commits, folders, files));
        BenchmarkGit.Git(repo, "reset", "--hard", "main");
        BenchmarkGit.Git(repo, "remote", "add", "origin", origin);
        BenchmarkGit.Git(repo, "push", "-u", "origin", "main");
        for (var index = 0; index < 3; index++)
        {
            File.AppendAllText(Path.Combine(repo, "0", "0.txt"), $"Not pushed {index}\n");
            BenchmarkGit.Git(repo, "add", ".");
            BenchmarkGit.Commit(repo, $"Not pushed {index}");
        }

        BenchmarkGit.Git(repo, "gc", "--quiet");
    }

    static void Import(string repo, string history)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("fast-import");
        startInfo.ArgumentList.Add("--quiet");
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();

        // Stopped by something it was given, it closes the pipe before the rest is written, and
        // what it says is the reason.
        try
        {
            process.StandardInput.Write(history);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new($"git fast-import failed in {repo}: {error.Result}");
        }
    }

    static string[] authors = ["Ada Lovelace", "Grace Hopper", "Alan Turing", "Edsger Dijkstra", "Barbara Liskov"];

    // 2020-01-01, the first commit's time, each after it ten minutes later.
    const long firstTime = 1_577_836_800;

    // What git fast-import is given: a first commit of folders of files, then commits each
    // changing a file, every third a second file too. Every 50th merges a commit made on a branch
    // from the one before; every 100th is tagged, every 500th with an annotated tag. Subjects of
    // one line, of one with a body, and of two lines, which git log's %s joins.
    static string History(int commits, int folders, int files)
    {
        var history = new StringBuilder();
        var marks = 0;
        var main = 0;
        for (var index = 0; index < commits; index++)
        {
            var time = firstTime + index * 600L;
            var side = 0;
            if (index > 0 &&
                index % 50 == 0)
            {
                side = ++marks;
                Header(history, "refs/heads/side", side, index + 1, time - 300, $"Work on the side for {index}\n");
                history.Append($"from :{main}\n");
                Modify(history, "side.txt", $"Side {index}\n");
                history.Append('\n');
            }

            var commit = ++marks;
            Header(history, "refs/heads/main", commit, index, time, Message(index));
            if (main > 0)
            {
                history.Append($"from :{main}\n");
            }

            if (side > 0)
            {
                history.Append($"merge :{side}\n");
                Modify(history, "side.txt", $"Side {index}\n");
            }

            if (index == 0)
            {
                for (var folder = 0; folder < folders; folder++)
                {
                    for (var file = 0; file < files; file++)
                    {
                        Modify(history, $"{folder}/{file}.txt", $"{folder} {file}\n");
                    }
                }
            }
            else
            {
                Modify(history, $"{index % folders}/{index % files}.txt", $"Changed by {index}\n");
                if (index % 3 == 0)
                {
                    Modify(history, $"{(index + 1) % folders}/{(index + 7) % files}.txt", $"Also changed by {index}\n");
                }
            }

            history.Append('\n');
            main = commit;
            if (index == 0 ||
                index % 100 != 0)
            {
                continue;
            }

            var name = $"v{index / 100}.0";
            if (index % 500 == 0)
            {
                history.Append($"tag {name}\nfrom :{commit}\ntagger {authors[0]} <tagger@example.com> {time} +0000\n");
                // Unlike a commit, a tag ends at its message, with no blank line after.
                Data(history, $"Release {name}\n");
                continue;
            }

            history.Append($"reset refs/tags/{name}\nfrom :{commit}\n\n");
        }

        return history.ToString();
    }

    static string Message(int index)
    {
        if (index % 7 == 0)
        {
            return $"Fix the {index}th thing\n\nWhy it was wrong, and how this fixes it.\n";
        }

        if (index % 11 == 0)
        {
            return $"Change {index}, the start of a subject\nthat runs onto a second line\n\nThe body.\n";
        }

        return $"Change {index}\n";
    }

    static void Header(StringBuilder history, string branch, int mark, int index, long time, string message)
    {
        var author = authors[index % authors.Length];
        var email = $"{author.Split(' ')[0].ToLowerInvariant()}@example.com";
        history.Append($"commit {branch}\nmark :{mark}\n");
        history.Append($"author {author} <{email}> {time} +0000\n");
        history.Append($"committer {author} <{email}> {time} +0000\n");
        Data(history, message);
    }

    static void Modify(StringBuilder history, string path, string content)
    {
        history.Append($"M 100644 inline {path}\n");
        Data(history, content);
    }

    static void Data(StringBuilder history, string text) =>
        history.Append($"data {Encoding.UTF8.GetByteCount(text)}\n{text}\n");

    // Timing two readers is only fair if they read the same. A real repository may hold what
    // they read differently, a .mailmap renaming an author say, which git log follows and
    // LibGit2Sharp doesn't, so there that is said rather than thrown.
    static void Agree(string repo, bool strict)
    {
        var differences = new List<string>();
        Compare(differences, "Page", Lines(GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)), Lines(LibGitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)));
        Compare(differences, "LaterPage", Lines(GitHistory.Read(repo, laterSkip, GitHistory.Page, Cancel.None)), Lines(LibGitHistory.Read(repo, laterSkip, GitHistory.Page, Cancel.None)));
        Compare(differences, "Unpushed", GitHistory.Unpushed(repo, Cancel.None).Order().ToList(), LibGitHistory.Unpushed(repo, Cancel.None)?.Order().ToList());

        var gitCommits = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None);
        if (gitCommits is not [var newest, ..])
        {
            throw new($"{repo}: git read no commits");
        }

        Compare(differences, "Files", Lines(GitHistory.FilesOf(repo, newest, Cancel.None)), Lines(LibGitHistory.FilesOf(repo, newest, Cancel.None)));

        // A merge's, against its first parent.
        if (gitCommits.FirstOrDefault(_ => _.Parents.Count > 1) is { } merge)
        {
            Compare(differences, "Merge's files", Lines(GitHistory.FilesOf(repo, merge, Cancel.None)), Lines(LibGitHistory.FilesOf(repo, merge, Cancel.None)));
        }

        if (differences.Count == 0)
        {
            return;
        }

        var message = $"{repo}: {string.Join("; ", differences)}";
        if (strict)
        {
            throw new(message);
        }

        Console.WriteLine($"// Readers disagree: {message}");
    }

    // The first line the two differ at, and how many each read.
    static void Compare(List<string> differences, string what, List<string>? git, List<string>? libGit)
    {
        if (git == null ||
            libGit == null)
        {
            if (git != libGit)
            {
                differences.Add($"{what}: git read {git?.Count.ToString() ?? "nothing"}, LibGit2Sharp {libGit?.Count.ToString() ?? "nothing"}");
            }

            return;
        }

        for (var index = 0; index < Math.Max(git.Count, libGit.Count); index++)
        {
            var gitLine = git.ElementAtOrDefault(index);
            var libGitLine = libGit.ElementAtOrDefault(index);
            if (gitLine == libGitLine)
            {
                continue;
            }

            differences.Add($"{what} differs at {index} of {git.Count} and {libGit.Count}: git '{gitLine}', LibGit2Sharp '{libGitLine}'");
            return;
        }
    }

    // A commit's tags in order of name, as the order git lists several on one commit in isn't
    // what is being timed.
    static List<string>? Lines(List<GitCommit>? commits) =>
        commits?
            .Select(_ => $"{_.Sha} [{string.Join(' ', _.Parents)}] {_.Author} {_.When.ToUnixTimeSeconds()} [{string.Join(' ', _.Tags.Order())}] {_.Subject}")
            .ToList();

    static List<string>? Lines(List<Change>? files) =>
        files?
            .Select(_ => $"{_.Kind} {_.Path} {_.OldPath}")
            .ToList();
}
