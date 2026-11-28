/// <summary>
/// Reading in process a repository of 2,000 files whose times have changed but whose content
/// hasn't, as npm install, a formatter or a build leaves the files it writes again: once, and
/// twice, as Lode reads a repository again each time something in it changes. Nothing writes
/// the index, as Lode runs git with --no-optional-locks, so every read finds the files' times
/// not those staged. The repository is made once, in the temp folder, and kept for the next
/// run; its files are touched again before each iteration, so the iteration's first read is
/// the first to find them so. Before timing, it is read by git and in process, which must agree
/// that nothing has changed.
/// </summary>
[MemoryDiagnoser]
public class TouchedFilesBenchmarks
{
    const int folders = 20;
    const int filesInEach = 100;

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "TouchedFiles");

    readonly string repo = Path.Combine(root, "Repo");
    List<string> files = [];
    int touches;

    [GlobalSetup]
    public void Setup()
    {
        var ready = Path.Combine(root, "ready");
        if (!File.Exists(ready))
        {
            if (Directory.Exists(root))
            {
                BenchmarkGit.Delete(root);
            }

            Create();
            File.WriteAllText(ready, "");
        }

        files = [];
        for (var folder = 0; folder < folders; folder++)
        {
            files.AddRange(Directory.EnumerateFiles(Path.Combine(repo, $"{folder}")));
        }

        Touch();
        var git = GitState.FromGit(repo, Cancel.None);
        var inProcess = InProcessState.FromFiles(repo, Cancel.None);
        if (git == null ||
            git != inProcess ||
            git.Changes != 0)
        {
            throw new($"Readers disagree, or find changes: git {git}, in process {inProcess}");
        }
    }

    // Each file's write time a second on from the last iteration's, and an hour before now,
    // long enough ago that nothing is still writing it.
    [IterationSetup]
    public void Touch()
    {
        touches++;
        var time = DateTime.UtcNow.AddHours(-1).AddSeconds(touches);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, time);
        }
    }

    [Benchmark]
    public int ReadOnce() =>
        Read();

    [Benchmark]
    public int ReadTwice() =>
        Read() + Read();

    int Read() =>
        InProcessState.FromFiles(repo, Cancel.None)?.Changes ?? -1;

    // Folders of files of some 4 KB, as source files are, committed.
    void Create()
    {
        Directory.CreateDirectory(repo);
        BenchmarkGit.Git(repo, "init", "--initial-branch=main");
        for (var folder = 0; folder < folders; folder++)
        {
            var directory = Path.Combine(repo, $"{folder}");
            Directory.CreateDirectory(directory);
            for (var file = 0; file < filesInEach; file++)
            {
                var lines = Enumerable.Range(0, 100).Select(_ => $"Line {_} of file {file} in folder {folder}.");
                File.WriteAllText(Path.Combine(directory, $"{file}.txt"), string.Join('\n', lines) + "\n");
            }
        }

        BenchmarkGit.Git(repo, "add", ".");
        BenchmarkGit.Commit(repo, "Initial");
    }
}
