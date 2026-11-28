using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

public class PartialCommitTests
{
    static string Lines(params string[] lines) =>
        string.Join('\n', lines) + "\n";

    // A file of twenty lines committed, its third and fifteenth changed, another file's change
    // staged, and a file git doesn't track.
    static string Repo(Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        File.WriteAllText(Path.Combine(repo, "file.txt"), Twenty(false));
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Twenty");
        File.WriteAllText(Path.Combine(repo, "file.txt"), Twenty(true));
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged, changed\n");
        Git(repo, "add", "staged.txt");
        File.WriteAllText(Path.Combine(repo, "untracked.txt"), "new\n");
        return repo;
    }

    static string Twenty(bool changed)
    {
        var lines = Enumerable.Range(1, 20).Select(_ => $"line {_}").ToArray();
        if (changed)
        {
            lines[2] = "line three";
            lines[14] = "line fifteen";
        }

        return Lines(lines);
    }

    // The fifteenth line's change picked, not the third's.
    static PartialChange Partial(string repo)
    {
        var change = new Change("file.txt", null, GitChange.Modified);
        var diff = (TextDiff) GitFileDiff.Read(new(repo, change), false, true, Cancel.None);
        var unpicked = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.Text is "-line 3" or "+line three")
            .Select(_ => _.Index!.Value);
        var picks = LinePicks.All(diff).With(unpicked, false);
        return new(change, LinePatch.Format(diff, picks, deleted: false)!);
    }

    // Only the lines picked go in, with the whole file ticked beside them; the rest stay
    // changed; what else was staged stays staged.
    [Test]
    public async Task CommitsThePickedLines()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);

        var result = GitChanges.Commit(repo, [new("untracked.txt", null, GitChange.Untracked)], false, "Some lines", Cancel.None, [Partial(repo)]);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "log", "-1", "--format=%s")).IsEqualTo("Some lines");
        await Assert.That(Git(repo, "show", "--name-only", "--format=", "HEAD").Split('\n')).IsEquivalentTo(["file.txt", "untracked.txt"]);
        var committed = Git(repo, "show", "HEAD:file.txt");
        await Assert.That(committed).Contains("line fifteen");
        await Assert.That(committed).Contains("line 3\n");
        await Assert.That(Git(repo, "diff", "--name-only")).IsEqualTo("file.txt");
        await Assert.That(Git(repo, "diff", "-U0", "file.txt")).Contains("+line three");
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "file.txt"))).IsEqualTo(Twenty(true));
    }

    // Amending, the lines picked go in the last commit, in its place; the rest stay changed, and
    // what else was staged stays staged.
    [Test]
    public async Task AmendsWithThePickedLines()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var parent = Git(repo, "rev-parse", "HEAD^");

        var result = GitChanges.Commit(repo, [], false, "Twenty, one changed", Cancel.None, [Partial(repo)], amend: true);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-parse", "HEAD^")).IsEqualTo(parent);
        await Assert.That(Git(repo, "log", "-1", "--format=%s")).IsEqualTo("Twenty, one changed");
        await Assert.That(Git(repo, "show", "--name-only", "--format=", "HEAD").Split('\n')).IsEquivalentTo(["file.txt", "staged.txt"]);
        var committed = Git(repo, "show", "HEAD:file.txt");
        await Assert.That(committed).Contains("line fifteen");
        await Assert.That(committed).Contains("line 3\n");
        await Assert.That(Git(repo, "show", "HEAD:staged.txt")).IsEqualTo("staged");
        await Assert.That(Git(repo, "diff", "--name-only")).IsEqualTo("file.txt");
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
    }

    // A hook turning the commit down leaves the real index as it was.
    [Test]
    public async Task HookRefuses()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var head = Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");

        var result = GitChanges.Commit(repo, [], false, "Refused", Cancel.None, [Partial(repo)]);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
        await Assert.That(Git(repo, "diff", "--name-only")).IsEqualTo("file.txt");
    }

    // Before the first commit: from an empty index.
    [Test]
    public async Task FirstCommit()
    {
        using var scratch = new Scratch();
        var repo = scratch.At("Empty");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, "first.txt"), Lines("one", "two", "three"));
        var change = new Change("first.txt", null, GitChange.Untracked);
        var diff = (TextDiff) GitFileDiff.Read(new(repo, change, HasCommits: false), false, true, Cancel.None);
        var two = diff.Hunks.SelectMany(_ => _.Lines).Where(_ => _.Text == "+two").Select(_ => _.Index!.Value);
        var patch = LinePatch.Format(diff, LinePicks.All(diff).With(two, false), deleted: false)!;

        var result = GitChanges.Commit(repo, [], false, "First", Cancel.None, [new(change, patch)]);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "show", "HEAD:first.txt")).IsEqualTo("one\nthree");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("M first.txt");
    }

    // The file changed after its lines were picked: nothing goes in, and it is given back to be
    // ticked whole again.
    [Test]
    public async Task ChangedAfterPicking()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var head = Git(repo, "rev-parse", "HEAD");
        var change = new Change("file.txt", null, GitChange.Modified);
        var diff = (TextDiff) GitFileDiff.Read(new(repo, change), false, true, Cancel.None);
        var unpicked = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.Text is "-line 3" or "+line three")
            .Select(_ => _.Index!.Value);
        var picks = LinePicks.All(diff).With(unpicked, false);
        await File.AppendAllTextAsync(Path.Combine(repo, "file.txt"), "more\n");

        var (result, stale) = ChangesPane.CommitPicked(repo, [], [(change, picks)], true, false, "Stale", null, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(stale).IsEqualTo(change);
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
    }

    // What a diff read as UTF-8 shows for a byte that isn't UTF-8: U+FFFD.
    const char replacement = (char) 0xFFFD;

    // A file in Windows-1252, not UTF-8, committed, then changed: é the byte E9, as Latin-1 has
    // it too.
    static string NotUtf8Repo(Scratch scratch, string committed, string changed)
    {
        var repo = scratch.CreateRepo("Repo");
        var path = Path.Combine(repo, "latin.txt");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(committed));
        Git(repo, "add", "latin.txt");
        Git(repo, "commit", "-m", "Latin");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(changed));
        return repo;
    }

    // Commits the lines of latin.txt picked as they are in its diff shown, which has U+FFFD for
    // each byte that isn't UTF-8, all but those given, as the Changes pane commits them, and
    // gives the file as the commit has it, a character a byte, as Latin-1 reads them.
    static async Task<string> CommitPicked(string repo, params string[] unpicked)
    {
        var change = new Change("latin.txt", null, GitChange.Modified);
        var diff = (TextDiff) GitFileDiff.Read(new(repo, change), false, true, Cancel.None);
        var indexes = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => unpicked.Contains(_.Text))
            .Select(_ => _.Index!.Value);
        var picks = LinePicks.All(diff).With(indexes, false);

        var (result, stale) = ChangesPane.CommitPicked(repo, [], [(change, picks)], true, false, "Picked", null, Cancel.None);

        await Assert.That(stale).IsNull();
        await Assert.That(result.Succeeded).IsTrue();
        var file = Path.Combine(repo, "..", $"{Guid.NewGuid():N}.committed");
        CodeCommands.GitToFile(repo, file, Cancel.None, "cat-file", "blob", "HEAD:latin.txt");
        return Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file));
    }

    // A line added to a file that isn't UTF-8 goes in as the file has it, not as EF BF BD,
    // U+FFFD in UTF-8, which git took without a word.
    [Test]
    public async Task AddedNotUtf8()
    {
        using var scratch = new Scratch();
        var repo = NotUtf8Repo(scratch, "one\ntwo\n", "one\ncafé\ntwo\nmore\n");

        var committed = await CommitPicked(repo, "+more");

        await Assert.That(committed).IsEqualTo("one\ncafé\ntwo\n");
    }

    // Lines kept in a file that isn't UTF-8, one there before and one deleted but not picked,
    // match the file as committed before, which, as U+FFFD, they didn't, so nothing went in.
    [Test]
    public async Task KeptNotUtf8()
    {
        using var scratch = new Scratch();
        var repo = NotUtf8Repo(scratch, "café\nthé\nold\n", "café\nnew\n");

        var committed = await CommitPicked(repo, $"-th{replacement}");

        await Assert.That(committed).IsEqualTo("café\nthé\nnew\n");
    }
}
