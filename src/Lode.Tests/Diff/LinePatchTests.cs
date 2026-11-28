using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

public class LinePatchTests
{
    // Twenty lines, the third and fifteenth changed: two hunks.
    static string TwentyLines(params (int Line, string Text)[] changed)
    {
        var lines = Enumerable.Range(1, 20).Select(_ => $"line {_}").ToArray();
        foreach (var (line, text) in changed)
        {
            lines[line - 1] = text;
        }

        return string.Join('\n', lines) + "\n";
    }

    static string Repo(Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        File.WriteAllText(Path.Combine(repo, "file.txt"), TwentyLines());
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Twenty");
        File.WriteAllText(Path.Combine(repo, "file.txt"), TwentyLines((3, "line three"), (15, "line fifteen")));
        return repo;
    }

    static TextDiff Diff(string repo, Change change, bool hasCommits = true) =>
        (TextDiff) GitFileDiff.Read(new(repo, change, HasCommits: hasCommits), false, true, Cancel.None);

    static IEnumerable<int> Indexes(TextDiff diff, params string[] texts) =>
        diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => texts.Contains(_.Text))
            .Select(_ => _.Index!.Value);

    // Applies the patch to the index, as the commit's index has it applied, and gives what the
    // index then has of the file.
    static string Apply(string repo, string patch, string path)
    {
        var file = Path.Combine(repo, "..", $"{Guid.NewGuid():N}.patch");
        File.WriteAllText(file, patch);
        Git(repo, "apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", file);
        return Git(repo, "show", $":{path}");
    }

    // As Apply, but what the index then has of the file byte for byte, its last newline or none
    // with it, where Git trims what git writes.
    static string ApplyExactly(string repo, string patch, string path)
    {
        Apply(repo, patch, path);
        var file = Path.Combine(repo, "..", $"{Guid.NewGuid():N}.staged");
        CodeCommands.GitToFile(repo, file, Cancel.None, "cat-file", "blob", $":{path}");
        return File.ReadAllText(file);
    }

    // The second hunk alone: the first left out, as nothing in it is picked.
    [Test]
    public async Task OneHunk()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var change = new Change("file.txt", null, GitChange.Modified);
        var diff = Diff(repo, change);
        var picks = LinePicks.All(diff).With(Indexes(diff, "-line 3", "+line three"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(patch).DoesNotContain("line three");
        await Assert.That(Apply(repo, patch, "file.txt")).IsEqualTo(TwentyLines((15, "line fifteen")).TrimEnd());
    }

    // The first hunk alone, which moves nothing after it.
    [Test]
    public async Task FirstHunk()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var change = new Change("file.txt", null, GitChange.Modified);
        var diff = Diff(repo, change);
        var picks = LinePicks.All(diff).With(Indexes(diff, "-line 15", "+line fifteen"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(Apply(repo, patch, "file.txt")).IsEqualTo(TwentyLines((3, "line three")).TrimEnd());
    }

    // An added line picked without the deleted one it replaced: both are in.
    [Test]
    public async Task AddedNotDeleted()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var change = new Change("file.txt", null, GitChange.Modified);
        var diff = Diff(repo, change);
        var picks = LinePicks.All(diff).With(Indexes(diff, "-line 3", "-line 15", "+line fifteen"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        var lines = Apply(repo, patch, "file.txt").Split('\n');
        await Assert.That(lines.Take(5)).IsEquivalentTo(["line 1", "line 2", "line 3", "line three", "line 4"]);
        await Assert.That(lines.Length).IsEqualTo(21);
    }

    // Nothing picked, no patch.
    [Test]
    public async Task NonePicked()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch);
        var diff = Diff(repo, new("file.txt", null, GitChange.Modified));
        var picks = LinePicks.All(diff).With(LinePicks.Pickable(diff), false);

        await Assert.That(picks.State).IsFalse();
        await Assert.That(LinePatch.Format(diff, picks, deleted: false)).IsNull();
    }

    // Some lines of a file git doesn't track: a new file of those lines.
    [Test]
    public async Task NewFile()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "one\ntwo\nthree\n");
        var diff = Diff(repo, new("new.txt", null, GitChange.Untracked));
        var picks = LinePicks.All(diff).With(Indexes(diff, "+two"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(Apply(repo, patch, "new.txt")).IsEqualTo("one\nthree");
    }

    // Some lines of a deleted file: those go, the file stays.
    [Test]
    public async Task DeletedFile()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "one\ntwo\nthree\n");
        Git(repo, "commit", "-am", "Three");
        File.Delete(Path.Combine(repo, "tracked.txt"));
        var diff = Diff(repo, new("tracked.txt", null, GitChange.Deleted));
        var picks = LinePicks.All(diff).With(Indexes(diff, "-one", "-three"), false);

        var patch = LinePatch.Format(diff, picks, deleted: true)!;

        await Assert.That(patch).DoesNotContain("deleted file");
        await Assert.That(Apply(repo, patch, "tracked.txt")).IsEqualTo("one\nthree");
    }

    // The last line had no newline after it: the marker goes with the line it marks.
    [Test]
    public async Task NoNewlineAtEnd()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "original\nmore\n");
        var diff = Diff(repo, new("tracked.txt", null, GitChange.Modified));
        var picks = LinePicks.All(diff).With(Indexes(diff, "+more"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(patch).Contains("\\ No newline at end of file");
        await Assert.That(Apply(repo, patch, "tracked.txt")).IsEqualTo("original");
        await Assert.That(Git(repo, "cat-file", "-s", ":tracked.txt")).IsEqualTo("9");
    }

    // Lines added after a last line that had no newline, picked without the newline it gained:
    // it gains it all the same, as the lines picked can't follow it otherwise, where git would
    // join it to the first of them.
    [Test]
    public async Task AddedAfterNoNewline()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "original\nmore\nlast\n");
        var diff = Diff(repo, new("tracked.txt", null, GitChange.Modified));
        var picks = LinePicks.All(diff).With(Indexes(diff, "-original", "+original"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(ApplyExactly(repo, patch, "tracked.txt")).IsEqualTo("original\nmore\nlast\n");
    }

    // As above, the last line changed too, and its new text not picked: the line stays as it
    // was, gaining the newline, and the last line added has none, as the file has none.
    [Test]
    public async Task AddedAfterNoNewlineChanged()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed\nmore");
        var diff = Diff(repo, new("tracked.txt", null, GitChange.Modified));
        var picks = LinePicks.All(diff).With(Indexes(diff, "-original", "+changed"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(ApplyExactly(repo, patch, "tracked.txt")).IsEqualTo("original\nmore");
    }

    // Nothing added after a last line that had no newline: it stays as it was, without one.
    [Test]
    public async Task NothingAddedAfterNoNewline()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "first\noriginal");
        Git(repo, "commit", "-am", "First");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "original\nmore\n");
        var diff = Diff(repo, new("tracked.txt", null, GitChange.Modified));
        var picks = LinePicks.All(diff).With(Indexes(diff, "-original", "+original", "+more"), false);

        var patch = LinePatch.Format(diff, picks, deleted: false)!;

        await Assert.That(ApplyExactly(repo, patch, "tracked.txt")).IsEqualTo("original");
    }

    // Picks keep to all or none once every line is picked alike, and say how the tick shows.
    [Test]
    public async Task States()
    {
        var diff = (TextDiff) DiffParser.Parse("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n");
        var all = LinePicks.All(diff);

        await Assert.That(all.State).IsTrue();
        var some = all.With([0], false);
        await Assert.That(some.State).IsNull();
        await Assert.That(some.IsPicked(0)).IsFalse();
        await Assert.That(some.IsPicked(1)).IsTrue();
        var none = some.With([1], false);
        await Assert.That(none.State).IsFalse();
        await Assert.That(none.AllByDefault).IsFalse();
        await Assert.That(none.Diverging).IsEmpty();
        await Assert.That(none.With([0, 1], true)).IsEqualTo(all with { Diverging = all.Diverging });
    }
}
