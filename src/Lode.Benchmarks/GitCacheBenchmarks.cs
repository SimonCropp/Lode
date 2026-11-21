/// <summary>
/// What Lode does with each change on the drive the code folder is on. It hears all of them
/// (GitCache), so what one costs is paid for everything written anywhere on that drive.
/// Elsewhere is a change outside the code folder, as nearly all are. Source is to a file in a
/// repository, a few folders down, and Output to one a build wrote, further down. Index is to
/// git's own, as a commit or a stage writes it, and Objects to what git writes beside it, which
/// no state shows.
/// The code folder is on a share that isn't there, and a share is never watched, so only the
/// handling is timed. Nothing is read either, as no view is listening. It is one of 91
/// repositories, each known as one that has been read is.
/// </summary>
[MemoryDiagnoser]
public class GitCacheBenchmarks
{
    const string drive = @"\\Benchmarks\Share";
    const string code = $@"{drive}\Code";
    const string repo = $@"{code}\Repo";

    string elsewhere = "";
    string source = "";
    string output = "";
    string index = "";
    string objects = "";

    [GlobalSetup]
    public void Setup()
    {
        GitCache.Watch(code);
        for (var other = 0; other < 90; other++)
        {
            GitCache.Changes($@"{code}\Other{other:00}");
        }

        GitCache.Changes(repo);
        elsewhere = $@"{drive}\Users\Someone\AppData\Local\Temp\Build\0a1b2c3d\entry.tmp";
        source = $@"{repo}\src\Project\Areas\Things\Thing.cs";
        output = $@"{repo}\src\Project\obj\Debug\net11.0-windows\ref\Project.dll";
        index = $@"{repo}\.git\index";
        objects = $@"{repo}\.git\objects\3f\9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a";
    }

    [Benchmark]
    public void Elsewhere() =>
        GitCache.ChangedAt(elsewhere);

    [Benchmark]
    public void Source() =>
        GitCache.ChangedAt(source);

    [Benchmark]
    public void Output() =>
        GitCache.ChangedAt(output);

    [Benchmark]
    public void Index() =>
        GitCache.ChangedAt(index);

    [Benchmark]
    public void Objects() =>
        GitCache.ChangedAt(objects);
}
