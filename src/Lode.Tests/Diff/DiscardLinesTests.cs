using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

/// <summary>
/// Lines of an uncommitted change discarded from its diff, as the diff's Discard line and Discard
/// hunk do, run for real against throwaway repositories: what is left on disk, byte for byte,
/// and what went to the Recycle Bin first, here a stand-in.
/// </summary>
public class DiscardLinesTests
{
    static IEnumerable<string> Numbered(int first, int last) =>
        Enumerable.Range(first, last - first + 1).Select(_ => $"line {_}");

    static string Lines(IEnumerable<string> lines) =>
        string.Join('\n', lines) + "\n";

    // Twenty lines changed in two hunks: in the first, the third line changed, the fifth deleted
    // and a line added after the sixth; in the second, the fifteenth changed and the seventeenth
    // deleted.
    static string Changed { get; } = Lines([.. Numbered(1, 2), "line three", "line 4", "line 6", "added", .. Numbered(7, 14), "line fifteen", "line 16", .. Numbered(18, 20)]);

    // A repository whose file.txt is committed as one text and is another on disk, each byte for
    // byte, a character a byte. Git leaves line endings as they are, whatever its config on the
    // machine, unless told otherwise, so what is left is what the test says.
    static string Repo(Scratch scratch, string committed, string changed, string name = "Repo", string autocrlf = "false")
    {
        var repo = scratch.CreateRepo(name);
        Git(repo, "config", "core.autocrlf", autocrlf);
        var path = Path.Combine(repo, "file.txt");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(committed));
        Git(repo, "add", "file.txt");
        Git(repo, "commit", "-m", "Committed");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(changed));
        return repo;
    }

    static string Twenty(Scratch scratch, string name = "Repo") =>
        Repo(scratch, Lines(Numbered(1, 20)), Changed, name);

    // Discards the lines of file.txt whose text is one of those given, as the diff shows them,
    // read as UTF-8, the file going to the stand-in Recycle Bin first.
    static async Task<CodeCommands.RepoResult> Discard(string repo, Bin bin, params string[] texts)
    {
        var source = new DiffSource(repo, new("file.txt", null, GitChange.Modified));
        var diff = (TextDiff) GitFileDiff.Read(source, false, true, Cancel.None);
        var lines = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.IsChange && texts.Contains(_.Text))
            .Select(_ => _.Index!.Value)
            .ToList();
        await Assert.That(lines.Count).IsEqualTo(texts.Length);
        return await GitChanges.DiscardLines(source, diff.PatchHash, lines, bin.Recycle);
    }

    // The file as it is on disk, a character a byte.
    static string OnDisk(string repo) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(repo, "file.txt")));

    // An added line discarded goes, and every other change stays as it was, on disk alone:
    // nothing is staged.
    [Test]
    public async Task AnAddedLine()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);

        var result = await Discard(repo, new(scratch), "+added");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(OnDisk(repo)).IsEqualTo(Lines([.. Numbered(1, 2), "line three", "line 4", .. Numbered(6, 14), "line fifteen", "line 16", .. Numbered(18, 20)]));
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("");
    }

    // A deleted line discarded is put back where it was.
    [Test]
    public async Task ADeletedLineIsPutBack()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);

        var result = await Discard(repo, new(scratch), "-line 5");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(OnDisk(repo)).IsEqualTo(Lines([.. Numbered(1, 2), "line three", .. Numbered(4, 6), "added", .. Numbered(7, 14), "line fifteen", "line 16", .. Numbered(18, 20)]));
    }

    // Lines selected across both hunks, a line changed only partly: the deleted line put back
    // beside the one that took its place, the line that took another's taken away, the line it
    // took the place of still deleted. The second hunk is found where the first left it, a line
    // further down.
    [Test]
    public async Task AMixedSelection()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);

        var result = await Discard(repo, new(scratch), "-line 3", "+line fifteen", "-line 17");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(OnDisk(repo)).IsEqualTo(Lines([.. Numbered(1, 2), "line 3", "line three", "line 4", "line 6", "added", .. Numbered(7, 14), "line 16", .. Numbered(17, 20)]));
    }

    // Each hunk whole, the other left as it is.
    [Test]
    public async Task AHunk()
    {
        using var scratch = new Scratch();
        var first = Twenty(scratch, "First");
        var second = Twenty(scratch, "Second");

        var firstResult = await Discard(first, new(scratch), "-line 3", "+line three", "-line 5", "+added");
        var secondResult = await Discard(second, new(scratch), "-line 15", "+line fifteen", "-line 17");

        await Assert.That(firstResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(first)).IsEqualTo(Lines([.. Numbered(1, 14), "line fifteen", "line 16", .. Numbered(18, 20)]));
        await Assert.That(secondResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(second)).IsEqualTo(Lines([.. Numbered(1, 2), "line three", "line 4", "line 6", "added", .. Numbered(7, 20)]));
    }

    // The file as it was goes to the Recycle Bin before the lines are discarded, so it can be got
    // back: the one there has every change.
    [Test]
    public async Task TheFileGoesToTheRecycleBinFirst()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);
        var bin = new Bin(scratch);

        var result = await Discard(repo, bin, "+added");

        await Assert.That(result.Output).IsEqualTo("Moved file.txt as it was to the Recycle Bin");
        await Assert.That(bin.Recycled).IsEquivalentTo(["Repo/file.txt"]);
        await Assert.That(bin.Read("Repo/file.txt")).IsEqualTo(Changed);
    }

    // The last line committed had no newline, and lines were added after it, so it gained one.
    // Its deletion put back without what was added after it kept, it gains the newline all the
    // same, as they can't follow it otherwise, where git would join it to the first of them.
    [Test]
    public async Task NoNewlineAtTheEndCommitted()
    {
        (string[] Discarded, string Left)[] cases =
        [
            (["+more"], "original\n"),
            (["-original"], "original\noriginal\nmore\n"),
            (["-original", "+original"], "original\nmore\n"),
            (["-original", "+original", "+more"], "original")
        ];
        foreach (var (discarded, left) in cases)
        {
            using var scratch = new Scratch();
            var repo = Repo(scratch, "original", "original\nmore\n");

            var result = await Discard(repo, new(scratch), discarded);

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(OnDisk(repo)).IsEqualTo(left);
        }
    }

    // Neither the file committed nor the file as it is ends with a newline.
    [Test]
    public async Task NoNewlineAtTheEndOfEither()
    {
        (string[] Discarded, string Left)[] cases =
        [
            (["-two"], "one\ntwo\nchanged"),
            (["+changed"], "one\n"),
            (["-two", "+changed"], "one\ntwo")
        ];
        foreach (var (discarded, left) in cases)
        {
            using var scratch = new Scratch();
            var repo = Repo(scratch, "one\ntwo", "one\nchanged");

            var result = await Discard(repo, new(scratch), discarded);

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(OnDisk(repo)).IsEqualTo(left);
        }
    }

    // A file with Windows line endings, committed with them: each line in the diff has its
    // carriage return, and keeps it, the line put back with it too.
    [Test]
    public async Task WindowsLineEndings()
    {
        using var scratch = new Scratch();
        var added = Repo(scratch, "one\r\ntwo\r\nthree\r\n", "one\r\n2\r\nthree\r\nfour\r\n", "Added");
        var deleted = Repo(scratch, "one\r\ntwo\r\nthree\r\n", "one\r\n2\r\nthree\r\nfour\r\n", "Deleted");

        var addedResult = await Discard(added, new(scratch), "+four\r");
        var deletedResult = await Discard(deleted, new(scratch), "-two\r");

        await Assert.That(addedResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(added)).IsEqualTo("one\r\n2\r\nthree\r\n");
        await Assert.That(deletedResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(deleted)).IsEqualTo("one\r\ntwo\r\n2\r\nthree\r\nfour\r\n");
    }

    // With core.autocrlf on, as git for Windows has it unless told otherwise, the file committed
    // with Newlines alone, as git commits it, and on disk with Windows line endings: git's diff
    // has no carriage returns, and the file keeps its own.
    [Test]
    public async Task WindowsLineEndingsGitConverts()
    {
        using var scratch = new Scratch();
        var repo = Repo(scratch, "one\r\ntwo\r\nthree\r\n", "one\r\n2\r\nthree\r\nfour\r\n", autocrlf: "true");

        var result = await Discard(repo, new(scratch), "+four", "-two");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(OnDisk(repo)).IsEqualTo("one\r\ntwo\r\n2\r\nthree\r\n");
    }

    // What a diff read as UTF-8 shows for a byte that isn't UTF-8: U+FFFD.
    const char replacement = (char) 0xFFFD;

    // A file in Windows-1252, not UTF-8: é the byte E9, as Latin-1 has it too. A line put back
    // has the bytes it had, not EF BF BD, U+FFFD in UTF-8, and a line kept, as context, matches
    // the file, as U+FFFD it wouldn't.
    [Test]
    public async Task NotUtf8()
    {
        using var scratch = new Scratch();
        var putBack = Repo(scratch, "café\nthé\nold\n", "café\nnew\nmore\n", "PutBack");
        var kept = Repo(scratch, "café\nthé\nold\n", "café\nnew\nmore\n", "Kept");

        var putBackResult = await Discard(putBack, new(scratch), $"-th{replacement}");
        var keptResult = await Discard(kept, new(scratch), "+more");

        await Assert.That(putBackResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(putBack)).IsEqualTo("café\nthé\nnew\nmore\n");
        await Assert.That(keptResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(kept)).IsEqualTo("café\nnew\n");
    }

    // A file with a change staged, the third line, and the rest not: the diff is against the
    // last commit, of the file on disk, so either is discarded from disk, and what is staged
    // stays staged, discarded or not.
    [Test]
    public async Task APartlyStagedFile()
    {
        using var scratch = new Scratch();
        var staged = Lines([.. Numbered(1, 2), "line three", .. Numbered(4, 20)]);
        var notStaged = Twenty(scratch, "NotStaged");
        var wasStaged = Twenty(scratch, "WasStaged");
        foreach (var repo in (string[]) [notStaged, wasStaged])
        {
            var path = Path.Combine(repo, "file.txt");
            await File.WriteAllTextAsync(path, staged);
            Git(repo, "add", "file.txt");
            await File.WriteAllTextAsync(path, Changed);
        }

        var notStagedResult = await Discard(notStaged, new(scratch), "+added", "-line 17");
        var wasStagedResult = await Discard(wasStaged, new(scratch), "-line 3", "+line three");

        await Assert.That(notStagedResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(notStaged)).IsEqualTo(Lines([.. Numbered(1, 2), "line three", "line 4", .. Numbered(6, 14), "line fifteen", .. Numbered(16, 20)]));
        await Assert.That(Git(notStaged, "show", ":file.txt")).IsEqualTo(staged.TrimEnd());
        await Assert.That(wasStagedResult.Succeeded).IsTrue();
        await Assert.That(OnDisk(wasStaged)).IsEqualTo(Lines([.. Numbered(1, 4), "line 6", "added", .. Numbered(7, 14), "line fifteen", "line 16", .. Numbered(18, 20)]));
        await Assert.That(Git(wasStaged, "show", ":file.txt")).IsEqualTo(staged.TrimEnd());
    }

    // The file changed after its diff was read: which lines were meant can't be told, so nothing
    // is discarded, and nothing goes to the Recycle Bin.
    [Test]
    public async Task ChangedSinceItsDiffWasRead()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);
        var bin = new Bin(scratch);
        var source = new DiffSource(repo, new("file.txt", null, GitChange.Modified));
        var diff = (TextDiff) GitFileDiff.Read(source, false, true, Cancel.None);
        var added = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.Text == "+added")
            .Select(_ => _.Index!.Value)
            .ToList();
        await File.AppendAllTextAsync(Path.Combine(repo, "file.txt"), "more\n");

        var result = await GitChanges.DiscardLines(source, diff.PatchHash, added, bin.Recycle);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("file.txt changed since its diff was read, so nothing was discarded.");
        await Assert.That(bin.Recycled).IsEmpty();
        await Assert.That(OnDisk(repo)).IsEqualTo(Changed + "more\n");
    }

    // The file couldn't go to the Recycle Bin, in use say: nothing is discarded, so nothing is
    // lost.
    [Test]
    public async Task WithoutTheRecycleBin()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);
        var source = new DiffSource(repo, new("file.txt", null, GitChange.Modified));
        var diff = (TextDiff) GitFileDiff.Read(source, false, true, Cancel.None);
        var added = diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.Text == "+added")
            .Select(_ => _.Index!.Value)
            .ToList();

        var result = await GitChanges.DiscardLines(source, diff.PatchHash, added, _ => Task.FromResult(false));

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("file.txt couldn't go to the Recycle Bin, so nothing was discarded.");
        await Assert.That(OnDisk(repo)).IsEqualTo(Changed);
    }

    // Lines left out of the commit stay left out once others are discarded, each kept as it was,
    // in the diff read again; where git pairs the lines left otherwise, which is which can't be
    // told, so none are kept.
    [Test]
    public async Task PicksStayAsTheyWere()
    {
        using var scratch = new Scratch();
        var repo = Twenty(scratch);
        var source = new DiffSource(repo, new("file.txt", null, GitChange.Modified));
        var before = (TextDiff) GitFileDiff.Read(source, false, true, Cancel.None);
        var picks = LinePicks.All(before).With(Indexes(before, "-line 15", "+line fifteen"), false);
        var added = Indexes(before, "+added").ToHashSet();
        await GitChanges.DiscardLines(source, before.PatchHash, added, new Bin(scratch).Recycle);
        var after = (TextDiff) GitFileDiff.Read(source, false, true, Cancel.None);

        var kept = picks.AfterDiscarding(before, added, after)!;

        await Assert.That(kept.PatchHash).IsEqualTo(after.PatchHash);
        await Assert.That(kept.State).IsNull();
        var unpicked = after.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _ is { IsChange: true, Index: { } index } && !kept.IsPicked(index))
            .Select(_ => _.Text);
        await Assert.That(unpicked).IsEquivalentTo(["-line 15", "+line fifteen"], CollectionOrdering.Matching);

        using var paired = new Scratch();
        var pairedRepo = Repo(paired, "original", "original\nmore\n");
        var pairedSource = new DiffSource(pairedRepo, new("file.txt", null, GitChange.Modified));
        var pairedBefore = (TextDiff) GitFileDiff.Read(pairedSource, false, true, Cancel.None);
        var deleted = Indexes(pairedBefore, "-original").ToHashSet();
        var pairedPicks = LinePicks.All(pairedBefore).With(Indexes(pairedBefore, "+more"), false);
        await GitChanges.DiscardLines(pairedSource, pairedBefore.PatchHash, deleted, new Bin(paired).Recycle);
        var pairedAfter = (TextDiff) GitFileDiff.Read(pairedSource, false, true, Cancel.None);

        await Assert.That(pairedPicks.AfterDiscarding(pairedBefore, deleted, pairedAfter)).IsNull();
    }

    static IEnumerable<int> Indexes(TextDiff diff, params string[] texts) =>
        diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _.IsChange && texts.Contains(_.Text))
            .Select(_ => _.Index!.Value);

    // Only an uncommitted change to a file git tracks, changed in place, whose diff is of the
    // file on disk: not a file git doesn't track, one new in the index and edited since, one
    // deleted or renamed, nor one in conflict, nor with whitespace hidden, part way through a
    // merge, before the first commit, or a commit's.
    [Test]
    public async Task WhereLinesCanBeDiscarded()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "gone.txt"), "gone\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "old.txt"), Lines(Numbered(1, 10)));
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "More");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed\n");
        File.Delete(Path.Combine(repo, "gone.txt"));
        Git(repo, "mv", "old.txt", "renamed.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "renamed.txt"), Lines([.. Numbered(1, 9), "ten"]));
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged\n");
        Git(repo, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged, edited\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "new\n");
        var files = GitChanges.Read(repo, Cancel.None)!.Files;

        TextDiff DiffOf(Change change) =>
            (TextDiff) GitFileDiff.Read(new(repo, change), false, true, Cancel.None);

        var discardable = files
            .Select(_ => $"{_.Path}: {DiffView.CanDiscard(new(repo, _), DiffOf(_), hideWhitespace: false)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        await Assert.That(discardable).IsEquivalentTo(
            [
                "gone.txt: False",
                "new.txt: False",
                "renamed.txt: False",
                "staged.txt: False",
                "tracked.txt: True"
            ],
            CollectionOrdering.Matching);
        var tracked = files.Single(_ => _.Path == "tracked.txt");
        var diff = DiffOf(tracked);
        var commit = new GitCommit("abc", [], "Ada", DateTimeOffset.UnixEpoch, [], "Subject");
        await Assert.That(DiffView.CanDiscard(new(repo, tracked), diff, hideWhitespace: true)).IsFalse();
        await Assert.That(DiffView.CanDiscard(new(repo, tracked, Merging: true), diff, hideWhitespace: false)).IsFalse();
        await Assert.That(DiffView.CanDiscard(new(repo, tracked, HasCommits: false), diff, hideWhitespace: false)).IsFalse();
        await Assert.That(DiffView.CanDiscard(new(repo, tracked, commit), diff, hideWhitespace: false)).IsFalse();
        await Assert.That(DiffView.CanDiscard(new(repo, tracked with { Kind = GitChange.Conflicted }), diff, hideWhitespace: false)).IsFalse();
    }
}
