using static CodeCommandsTests;

/// <summary>
/// Changes' stashes, read, restored, discarded and made for real in throwaway repositories in
/// the temp folder.
/// </summary>
public class GitStashesTests
{
    static string Read(string repo, string file) =>
        File.ReadAllText(Path.Combine(repo, file));

    static void Write(string repo, string file, string text) =>
        File.WriteAllText(Path.Combine(repo, file), text);

    static string Stashes(string repo) =>
        Scratch.Git(repo, "stash", "list", "--format=%gs");

    static string Describe(IEnumerable<GitStashes.Stash> stashes) =>
        string.Join('\n', stashes.Select(_ => $"{_.Name} on {_.Branch ?? "no branch"}: {_.Text}"));

    // One line of stash list's output: the stash's name, its commit, when it was made, and git's
    // subject for it, each after a NUL.
    static string Line(string name, string commit, long made, string subject) =>
        string.Join('\0', name, commit, made.ToString(CultureInfo.InvariantCulture), subject);

    // Each stash as git names it, with its commit, when it was made, and the branch it was made
    // on; said as its message says it, Lode's own, left on a branch switched off, as what they
    // are, and one made without a message as the commit it was made on.
    [Test]
    public async Task Parse()
    {
        var output = string.Join(
            '\n',
            Line("stash@{0}", "a1", 1_700_000_000, "On main: their own"),
            Line("stash@{1}", "b2", 1_700_000_100, "WIP on feature: abc1234 Add the thing"),
            Line("stash@{2}", "c3", 1_700_000_200, "On feature: Lode:feature"),
            Line("stash@{3}", "d4", 1_700_000_300, "On fix/login: Lode clashed:fix/login"),
            Line("stash@{4}", "e5", 1_700_000_400, "On (no branch): detached: and more"),
            Line("stash@{5}", "f6", 1_700_000_500, "Created via \"git stash store\"."),
            "");

        var stashes = GitStashes.Parse(output);

        await Assert.That(Describe(stashes)).IsEqualTo(
            """
            stash@{0} on main: their own
            stash@{1} on feature: Work in progress on abc1234 Add the thing
            stash@{2} on feature: Changes left on feature
            stash@{3} on fix/login: Changes left on fix/login that clashed coming back
            stash@{4} on no branch: detached: and more
            stash@{5} on no branch: Created via "git stash store".
            """.ReplaceLineEndings("\n"));
        await Assert.That(stashes.Select(_ => _.Commit)).IsEquivalentTo(["a1", "b2", "c3", "d4", "e5", "f6"]);
        await Assert.That(stashes[1].Made).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_700_000_100));
    }

    // Lode's, left on a branch switched off, and those made in a terminal, with a message and
    // without, newest first. None before anything is stashed.
    [Test]
    public async Task ReadsTheStashes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await Assert.That(GitStashes.Read(repo, Cancel.None)).IsEmpty();

        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "left");
        GitBranches.Switch(repo, "feature", new("main", false, null, false), leave: true, Cancel.None);
        Write(repo, "tracked.txt", "theirs");
        Scratch.Git(repo, "stash", "push", "-m", "their own");
        Write(repo, "tracked.txt", "in progress");
        Scratch.Git(repo, "stash");
        var head = Scratch.Git(repo, "rev-parse", "--short", "HEAD");

        var stashes = GitStashes.Read(repo, Cancel.None);

        await Assert.That(Describe(stashes)).IsEqualTo(
            $$"""
              stash@{0} on main: Work in progress on {{head}} Initial
              stash@{1} on main: their own
              stash@{2} on feature: Changes left on feature
              """.ReplaceLineEndings("\n"));
        await Assert.That(stashes[1].Commit).IsEqualTo(Scratch.Git(repo, "rev-parse", "stash@{1}"));
        await Assert.That(stashes.All(_ => _.Made != null)).IsTrue();
    }

    // Restored, a change and a file git doesn't track are back, and the stash is gone. Found by
    // its commit, not its name, which a stash made since moved along; the others are left alone.
    [Test]
    public async Task RestoreBringsTheChangesBackAndDropsTheStash()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "other");
        Scratch.Git(repo, "stash", "push", "-m", "other");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "new.txt", "new");
        Scratch.Git(repo, "stash", "push", "--include-untracked", "-m", "mine");
        var mine = GitStashes.Read(repo, Cancel.None)[0];
        Write(repo, "tracked.txt", "since");
        Scratch.Git(repo, "stash", "push", "-m", "since");

        var result = GitStashes.Restore(repo, mine, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).Contains("Restored mine.");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
        await Assert.That(Stashes(repo)).IsEqualTo("On main: since\nOn main: other");
    }

    // Restoring a stash that clashes with what is committed since leaves the clashes marked in
    // the file, as a conflict Changes settles, and keeps the stash, saying so.
    [Test]
    public async Task RestoreThatConflictsKeepsTheStash()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "stashed");
        Scratch.Git(repo, "stash", "push", "-m", "mine");
        Write(repo, "tracked.txt", "committed");
        Scratch.Git(repo, "commit", "-am", "Committed");
        var mine = GitStashes.Read(repo, Cancel.None)[0];

        var result = GitStashes.Restore(repo, mine, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("mine clashed with what is here, so stash@{0} is kept");
        await Assert.That(Stashes(repo)).IsEqualTo("On main: mine");
        await Assert.That(Read(repo, "tracked.txt")).Contains("<<<<<<<");
        var changes = GitChanges.Read(repo, Cancel.None)!;
        await Assert.That(changes.Files.Single().Kind).IsEqualTo(GitChange.Conflicted);
        await Assert.That(changes.Conflicted).IsTrue();
        await Assert.That(ChangesPane.SideNames(changes)).IsEqualTo(("main", "the stash"));
    }

    // Git refusing to restore a stash over changes it would overwrite changes nothing, and the
    // stash is kept, saying why.
    [Test]
    public async Task RestoreGitRefusesKeepsTheStash()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "stashed");
        Scratch.Git(repo, "stash", "push", "-m", "mine");
        Write(repo, "tracked.txt", "here");
        var mine = GitStashes.Read(repo, Cancel.None)[0];

        var result = GitStashes.Restore(repo, mine, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("Couldn't restore mine, so stash@{0} is kept");
        await Assert.That(result.Output).Contains("would be overwritten");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("here");
        await Assert.That(Stashes(repo)).IsEqualTo("On main: mine");
    }

    // Discarded, the stash chosen is gone, and only it, found by its commit though another was
    // made since.
    [Test]
    public async Task DiscardDropsOnlyTheOneChosen()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        foreach (var name in (string[]) ["one", "two"])
        {
            Write(repo, "tracked.txt", name);
            Scratch.Git(repo, "stash", "push", "-m", name);
        }

        var two = GitStashes.Read(repo, Cancel.None)[0];
        Write(repo, "tracked.txt", "three");
        Scratch.Git(repo, "stash", "push", "-m", "three");

        var result = GitStashes.Discard(repo, two, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Stashes(repo)).IsEqualTo("On main: three\nOn main: one");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");

        var again = GitStashes.Discard(repo, two, Cancel.None);
        await Assert.That(again.Succeeded).IsFalse();
        await Assert.That(again.Output).IsEqualTo("two isn't stashed any more.");
        await Assert.That(Stashes(repo)).IsEqualTo("On main: three\nOn main: one");
    }

    // Stash all changes: a change and a file git doesn't track go in the one stash, with the
    // message, leaving the folder as the last commit has it, and what git ignores where it is.
    [Test]
    public async Task StashAllStashesEverythingButWhatIsIgnored()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, ".git/info/exclude", "ignored.txt");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "new.txt", "new");
        Write(repo, "ignored.txt", "ignored");

        var result = GitStashes.StashAll(repo, "Half way", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "new.txt"))).IsFalse();
        await Assert.That(Read(repo, "ignored.txt")).IsEqualTo("ignored");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(Describe(GitStashes.Read(repo, Cancel.None))).IsEqualTo("stash@{0} on main: Half way");

        GitStashes.Restore(repo, GitStashes.Read(repo, Cancel.None)[0], Cancel.None);
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
    }
}
