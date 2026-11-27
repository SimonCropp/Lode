using System.Text;

/// <summary>
/// What git ignores, as read in process, git check-ignore the oracle: patterns of every kind,
/// wildmatch's included, tried on files and folders, in folders and out, with .gitignore files
/// in folders, info/exclude and core.excludesFile.
/// </summary>
public class IgnoreRulesTests
{
    static readonly string[] files =
    [
        "a.txt", "b.log", "keep.log", "abc", "aXc", "a1c", "a-c", "a]c", "a c", "a[b]c", "foo bar.txt",
        "UPPER.TXT", ".hidden", "dir/a.txt", "dir/b.log", "dir/sub/c.log", "dir/sub/a.txt",
        "x/a.tmp", "x/y/z/deep.tmp", "build/out.dll", "src/build/out.dll", "Debug/out.dll",
        "node_modules/pkg/index.js"
    ];

    static readonly string[] folders =
    [
        "build", "src", "src/build", "dir", "dir/sub", "x", "x/y", "x/y/z", "Debug", "node_modules",
        "node_modules/pkg", "empty"
    ];

    // Names no file may have on Windows, or that aren't there: git takes them as files.
    static readonly string[] missing = ["a*b", "a?b", "#hash", "!bang", "trailing ", "foo[bar", "x/gone/file"];

    static string Layout(CodeCommandsTests.Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(repo, folder));
        }

        foreach (var file in files)
        {
            var path = Path.Combine(repo, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }

        return repo;
    }

    static IEnumerable<(string Path, bool Folder)> Paths() =>
        files.Select(_ => (_, false))
            .Concat(folders.Select(_ => (_, true)))
            .Concat(missing.Select(_ => (_, false)));

    // What git check-ignore says is ignored of the paths, without the index.
    static HashSet<string> GitIgnored(string repo, IEnumerable<string> paths)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(paths.Select(_ => _ + '\0')));
        var result = GitRun.Run(repo, null, input, "check-ignore", "--no-index", "--stdin", "-z");
        if (result.ExitCode > 1)
        {
            throw new($"git check-ignore failed: {result.Error}");
        }

        return result.Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    // What is ignored as read in process, each folder on the way entered.
    static HashSet<string> Ignored(string repo, IEnumerable<(string Path, bool Folder)> paths)
    {
        using var repository = GitRepository.Open(repo)!;
        var rules = new IgnoreRules(repository, repo);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, folder) in paths)
        {
            var parts = path.Split('/');
            var rulesFolder = rules.Top();
            foreach (var part in parts[..^1])
            {
                rulesFolder = rules.Enter(rulesFolder, part);
            }

            if (rules.IsIgnored(rulesFolder, parts[^1], folder))
            {
                ignored.Add(path);
            }
        }

        return ignored;
    }

    static async Task AgreeWithGit(string repo)
    {
        var paths = Paths().ToList();
        var git = GitIgnored(repo, paths.Select(_ => _.Path));
        var inProcess = Ignored(repo, paths);

        await Assert.That(inProcess.Order(StringComparer.Ordinal)).IsEquivalentTo(git.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    // One .gitignore at the top, of each kind of pattern: a name anywhere, a folder only, one
    // anchored, folders by ** and *, ? and classes of every kind, ranges, negated, escaped
    // characters, trailing spaces and the escaped one, patterns undone by a ! after, or not
    // undone by one before, and ones git takes as broken.
    [Test]
    [Arguments("*.log")]
    [Arguments("*.log\n!keep.log")]
    [Arguments("!keep.log\n*.log")]
    [Arguments("build/")]
    [Arguments("/build/")]
    [Arguments("build")]
    [Arguments("/a.txt")]
    [Arguments("a.txt")]
    [Arguments("dir/*.txt")]
    [Arguments("dir/**/c.log")]
    [Arguments("dir/**")]
    [Arguments("dir/**/")]
    [Arguments("**/deep.tmp")]
    [Arguments("**/sub")]
    [Arguments("x/**/z")]
    [Arguments("x/*/z")]
    [Arguments("x/*")]
    [Arguments("x/**")]
    [Arguments("x/**/**/deep.tmp")]
    [Arguments("**/x/**")]
    [Arguments("**")]
    [Arguments("*")]
    [Arguments("**/")]
    [Arguments("*/")]
    [Arguments("*/b.log")]
    [Arguments("**/b.log")]
    [Arguments("dir/**/sub/*")]
    [Arguments("/dir/sub/*.log")]
    [Arguments("dir/sub/")]
    [Arguments("a?c")]
    [Arguments("a?b")]
    [Arguments("a[bc]c")]
    [Arguments("a[!b]c")]
    [Arguments("a[^b]c")]
    [Arguments("a[a-c]c")]
    [Arguments("a[B-D]c")]
    [Arguments("a[W-Y]c")]
    [Arguments("a[]]c")]
    [Arguments("a[x-]c")]
    [Arguments("a[-x]c")]
    [Arguments("a[z-a]c")]
    [Arguments("a[\\]]c")]
    [Arguments("a[\\b]c")]
    [Arguments("a[a-\\c]c")]
    [Arguments("a[[:alpha:]]c")]
    [Arguments("a[[:digit:]]c")]
    [Arguments("a[[:upper:]]c")]
    [Arguments("a[[:lower:]]c")]
    [Arguments("a[[:alnum:]]c")]
    [Arguments("a[[:punct:]]c")]
    [Arguments("a[[:space:]]c")]
    [Arguments("a[[:blank:]]c")]
    [Arguments("a[[:xdigit:]]c")]
    [Arguments("a[[:cntrl:]]c")]
    [Arguments("a[[:graph:]]c")]
    [Arguments("a[[:print:]]c")]
    [Arguments("a[[:bogus:]]c")]
    [Arguments("a[[:alpha:]c")]
    [Arguments("a[[:alpha]]c")]
    [Arguments("a[[:]]c")]
    [Arguments("a[b")]
    [Arguments("a[")]
    [Arguments("a[\\")]
    [Arguments("a\\")]
    [Arguments("foo[bar")]
    [Arguments("a\\*b")]
    [Arguments("a\\?b")]
    [Arguments("\\#hash")]
    [Arguments("#hash")]
    [Arguments("\\!bang")]
    [Arguments("trailing\\ ")]
    [Arguments("trailing   ")]
    [Arguments("trailing\\")]
    [Arguments("foo\\ bar.txt")]
    [Arguments("a\\ c")]
    [Arguments("D*g/")]
    [Arguments("[Dd]ebug/")]
    [Arguments("node_modules/")]
    [Arguments("*.TXT")]
    [Arguments("upper.txt")]
    [Arguments("*.Txt")]
    [Arguments(".*")]
    [Arguments("*.tmp\n!x/a.tmp")]
    [Arguments("x/\n!x/a.tmp")]
    [Arguments("x/y/\n!x/y/z/")]
    [Arguments("#comment\n\n*.log")]
    [Arguments("  *.log")]
    [Arguments("a*")]
    [Arguments("*c")]
    [Arguments("*b*")]
    [Arguments("*.l?g")]
    [Arguments("d*/s*/c.*")]
    [Arguments("*/*/*.log")]
    [Arguments("*/sub/*")]
    [Arguments("a\\bc")]
    [Arguments("***")]
    [Arguments("a***c")]
    [Arguments("a**c")]
    [Arguments("**c")]
    [Arguments("dir/s*\n!dir/sub")]
    [Arguments("abc*?")]
    [Arguments("dir/*q")]
    [Arguments("a[b-\\")]
    [Arguments("a[[:alpha")]
    [Arguments("dir?sub/c.log")]
    [Arguments("x/*?z")]
    public async Task IgnoresWhatGitIgnores(string gitignore)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Layout(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), gitignore);

        await AgreeWithGit(repo);
    }

    // Case matters where core.ignoreCase is off, as on Linux.
    [Test]
    [Arguments("*.TXT")]
    [Arguments("upper.txt")]
    [Arguments("a[B-D]c")]
    [Arguments("a[X]c")]
    [Arguments("a[[:upper:]]c")]
    [Arguments("[Dd]EBUG/")]
    [Arguments("DIR/*.txt")]
    [Arguments("*X*")]
    public async Task IgnoresWithCaseAsGitDoes(string gitignore)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Layout(scratch);
        CodeCommandsTests.Scratch.Git(repo, "config", "core.ignorecase", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), gitignore);

        await AgreeWithGit(repo);
    }

    // A .gitignore in a folder, for what is in it: a pattern with a slash from there, one without
    // anywhere below it, one anchored to it; a deeper one undoing what one above ignores; and
    // those below a folder ignored, which git never reads. Then info/exclude, and the user's
    // core.excludesFile, each below any .gitignore.
    [Test]
    public async Task IgnoresByEachFolderAndTheUsersFiles()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Layout(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n!x/a.tmp\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "dir", ".gitignore"), "sub/*.txt\n/a.txt\nb.*\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "dir", "sub", ".gitignore"), "!c.log\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "build", ".gitignore"), "!out.dll\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "exclude"), "build/\n*.tmp\nUPPER.TXT\n");
        var excludes = scratch.At("excludes");
        await File.WriteAllTextAsync(excludes, "node_modules/\n!a.txt\nabc\n");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.excludesFile", excludes.Replace('\\', '/'));

        await AgreeWithGit(repo);
    }

    // A .gitignore with a BOM at its start and CRLF line breaks, as Windows editors write one.
    [Test]
    public async Task IgnoresByAFileWithABomAndCrlfs()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Layout(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), (char)0xFEFF + "*.log\r\n!keep.log\r\nbuild/ \r\n");

        await AgreeWithGit(repo);
    }

    // A core.excludesFile that isn't there ignores nothing more, as with git.
    [Test]
    public async Task IgnoresNothingMoreForAnExcludesFileNotThere()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Layout(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.excludesFile", "~/LodeNotThere/ignore");

        await AgreeWithGit(repo);
    }
}
