using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

public class GitFileDiffTests
{
    // Against the last commit: the committed line, which had no newline after it, deleted, and
    // the new one added.
    [Test]
    public async Task Modified()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed\n");

        var diff = Read(repo, new("tracked.txt", null, GitChange.Modified));

        var lines = ((TextDiff) diff).Hunks.Single().Lines;
        await Assert.That(DiffParserTests.Describe(lines)).IsEquivalentTo(["0 Delete 1 _ original", "1 Add _ 1 changed"]);
        await Assert.That(lines[0].NoTrailingNewline).IsTrue();
        await Assert.That(lines[1].NoTrailingNewline).IsFalse();
    }

    // Against nothing, as git has no version of it: git diff --no-index ends with 1, as the two
    // differ, which is no failure.
    [Test]
    public async Task Untracked()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "new file.txt"), "one\ntwo\n");

        var diff = Read(repo, new("new file.txt", null, GitChange.Untracked));

        var lines = ((TextDiff) diff).Hunks.Single().Lines;
        await Assert.That(DiffParserTests.Describe(lines)).IsEquivalentTo(["0 Add _ 1 one", "1 Add _ 2 two"]);
    }

    // Before the first commit, everything is new.
    [Test]
    public async Task NoCommitsYet()
    {
        using var scratch = new Scratch();
        var repo = scratch.At("Empty");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "one\n");
        Git(repo, "add", ".");

        var diff = GitFileDiff.Read(new(repo, new("staged.txt", null, GitChange.Added), HasCommits: false), false, false, Cancel.None);

        await Assert.That(((TextDiff) diff).Added).IsEqualTo(1);
    }

    [Test]
    public async Task Deleted()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        File.Delete(Path.Combine(repo, "tracked.txt"));

        var diff = Read(repo, new("tracked.txt", null, GitChange.Deleted));

        await Assert.That(((TextDiff) diff).Deleted).IsEqualTo(1);
        await Assert.That(GitFileDiff.NewLines(new(repo, new("tracked.txt", null, GitChange.Deleted)))).IsNull();
    }

    // Moved and not changed: nothing to show. Moved and changed: what changed.
    [Test]
    public async Task Renamed()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "one\ntwo\nthree\nfour\nfive\n");
        Git(repo, "commit", "-am", "Longer");
        Git(repo, "mv", "tracked.txt", "moved.txt");
        var change = new Change("moved.txt", "tracked.txt", GitChange.Renamed);

        await Assert.That(Read(repo, change)).IsTypeOf<EmptyDiff>();

        await File.WriteAllTextAsync(Path.Combine(repo, "moved.txt"), "one\ntwo\nthree\nfour\nfive!\n");
        var diff = (TextDiff) Read(repo, change);
        await Assert.That(diff.Added).IsEqualTo(1);
        await Assert.That(diff.Deleted).IsEqualTo(1);
    }

    // Spaces changed alone are nothing once whitespace is hidden.
    [Test]
    public async Task HideWhitespace()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "  original");
        var source = new DiffSource(repo, new("tracked.txt", null, GitChange.Modified));

        await Assert.That(GitFileDiff.Read(source, false, false, Cancel.None)).IsTypeOf<TextDiff>();
        await Assert.That(GitFileDiff.Read(source, true, false, Cancel.None)).IsTypeOf<EmptyDiff>();
    }

    // What a commit changed, against the commit before; the first commit's, against nothing; a
    // merge's against its first parent.
    [Test]
    public async Task Commits()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "second");
        Git(repo, "commit", "-am", "Second");
        Git(repo, "switch", "-q", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(repo, "side.txt"), "side\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        Git(repo, "merge", "--no-ff", "-m", "Merge side", "side");
        var commits = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;
        var tracked = new Change("tracked.txt", null, GitChange.Modified);

        var second = (TextDiff) GitFileDiff.Read(new(repo, tracked, commits.Single(_ => _.Subject == "Second")), false, false, Cancel.None);
        await Assert.That(second.Hunks.Single().Lines.Select(_ => _.Text)).IsEquivalentTo(["-original", "+second"]);

        var first = (TextDiff) GitFileDiff.Read(new(repo, tracked with { Kind = GitChange.Added }, commits.Single(_ => _.Subject == "Initial")), false, false, Cancel.None);
        await Assert.That(first.Hunks.Single().Lines.Select(_ => _.Text)).IsEquivalentTo(["+original"]);

        var merge = (TextDiff) GitFileDiff.Read(new(repo, new("side.txt", null, GitChange.Added), commits.Single(_ => _.Subject == "Merge side")), false, false, Cancel.None);
        await Assert.That(merge.Added).IsEqualTo(1);

        var newLines = GitFileDiff.NewLines(new(repo, tracked, commits.Single(_ => _.Subject == "Second")));
        await Assert.That(newLines).IsEquivalentTo(["second"]);
    }

    // With git turning Windows line endings to Unix ones going in, the diff is of what it keeps;
    // the lines to expand with are the file's, without their carriage returns.
    [Test]
    public async Task WindowsLineEndings()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "core.autocrlf", "true");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "one\r\ntwo\r\nthree\r\n");
        Git(repo, "commit", "-am", "Lines");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "one\r\ntwo!\r\nthree\r\n");
        var source = new DiffSource(repo, new("tracked.txt", null, GitChange.Modified));

        var diff = (TextDiff) GitFileDiff.Read(source, false, false, Cancel.None);

        await Assert.That(diff.Hunks.Single().Lines.Select(_ => _.Text)).IsEquivalentTo([" one", "-two", "+two!", " three"]);
        await Assert.That(GitFileDiff.NewLines(source)).IsEquivalentTo(["one", "two!", "three"]);
    }

    // An image git calls binary, as it was and as it is.
    [Test]
    public async Task Image()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        var path = Path.Combine(repo, "picture.png");
        WritePng(path, Color.Red);
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Picture");
        WritePng(path, Color.Blue);

        var diff = Read(repo, new("picture.png", null, GitChange.Modified));

        var image = (ImageDiff) diff;
        await Assert.That(image.Old).IsNotNull();
        await Assert.That(image.New).IsEquivalentTo(await File.ReadAllBytesAsync(path));

        var added = (ImageDiff) Read(repo, new("picture.png", null, GitChange.Untracked));
        await Assert.That(added.Old).IsNull();
    }

    // Binary, but not an image GDI+ draws.
    [Test]
    public async Task Binary()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        File.WriteAllBytes(Path.Combine(repo, "data.bin"), [0, 1, 2, 0, 3]);

        await Assert.That(Read(repo, new("data.bin", null, GitChange.Untracked))).IsTypeOf<BinaryDiff>();
    }

    static void WritePng(string path, Color color)
    {
        using var bitmap = new Bitmap(4, 3);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    static FileDiff Read(string repo, Change change) =>
        GitFileDiff.Read(new(repo, change), false, false, Cancel.None);
}
