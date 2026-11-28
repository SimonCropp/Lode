/// <summary>
/// Merging, squashing and rebasing against another branch, and bringing in the default branch,
/// against throwaway repositories in the temp folder.
/// </summary>
public class GitMergingTests
{
    static string Git(string repo, params string[] arguments) =>
        CodeCommandsTests.Scratch.Git(repo, arguments);

    // Commits a file of its own on a new branch, feature, then goes back to main.
    static async Task Branch(string repo, string branch = "feature", string file = "feature.txt", string text = "feature")
    {
        Git(repo, "switch", "-c", branch);
        await Commit(repo, file, text);
        Git(repo, "switch", "main");
    }

    static async Task Commit(string repo, string file, string text)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, file), text);
        Git(repo, "add", file);
        Git(repo, "commit", "-m", $"{file} {text}");
    }

    static GitMerging.Preview Preview(string repo, string current, string branch, GitMerging.Kind kind) =>
        GitMerging.Of(repo, current, branch, kind, Cancel.None);

    // As GitHub Desktop says it: how many commits it brings in, or that there is nothing to.
    [Test]
    public async Task MergeBringsInTheBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo);
        await Commit(repo, "main.txt", "main");

        var preview = Preview(repo, "main", "feature", GitMerging.Kind.Merge);
        var result = GitMerging.Merge(repo, "feature", squash: false, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will merge 1 commit from feature into main.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "log", "-1", "--format=%P").Split(' ').Length).IsEqualTo(2);
        await Assert.That(File.Exists(Path.Combine(repo, "feature.txt"))).IsTrue();
        await Assert.That(Preview(repo, "main", "feature", GitMerging.Kind.Merge)).IsEqualTo(new("main is already up to date with feature.", false));
    }

    // Found before merging, as git would merge them, without touching the folder. A merge is
    // left part way, for them to be settled in Changes; a squash, which leaves no merge under way
    // there, can't be pressed, and done anyway is undone, the folder as it was.
    [Test]
    public async Task AMergeThatWouldConflictIsLeftToSettle()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo, file: "tracked.txt", text: "feature");
        await Commit(repo, "tracked.txt", "main");
        var head = Git(repo, "rev-parse", "HEAD");

        var merge = Preview(repo, "main", "feature", GitMerging.Kind.Merge);
        var squash = Preview(repo, "main", "feature", GitMerging.Kind.Squash);

        await Assert.That(merge).IsEqualTo(new("There will be 1 conflicted file when merging feature into main, to settle in Changes before committing the merge.\ntracked.txt", true));
        await Assert.That(squash).IsEqualTo(new("There will be 1 conflicted file when squashing feature into main, so it can't be squashed here: merge it instead, settling them in Changes.\ntracked.txt", false));
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");

        var squashed = GitMerging.Merge(repo, "feature", squash: true, Cancel.None);

        await Assert.That(squashed.Succeeded).IsFalse();
        await Assert.That(squashed.Output).StartsWith("Squashing feature would conflict. It was undone");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);

        var merged = GitMerging.Merge(repo, "feature", squash: false, Cancel.None);

        await Assert.That(merged.Succeeded).IsFalse();
        await Assert.That(merged.Output).StartsWith("Merging feature left 1 conflicted file, to settle in Changes");
        await Assert.That(GitChanges.IsMerging(repo)).IsTrue();
        await Assert.That(Git(repo, "diff", "--name-only", "--diff-filter=U")).IsEqualTo("tracked.txt");
    }

    // Part way through a merge already, its conflicts settled, another would be refused by git,
    // and that one undone with it: it says what to do with the one under way instead.
    [Test]
    public async Task MergeLeavesAMergeNotFinished()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo, file: "tracked.txt", text: "feature");
        await Branch(repo, branch: "other", file: "other.txt", text: "other");
        await Commit(repo, "tracked.txt", "main");
        GitMerging.Merge(repo, "feature", squash: false, Cancel.None);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "settled");
        Git(repo, "add", "tracked.txt");

        var result = GitMerging.Merge(repo, "other", squash: false, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("It is part way through a merge, so nothing was merged. Settle its conflicts in Changes and commit it, or abort it there.");
        await Assert.That(GitChanges.IsMerging(repo)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("settled");
    }

    // The branch's commits as one commit, with git's message for it, listing them.
    [Test]
    public async Task SquashMakesOneCommit()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo);
        Git(repo, "switch", "feature");
        await Commit(repo, "second.txt", "second");
        Git(repo, "switch", "main");

        var preview = Preview(repo, "main", "feature", GitMerging.Kind.Squash);
        var result = GitMerging.Merge(repo, "feature", squash: true, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will squash 2 commits from feature into one commit on main.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "log", "-1", "--format=%P").Split(' ').Length).IsEqualTo(1);
        await Assert.That(Git(repo, "rev-list", "--count", "HEAD")).IsEqualTo("2");
        await Assert.That(Git(repo, "log", "-1", "--format=%s")).StartsWith("Squashed commit");
        await Assert.That(File.Exists(Path.Combine(repo, "second.txt"))).IsTrue();
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // The newest commit on the branch checked out, as History lists it.
    static GitCommit Newest(string repo) =>
        GitHistory.Read(repo, 0, 1, Cancel.None)![0];

    // History's Cherry-pick, comparing with another branch: the commit picked, of a branch the
    // one checked out hasn't merged, made again on top of it, by its author and with its whole
    // message, and none of that branch's other commits. Not with uncommitted changes, which
    // settling its conflicts would take into it, but for files git doesn't track.
    [Test]
    public async Task CherryPickMakesTheCommitAgainOnTheBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Git(repo, "switch", "-c", "feature");
        await Commit(repo, "first.txt", "first");
        await File.WriteAllTextAsync(Path.Combine(repo, "picked.txt"), "picked");
        Git(repo, "add", "picked.txt");
        Git(repo, "-c", "user.name=Ada Lovelace", "-c", "user.email=ada@example.com", "commit", "-m", "Picked", "-m", "Its body.");
        var picked = Newest(repo);
        Git(repo, "switch", "main");
        await Commit(repo, "main.txt", "main");
        var head = Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");

        var refused = GitMerging.CherryPick(repo, picked, Cancel.None);
        var headOnceRefused = Git(repo, "rev-parse", "HEAD");
        Git(repo, "checkout", "--", "tracked.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");
        var result = GitMerging.CherryPick(repo, picked, Cancel.None);

        await Assert.That(refused.Succeeded).IsFalse();
        await Assert.That(refused.Output).StartsWith("There are uncommitted changes, so nothing was cherry-picked: commit or stash them first.");
        await Assert.That(headOnceRefused).IsEqualTo(head);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-parse", "HEAD^")).IsEqualTo(head);
        await Assert.That(Git(repo, "log", "-1", "--format=%an <%ae>%n%B")).IsEqualTo("Ada Lovelace <ada@example.com>\nPicked\n\nIts body.");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "picked.txt"))).IsEqualTo("picked");
        await Assert.That(File.Exists(Path.Combine(repo, "first.txt"))).IsFalse();
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("?? untracked.txt");
    }

    // One that conflicts is left part way, for the conflicts to be settled in Changes, which has
    // it as the commit cherry-picked onto the branch, git's sides the branch checked out and the
    // commit. Aborted, the branch and its files are as they were. Settled and continued, the files
    // go in the commit, with its own message, no editor waited for, and nothing else does: a
    // change to another file, made meanwhile, stays uncommitted.
    [Test]
    public async Task ACherryPickThatConflictsIsLeftToSettle()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Commit(repo, "other.txt", "other");
        Git(repo, "switch", "-c", "feature");
        await Commit(repo, "tracked.txt", "feature");
        var picked = Newest(repo);
        Git(repo, "switch", "main");
        await Commit(repo, "tracked.txt", "main");
        var head = Git(repo, "rev-parse", "HEAD");

        var result = GitMerging.CherryPick(repo, picked, Cancel.None);
        var changes = GitChanges.Read(repo, Cancel.None)!;

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).StartsWith($"Cherry-picking {picked.ShortSha} left 1 conflicted file, to settle in Changes");
        await Assert.That((changes.Merging, changes.CherryPicking, changes.MergeSummary, changes.ConflictsLeft)).IsEqualTo((true, new CherryPick(picked.ShortSha), (string?) null, 1));
        await Assert.That(ChangesPane.MergeBarText(changes)).IsEqualTo($"Cherry-picking {picked.ShortSha} onto main: 1 conflicted file left. Right-click one to pick a side, or settle its markers in the editor.");
        await Assert.That(ChangesPane.SideNames(changes)).IsEqualTo(("main", picked.ShortSha));

        var aborted = GitChanges.Abort(repo, Cancel.None);

        await Assert.That(aborted.Succeeded).IsTrue();
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Merging).IsFalse();
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("main");

        GitMerging.CherryPick(repo, picked, Cancel.None);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "settled");
        await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "changed meanwhile");
        var settled = GitChanges.Read(repo, Cancel.None)!;
        var continued = GitChanges.Commit(repo, settled.Files, settled.Merging, "Not this", Cancel.None);

        await Assert.That(settled.Conflicted).IsFalse();
        await Assert.That(continued.Succeeded).IsTrue();
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Merging).IsFalse();
        await Assert.That(Git(repo, "rev-parse", "HEAD^")).IsEqualTo(head);
        await Assert.That(Git(repo, "log", "-1", "--format=%B")).IsEqualTo("tracked.txt feature");
        await Assert.That(Git(repo, "show", "HEAD:tracked.txt")).IsEqualTo("settled");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("M other.txt");
    }

    // Undoing a merge that conflicted could take an uncommitted change with it, so there must
    // be none, but for files git doesn't track.
    [Test]
    public async Task MergeRefusesWithUncommittedChanges()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        var head = Git(repo, "rev-parse", "HEAD");

        var result = GitMerging.Merge(repo, "feature", squash: false, Cancel.None);
        var rebase = GitMerging.Rebase(repo, "feature", forcePush: false, Cancel.None);

        await Assert.That((result.Succeeded, rebase.Succeeded)).IsEqualTo((false, false));
        await Assert.That(result.Output).StartsWith("There are uncommitted changes, so nothing was merged: commit or stash them first.");
        await Assert.That(rebase.Output).StartsWith("There are uncommitted changes, so nothing was rebased");
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);

        Git(repo, "checkout", "--", "tracked.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");
        await Assert.That(GitMerging.Merge(repo, "feature", squash: false, Cancel.None).Succeeded).IsTrue();
    }

    // The branch's own commits applied on top of the other, as GitHub Desktop says it, or a fast
    // forward where it has none of its own.
    [Test]
    public async Task RebaseAppliesTheCommitsOnTop()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo);
        await Commit(repo, "main.txt", "main");
        Git(repo, "switch", "-c", "behind", "HEAD~1");
        Git(repo, "switch", "feature");

        var preview = Preview(repo, "feature", "main", GitMerging.Kind.Rebase);
        var result = GitMerging.Rebase(repo, "main", forcePush: false, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will update feature by applying its 1 commit on top of main. Should one conflict, the rebase is undone.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-list", "--count", "main..HEAD")).IsEqualTo("1");
        await Assert.That(Git(repo, "rev-list", "--count", "HEAD..main")).IsEqualTo("0");
        await Assert.That(Preview(repo, "feature", "main", GitMerging.Kind.Rebase)).IsEqualTo(new("feature is already up to date with main.", false));

        Git(repo, "switch", "behind");
        await Assert.That(Preview(repo, "behind", "main", GitMerging.Kind.Rebase)).IsEqualTo(new("This will fast-forward behind by 1 commit to match main.", true));
    }

    // feature, with a commit pushed to origin, checked out, and a commit on main it hasn't, to
    // be rebased onto.
    static async Task<string> PushedFeature(CodeCommandsTests.Scratch code)
    {
        var repo = code.CreateRepo("Repo");
        Git(repo, "switch", "-c", "feature");
        await Commit(repo, "feature.txt", "feature");
        Git(repo, "push", "-u", "origin", "feature");
        Git(repo, "switch", "main");
        await Commit(repo, "main.txt", "main");
        Git(repo, "switch", "feature");
        return repo;
    }

    // A commit on origin's feature from another clone, as a teammate would push one.
    static async Task<string> PushFromAnotherClone(CodeCommandsTests.Scratch code)
    {
        var other = code.Clone("Repo", "Other");
        Git(other, "switch", "feature");
        await Commit(other, "teammate.txt", "teammate");
        Git(other, "push", "origin", "feature");
        return Git(other, "rev-parse", "HEAD");
    }

    // Rewriting commits origin has needs a force push, which it says, and does once asked,
    // overwriting origin's branch with the one rebased.
    [Test]
    public async Task RebasingAPushedBranchForcePushes()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await PushedFeature(code);

        var preview = Preview(repo, "feature", "main", GitMerging.Kind.Rebase);
        var result = GitMerging.Rebase(repo, "main", forcePush: true, Cancel.None);

        await Assert.That(preview.OnOrigin).IsEqualTo(1);
        await Assert.That(preview.Text).Contains("Origin has 1 commit of feature that this rewrites, so pushing it afterwards needs force");
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(Git(repo, "rev-parse", "HEAD"));
    }

    // A teammate's commit on origin's branch, fetched but not pulled: origin is where it was last
    // fetched, so a lease alone would pass, and the push delete it. Force push isn't offered,
    // and asked for anyway, nothing is rebased, nor pushed.
    [Test]
    public async Task RebaseDoesNotForcePushOverCommitsNotPulled()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await PushedFeature(code);
        var teammate = await PushFromAnotherClone(code);
        Git(repo, "fetch", "origin");
        var head = Git(repo, "rev-parse", "HEAD");

        var preview = Preview(repo, "feature", "main", GitMerging.Kind.Rebase);
        var result = GitMerging.Rebase(repo, "main", forcePush: true, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will update feature by applying its 1 commit on top of main. Should one conflict, the rebase is undone.\n\nOrigin has 1 commit on feature not pulled yet, which a force push would delete, so force pushing afterwards isn't offered: pull first, then rebase.", true));
        await Assert.That((result.Succeeded, result.Output)).IsEqualTo((false, "Origin has 1 commit on feature not pulled yet, which a force push would delete, so nothing was rebased: pull first, then rebase."));
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
        await Assert.That(Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(teammate);
    }

    // Fetched while rebasing, as a fetch in another window could, a teammate's commit would pass
    // the lease too: the push refuses it, as the branch never had it, leaving the branch rebased
    // and origin's as the teammate pushed it. The hook fetches once the rebase is done, before
    // the push.
    [Test]
    public async Task RebaseDoesNotForcePushOverCommitsFetchedWhileRebasing()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await PushedFeature(code);
        var teammate = await PushFromAnotherClone(code);
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "hooks", "post-rewrite"), "#!/bin/sh\ngit fetch --quiet origin\n");

        var result = GitMerging.Rebase(repo, "main", forcePush: true, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("feature -> feature (remote ref updated since checkout)");
        await Assert.That(Git(repo, "rev-parse", "origin/feature")).IsEqualTo(teammate);
        await Assert.That(Git(repo, "rev-list", "--count", "HEAD..main")).IsEqualTo("0");
        await Assert.That(Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(teammate);
    }

    [Test]
    public async Task ARebaseThatConflictsIsUndone()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await Branch(repo, file: "tracked.txt", text: "feature");
        await Commit(repo, "tracked.txt", "main");
        Git(repo, "switch", "feature");
        var head = Git(repo, "rev-parse", "HEAD");

        var result = GitMerging.Rebase(repo, "main", forcePush: false, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).StartsWith("Rebasing onto main would conflict. The rebase was undone");
        await Assert.That(result.Output).Contains("tracked.txt");
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
        await Assert.That(Git(repo, "symbolic-ref", "--short", "HEAD")).IsEqualTo("feature");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // Origin's default branch as fetched now, merged into the branch checked out, and nothing
    // pushed; the default branch itself is left to Pull.
    [Test]
    public async Task UpdateFromDefaultMergesOriginsDefaultBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        await Commit(other, "other.txt", "other");
        Git(other, "push", "origin", "main");
        var onMain = (await GitMerging.UpdateFromDefault([repo])).Single();
        var mainHead = Git(repo, "rev-parse", "HEAD");
        Git(repo, "switch", "-c", "feature");
        await Commit(repo, "feature.txt", "feature");

        var result = (await GitMerging.UpdateFromDefault([repo])).Single();

        await Assert.That(onMain.Output).IsEqualTo("main is the default branch: Pull brings it up to date.");
        await Assert.That(mainHead).IsNotEqualTo(Git(other, "rev-parse", "HEAD"));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(File.Exists(Path.Combine(repo, "other.txt"))).IsTrue();
        await Assert.That(Git(code.Origin("Repo"), "branch", "--list", "feature")).IsEqualTo("");
    }

    // A fork's default branch is upstream's, the repository it was made from.
    [Test]
    public async Task UpdateFromDefaultInAForkMergesUpstreams()
    {
        using var code = new CodeCommandsTests.Scratch();
        var fork = code.CreateFork("Repo");
        Git(fork, "switch", "-c", "feature");
        var upstream = code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await GitMerging.UpdateFromDefault([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(fork, "merge-base", "--is-ancestor", upstream, "HEAD")).IsEqualTo("");
    }
}
