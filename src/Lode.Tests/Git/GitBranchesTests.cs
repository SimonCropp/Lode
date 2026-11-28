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

        return Verify(Describe(GitBranches.Parse(refs, "main", reflog, stashes)))
            .Snapshot(
                """
                {
                  Current: feature-b,
                  Default: main,
                  Recent: [
                    feature-b,
                    feature-a (stashed)
                  ],
                  Other: [
                    zebra,
                    origin/only-remote (remote)
                  ]
                }
                """);
    }

    // GitHub Desktop lists five.
    [Test]
    public async Task RecentHoldsFive()
    {
        var names = Enumerable.Range(1, 7).Select(_ => $"branch-{_}").ToList();
        var refs = Lines([.. names.Select(_ => Ref($"refs/heads/{_}"))]);
        var reflog = Lines([.. names.Select(_ => $"checkout: moving from main to {_}")]);

        var listing = GitBranches.Parse(refs, null, reflog, "");

        await Assert.That(listing.Recent.Select(_ => _.Name)).IsEquivalentTo(names.Take(5), CollectionOrdering.Matching);
        await Assert.That(listing.Other.Select(_ => _.Name)).IsEquivalentTo(names.Skip(5), CollectionOrdering.Matching);
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
        await Assert.That(remote.Default).IsEqualTo(new("origin/trunk", true, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), false));
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
                .Select(_ => $"{_} -> {GitBranches.Ago(now - _, now)}"))
                .Snapshot(
                    """
                    [
                      -00:00:05 -> just now,
                      00:00:30 -> just now,
                      00:01:00 -> 1 minute,
                      00:59:00 -> 59 minutes,
                      01:00:00 -> 1 hour,
                      15:00:00 -> 15 hours,
                      1.00:00:00 -> 1 day,
                      29.00:00:00 -> 29 days,
                      45.00:00:00 -> 1 month,
                      364.00:00:00 -> 12 months,
                      800.00:00:00 -> 2 years
                    ]
                    """);
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
        await Assert.That(listing.Recent.Select(Describe)).IsEquivalentTo(["untracked", "tracked"], CollectionOrdering.Matching);
        await Assert.That(listing.Other.Select(Describe)).IsEquivalentTo(["origin/gone (remote)", "origin/untracked (remote)"], CollectionOrdering.Matching);
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
    // the same, it says so, and the changes are still stashed, though no longer as the branch's
    // to put back. With the clash settled and committed, coming back again leaves them there,
    // rather than putting them back to clash again.
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
        var stashes = Stashes(repo);
        Write(repo, "tracked.txt", "settled");
        Scratch.Git(repo, "commit", "-am", "Settled");
        Scratch.Git(repo, "switch", "main");
        var again = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("couldn't all be put back, so they are still stashed, in stash@{0}");
        await Assert.That(stashes).IsEqualTo("On feature: Lode clashed:feature");
        await Assert.That(again.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("settled");
        await Assert.That(Stashes(repo)).IsEqualTo("On feature: Lode clashed:feature");
    }

    // Git keeps the stash before it clears what it stashed from the folder, the files it doesn't
    // track first. With one of those that another program has open, sharing reading but not
    // deleting, as an editor might, the clearing fails, and so does the stash, though the changes
    // are in it by then: the other new files are gone from the folder, and the changes to the
    // files git tracks are still there, as git stopped before them. Not switched, the folder is
    // as it was, the new files back, and no stash is left, to be put back on coming back to the
    // branch later, onto whatever is there then. Open sharing nothing, git couldn't read it to
    // stash it, and nothing changes at all.
    [Test]
    public async Task AStashGitCouldNotFinishLeavesTheChangesAsTheyWere()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "staged.txt", "staged");
        Scratch.Git(repo, "add", "staged.txt");
        Write(repo, "new.txt", "new");
        Directory.CreateDirectory(Path.Combine(repo, "folder"));
        Write(repo, @"folder\deep.txt", "deep");
        Write(repo, "open.txt", "open");
        var status = Scratch.Git(repo, "status", "--porcelain");
        CodeCommands.RepoResult result;
        await using (new FileStream(Path.Combine(repo, "open.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);
        }

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("Couldn't stash the changes, so not switched");
        await Assert.That(result.Output).Contains("Nothing is left stashed: the changes are here, as they were.");
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo(status);
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
        await Assert.That(Read(repo, @"folder\deep.txt")).IsEqualTo("deep");
        await Assert.That(Read(repo, "open.txt")).IsEqualTo("open");
        await Assert.That(Stashes(repo)).IsEqualTo("");

        // The changes discarded since, and feature left and come back to, they aren't put back.
        Scratch.Git(repo, "reset", "--hard");
        Scratch.Git(repo, "clean", "-fd");
        GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);
        var back = GitBranches.Switch(repo, "main", Local("feature"), leave: false, Cancel.None);

        await Assert.That(back.Succeeded).IsTrue();
        await Assert.That(back.Output).DoesNotContain("Put back");
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("original");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // A file git tracks that another program has open, as a spreadsheet open in Excel is: git
    // stashes it, reading it, clears the new files from the folder, then puts back the files it
    // tracks as the last commit has them, every one but that, which it can't replace, and fails.
    // Not switched, the folder is as it was, what is staged staged still, and no stash is left.
    [Test]
    public async Task AStashGitCouldNotFinishOverAnOpenTrackedFileLeavesTheChangesAsTheyWere()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "open.txt", "original");
        Write(repo, "staged.txt", "original");
        Scratch.Git(repo, "add", "open.txt", "staged.txt");
        Scratch.Git(repo, "commit", "-m", "More");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "open.txt", "changed");
        Write(repo, "staged.txt", "staged");
        Scratch.Git(repo, "add", "staged.txt");
        Write(repo, "staged.txt", "staged, then changed");
        Write(repo, "new.txt", "new");
        var status = Scratch.Git(repo, "status", "--porcelain");
        CodeCommands.RepoResult result;
        await using (new FileStream(Path.Combine(repo, "open.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = GitBranches.Switch(repo, "feature", Local("main"), leave: true, Cancel.None);
        }

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("Couldn't stash the changes, so not switched");
        await Assert.That(result.Output).Contains("Nothing is left stashed: the changes are here, as they were.");
        await Assert.That(Branch(repo)).IsEqualTo("feature");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo(status);
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "open.txt")).IsEqualTo("changed");
        await Assert.That(Read(repo, "staged.txt")).IsEqualTo("staged, then changed");
        await Assert.That(Scratch.Git(repo, "show", ":staged.txt")).IsEqualTo("staged");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
        await Assert.That(Stashes(repo)).IsEqualTo("");
    }

    // Should a change of a stash git couldn't finish not go back, a file another program opened
    // since, say, the stash is kept, but not as changes left on the branch, which coming back to
    // it would put back: Stashed changes has it, to restore.
    [Test]
    public async Task AStashGitCouldNotFinishThatCantAllGoBackIsKeptButNotAsTheBranchs()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "changed");
        Write(repo, "new.txt", "new");
        Scratch.Git(repo, "stash", "push", "--include-untracked", "-m", GitBranches.StashMarker + "feature");
        var stash = Scratch.Git(repo, "rev-parse", "refs/stash");
        CodeCommands.RepoResult result;
        await using (new FileStream(Path.Combine(repo, "tracked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = GitBranches.Unstash(repo, stash, "feature", new(repo, false, "Couldn't stash the changes, so not switched"));
        }

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("The changes couldn't all be put back, so they are still stashed, in stash@{0}");
        await Assert.That(Read(repo, "new.txt")).IsEqualTo("new");
        await Assert.That(Stashes(repo)).IsEqualTo("On feature: Lode kept:feature");
        await Assert.That(Scratch.Git(repo, "rev-parse", "refs/stash")).IsEqualTo(stash);
        await Assert.That(GitBranches.Read(repo, Cancel.None).Recent.Single(_ => _.Name == "feature").Stashed).IsFalse();
        await Assert.That(GitStashes.Read(repo, Cancel.None).Single().Text).IsEqualTo("Changes on feature a switch couldn't stash, not all put back");
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

    // History's Create branch from commit: the branch is made at the commit picked, not the one
    // checked out, and switched to, what is uncommitted coming along where git can bring it.
    // Where it can't, a file the commit has otherwise changed, nothing is made.
    [Test]
    public async Task CreateMakesABranchAtTheCommitPicked()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var picked = Scratch.Git(repo, "rev-parse", "HEAD");
        Write(repo, "later.txt", "later");
        Scratch.Git(repo, "add", "later.txt");
        Scratch.Git(repo, "commit", "-m", "Later");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.Create(repo, "from-commit", Cancel.None, at: picked);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("from-commit");
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(picked);
        await Assert.That(Read(repo, "tracked.txt")).IsEqualTo("changed");
        await Assert.That(File.Exists(Path.Combine(repo, "later.txt"))).IsFalse();

        Scratch.Git(repo, "switch", "-q", "main");
        Scratch.Git(repo, "checkout", "--", "tracked.txt");
        Write(repo, "later.txt", "uncommitted");
        var refused = GitBranches.Create(repo, "overwriting", Cancel.None, at: picked);

        await Assert.That(refused.Succeeded).IsFalse();
        await Assert.That(Branch(repo)).IsEqualTo("main");
        await Assert.That(Branches(repo)).DoesNotContain("overwriting");
        await Assert.That(Read(repo, "later.txt")).IsEqualTo("uncommitted");
    }

    // History's Checkout commit: the commit picked checked out on no branch, the branch left
    // where it was, what is uncommitted coming along, as git brings it.
    [Test]
    public async Task CheckoutCommitDetachesAtTheCommitPicked()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var picked = Scratch.Git(repo, "rev-parse", "HEAD");
        Write(repo, "later.txt", "later");
        Scratch.Git(repo, "add", "later.txt");
        Scratch.Git(repo, "commit", "-m", "Later");
        var later = Scratch.Git(repo, "rev-parse", "HEAD");
        Write(repo, "tracked.txt", "changed");

        var result = GitBranches.CheckoutCommit(repo, picked, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(picked);
        await Assert.That(Scratch.Git(repo, "branch", "--show-current")).IsEqualTo("");
        await Assert.That(Scratch.Git(repo, "rev-parse", "main")).IsEqualTo(later);
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

    static string Branches(string repo) =>
        Scratch.Git(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads");

    static string OriginBranches(Scratch code, string name) =>
        Scratch.Git(code.Origin(name), "for-each-ref", "--format=%(refname:short)", "refs/heads");

    // What the right-click entries need to know of a local branch: the branch it tracks.
    [Test]
    public async Task ReadsWhatEachBranchTracks()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "local-only");

        var listing = GitBranches.Read(repo, Cancel.None);

        await Assert.That(listing.Default?.Upstream).IsEqualTo("origin/main");
        await Assert.That(listing.Other.Single(_ => _.Name == "local-only").Upstream).IsNull();
    }

    [Test]
    public async Task RenamesABranch()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "feature");

        var result = GitBranches.Rename(repo, "feature", "renamed", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branches(repo)).IsEqualTo("main\nrenamed");
    }

    // Windows' file system ignores case, so git takes Feature for the feature there already
    // unless forced.
    [Test]
    public async Task RenamesOnlyTheCase()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "feature");

        var result = GitBranches.Rename(repo, "feature", "Feature", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branches(repo)).IsEqualTo("Feature\nmain");
    }

    // Merged or not, as GitHub Desktop deletes, saying the commit it was at, to get it back.
    [Test]
    public async Task DeletesAnUnmergedBranch()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Write(repo, "tracked.txt", "feature's");
        Scratch.Git(repo, "commit", "-am", "Feature");
        Scratch.Git(repo, "switch", "main");

        var result = GitBranches.Delete(repo, Local("feature"), alsoOnRemote: false, switchTo: null, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).StartsWith("Deleted branch feature (was ");
        await Assert.That(Branches(repo)).IsEqualTo("main");
    }

    // Asked to, the branch it tracks goes from origin too; otherwise origin keeps it.
    [Test]
    public async Task DeletesOnTheRemoteWhenAsked()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        foreach (var name in new[] {"kept", "gone"})
        {
            Scratch.Git(repo, "switch", "-c", name);
            Scratch.Git(repo, "push", "-u", "origin", name);
        }

        Scratch.Git(repo, "switch", "main");
        var listing = GitBranches.Read(repo, Cancel.None);
        var kept = listing.Recent.Single(_ => _.Name == "kept");
        var gone = listing.Recent.Single(_ => _.Name == "gone");

        var keptResult = GitBranches.Delete(repo, kept, alsoOnRemote: false, switchTo: null, Cancel.None);
        var goneResult = GitBranches.Delete(repo, gone, alsoOnRemote: true, switchTo: null, Cancel.None);

        await Assert.That(keptResult.Succeeded).IsTrue();
        await Assert.That(goneResult.Succeeded).IsTrue();
        await Assert.That(Branches(repo)).IsEqualTo("main");
        await Assert.That(OriginBranches(code, "Repo")).IsEqualTo("kept\nmain");
    }

    // The branch checked out is switched off first, as git won't delete it.
    [Test]
    public async Task DeletingTheBranchCheckedOutSwitchesFirst()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");

        var result = GitBranches.Delete(repo, Local("feature"), alsoOnRemote: false, switchTo: Local("main"), Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(repo)).IsEqualTo("main");
        await Assert.That(Branches(repo)).IsEqualTo("main");
    }

    // Couldn't switch off it, it isn't deleted.
    [Test]
    public async Task NotDeletedWhenTheSwitchFails()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");

        var result = GitBranches.Delete(repo, Local("feature"), alsoOnRemote: false, switchTo: Local("missing"), Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).StartsWith("Couldn't switch off feature, so it wasn't deleted:");
        await Assert.That(Branches(repo)).IsEqualTo("feature\nmain");
    }

    // One only on origin goes from origin.
    [Test]
    public async Task DeletesARemoteBranchFromTheRemote()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");
        Scratch.Git(repo, "push", "origin", "feature");
        Scratch.Git(repo, "switch", "main");
        Scratch.Git(repo, "branch", "-D", "feature");

        var result = GitBranches.Delete(repo, new("origin/feature", true, null, false), alsoOnRemote: false, switchTo: null, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(OriginBranches(code, "Repo")).IsEqualTo("main");
        await Assert.That(GitBranches.Read(repo, Cancel.None).Other).IsEmpty();
    }

    [Test]
    public async Task ChecksABranchOutInANewWorktree()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "branch", "feature");
        var folder = GitBranches.WorktreeFolder(repo, Local("feature"));

        var result = GitBranches.AddWorktree(repo, Local("feature"), folder, Cancel.None);

        await Assert.That(code.Relative(folder)).IsEqualTo("Repo-feature");
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(folder)).IsEqualTo("feature");
        await Assert.That(Branch(repo)).IsEqualTo("main");
    }

    // One only on origin gets a local branch of its name, tracking it, as switching to it does.
    // A slash in its name is no folder in the worktree's.
    [Test]
    public async Task ChecksARemoteBranchOutInANewWorktree()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature/one");
        Scratch.Git(repo, "push", "origin", "feature/one");
        Scratch.Git(repo, "switch", "main");
        Scratch.Git(repo, "branch", "-D", "feature/one");
        var remote = new GitBranches.Branch("origin/feature/one", true, null, false);
        var folder = GitBranches.WorktreeFolder(repo, remote);

        var result = GitBranches.AddWorktree(repo, remote, folder, Cancel.None);

        await Assert.That(code.Relative(folder)).IsEqualTo("Repo-feature-one");
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Branch(folder)).IsEqualTo("feature/one");
        await Assert.That(Scratch.Git(folder, "rev-parse", "--abbrev-ref", "feature/one@{upstream}")).IsEqualTo("origin/feature/one");
    }
}
