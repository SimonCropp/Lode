using static CodeCommandsTests;

/// <summary>
/// The status bar's branches, read and switched to for real in throwaway repositories in the
/// temp folder, with a bare repository standing in for origin.
/// </summary>
public class GitBranchesTests
{
    // One line of for-each-ref's output: "*" for the branch checked out, its ref, its upstream,
    // and when its last commit was made, each after a NUL.
    static string Ref(string name, string upstream = "", bool current = false, long committed = 1_700_000_000)
    {
        var head = " ";
        if (current)
        {
            head = "*";
        }

        return string.Join('\0', head, name, upstream, committed.ToString(CultureInfo.InvariantCulture));
    }

    static string Lines(params string[] lines) =>
        string.Join('\n', lines);

    // A branch as the tests show it: its name, and whether it is only on a remote, or has
    // changes stashed, waiting for it.
    static string Describe(GitBranches.Branch branch)
    {
        var text = branch.Name;
        if (branch.Remote)
        {
            text += " (remote)";
        }

        if (branch.Stashed)
        {
            text += " (stashed)";
        }

        return text;
    }

    static object Describe(GitBranches.Listing listing) =>
        new
        {
            listing.Current,
            Default = listing.Default?.Name,
            Recent = listing.Recent.Select(Describe),
            Other = listing.Other.Select(Describe)
        };

    // Origin's default branch on its own; then those switched to, newest first, each once, not
    // the default again, nor a commit HEAD was detached at, nor a branch since deleted; then the
    // rest, local before remote. A remote branch a local one tracks is listed as the local one.
    [Test]
    public Task ParseGroupsAsGitHubDesktopDoes()
    {
        var refs = Lines(
            Ref("refs/heads/feature-a", "refs/remotes/origin/feature-a"),
            Ref("refs/heads/feature-b", current: true),
            Ref("refs/heads/main", "refs/remotes/origin/main"),
            Ref("refs/heads/zebra"),
            Ref("refs/remotes/origin/HEAD"),
            Ref("refs/remotes/origin/feature-a"),
            Ref("refs/remotes/origin/main"),
            Ref("refs/remotes/origin/only-remote"));
        var reflog = Lines(
            "checkout: moving from feature-a to feature-b",
            "commit: More",
            "checkout: moving from main to feature-a",
            "checkout: moving from feature-b to main",
            "checkout: moving from 1a2b3c4 to feature-b",
            "checkout: moving from main to 1a2b3c4",
            "checkout: moving from deleted to main",
            "checkout: moving from main to deleted");
        var stashes = Lines(
            "On feature-a: Lode:feature-a",
            "WIP on main: 1a2b3c4 Initial",
            "On main: something of the user's own");

        return Verify(Describe(GitBranches.Parse(refs, "main", reflog, stashes)));
    }

    // GitHub Desktop lists five.
    [Test]
    public async Task RecentHoldsFive()
    {
        var names = Enumerable.Range(1, 7).Select(_ => $"branch-{_}").ToList();
        var refs = Lines([.. names.Select(_ => Ref($"refs/heads/{_}"))]);
        var reflog = Lines([.. names.Select(_ => $"checkout: moving from main to {_}")]);

        var listing = GitBranches.Parse(refs, null, reflog, "");

        await Assert.That(listing.Recent.Select(_ => _.Name)).IsEquivalentTo(names.Take(5), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(listing.Other.Select(_ => _.Name)).IsEquivalentTo(names.Skip(5), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // With no default recorded, as in a repository made by init, main or master, whichever
    // there is; recorded, but with no local branch of it yet, origin's.
    [Test]
    public async Task DefaultWithoutALocalBranchOrARecord()
    {
        var master = GitBranches.Parse(Lines(Ref("refs/heads/feature"), Ref("refs/heads/master")), null, "", "");
        var none = GitBranches.Parse(Ref("refs/heads/feature"), null, "", "");
        var remote = GitBranches.Parse(Lines(Ref("refs/heads/feature"), Ref("refs/remotes/origin/trunk")), "trunk", "", "");

        await Assert.That(master.Default?.Name).IsEqualTo("master");
        await Assert.That(none.Default).IsNull();
        await Assert.That(remote.Default).IsEqualTo(new GitBranches.Branch("origin/trunk", true, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), false));
        await Assert.That(remote.Other.Select(_ => _.Name)).IsEquivalentTo(["feature"]);
    }

    [Test]
    public Task Ago()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        return Verify(
            new[]
                {
                    TimeSpan.FromSeconds(-5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(59),
                    TimeSpan.FromHours(1),
                    TimeSpan.FromHours(15),
                    TimeSpan.FromDays(1),
                    TimeSpan.FromDays(29),
                    TimeSpan.FromDays(45),
                    TimeSpan.FromDays(364),
                    TimeSpan.FromDays(800)
                }
                .Select(_ => $"{_} -> {GitBranches.Ago(now - _, now)}"));
    }

    // A repository made by init has no default recorded, so main is; a branch pushed without
    // tracking is listed both ways, and one whose local branch was deleted only as origin's.
    [Test]
    public async Task ReadsARepository()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "tracked");
        Scratch.Git(repo, "push", "-u", "origin", "tracked");
        Scratch.Git(repo, "switch", "-c", "gone");
        Scratch.Git(repo, "push", "origin", "gone");
        Scratch.Git(repo, "switch", "-c", "untracked");
        Scratch.Git(repo, "push", "origin", "untracked");
        Scratch.Git(repo, "branch", "-D", "gone");

        var listing = GitBranches.Read(repo, Cancel.None);

        await Assert.That(listing.Error).IsNull();
        await Assert.That(listing.Current).IsEqualTo("untracked");
        await Assert.That(listing.Default?.Name).IsEqualTo("main");
        await Assert.That(listing.Recent.Select(Describe)).IsEquivalentTo(["untracked", "tracked"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(listing.Other.Select(Describe)).IsEquivalentTo(["origin/gone (remote)", "origin/untracked (remote)"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(listing.Recent[0].Committed).IsNotNull();
    }

    // A folder git doesn't know is no repository: it says why, rather than listing nothing.
    [Test]
    public async Task ReadingNoRepositorySaysWhy()
    {
        using var code = new Scratch();

        var listing = GitBranches.Read(code.Root, Cancel.None);

        await Assert.That(listing.Error).IsNotNull();
        await Assert.That(listing.IsEmpty).IsTrue();
    }

    static GitBranches.Branch Local(string name) =>
        new(name, false, null, false);

    static string Branch(string repo) =>
        Scratch.Git(repo, "branch", "--show-current");

    static string Read(string repo, string file) =>
        File.ReadAllText(Path.Combine(repo, file));

    static void Write(string repo, string file, string text) =>
        File.WriteAllText(Path.Combine(repo, file), text);

    static string Stashes(string repo) =>
        Scratch.Git(repo, "stash", "list", "--format=%gs");

    [Test]
    public async Task SwitchesToALocalBranch()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "feature");

        var result = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("feature");
    }

    // As GitHub Desktop does: a local branch of its name, tracking it.
    [Test]
    public async Task SwitchingToARemoteBranchMakesALocalOneTrackingIt()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Scratch.Git(repo, "push", "origin", "feature");
        Scratch.Git(repo, "switch", "main");
        Scratch.Git(repo, "branch", "-D", "feature");

        var result = GitBranches.Switch(repo, "main", new("origin/feature", true, null, false), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Scratch.Git(repo, "rev-parse", "--abbrev-ref", "feature@{upstream}")).IsEqualTo("origin/feature");
    }

    // Left, a change and a file git doesn't track are stashed, and the branch switched to has
    // neither; back on the branch they were left on, both are put back, and the stash is gone.
    [Test]
    public async Task ChangesLeftOnABranchComeBackWithIt()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "new.txt", "new");

        var away = GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);

        await Assert.That(away.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("main");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "new.txt"))).IsFalse();
        await Assert.That(Stashes(repo)).IsEqualTo("On feature: Lode:feature");
        await Assert.That(GitBranches.Read(repo, Cancel.None).Recent.Single(_ => _.Name == "feature").Stashed).IsTrue();

        var back = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(back.Succeeded).IsTrue();
        await Assert.That(back.Output).Contains("Put back the changes left on feature.");
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
        await Assert.That(Stashes(repo)).IsEqualTo("");
    }

    // The changes are never left hidden in a stash on a branch that wasn't left.
    [Test]
    public async Task ChangesLeftAreBackWhenTheSwitchFails()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.Switch(repo, "main", Local("missing"), leave: true, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("The changes were put back.");
        await Assert.That(Branch(repo)).IsEqualTo("main");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Stashes(repo)).IsEqualTo("");
    }

    // Asked to leave changes there turn out to be none of, there is nothing to put back: a stash
    // of the user's own is left alone.
    [Test]
    public async Task LeavingNoChangesTouchesNoStash()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "theirs");
        Scratch.Git(repo, "stash", "push", "-m", "their own");

        var result = GitBranches.Switch(repo, "main", Local("missing"), leave: true, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");
        await Assert.That(Stashes(repo)).IsEqualTo("On main: their own");
    }

    [Test]
    public async Task ChangesBroughtComeAlong()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "feature");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
    }

    // Git refuses to overwrite a change with the branch's own, so nothing is switched and the
    // change is as it was.
    [Test]
    public async Task ChangesThatWouldBeOverwrittenStopTheSwitch()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "feature's");
        Scratch.Git(repo, "commit", "-am", "Feature");
        Scratch.Git(repo, "switch", "main");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("would be overwritten");
        await Assert.That(Branch(repo)).IsEqualTo("main");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
    }

    // The branch moved on since, in a way that clashes with the changes left on it: switched all
    // the same, it says so, and the changes are still stashed.
    [Test]
    public async Task ChangesThatClashStayStashed()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "left");
        GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);
        Scratch.Git(repo, "switch", "feature");
        Write(repo, "tracked.txt", "moved on");
        Scratch.Git(repo, "commit", "-am", "Moved on");
        Scratch.Git(repo, "switch", "main");

        var result = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("couldn't all be put back, so they are still stashed, in stash@{0}");
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Stashes(repo)).IsEqualTo("On feature: Lode:feature");
    }

    // Changes brought to a branch that has some left on it already: those left stay stashed,
    // rather than being mixed in.
    [Test]
    public async Task ChangesLeftWaitWhileThereAreOthers()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "left");
        GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);
        Write(repo, "new.txt", "brought");

        var result = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).Contains("The changes left on feature are still stashed, in stash@{0}, as there are changes here already.");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("brought");
        await Assert.That(Stashes(repo)).IsEqualTo("On feature: Lode:feature");
    }

    [Test]
    public async Task CreateMakesABranchAndSwitchesToIt()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.Create(repo, "feature", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
    }

    [Test]
    public async Task CreateRefusesANameGitWont()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");

        var result = GitBranches.Create(repo, "two..dots", Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Branch(repo)).IsEqualTo("main");
    }
}
