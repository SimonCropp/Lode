/// <summary>
/// Merging the branch checked out into another without checking that out, against throwaway
/// repositories in the temp folder: develop's commits put on main, develop staying checked out.
/// </summary>
public class GitMergeIntoTests
{
    static string Git(string repo, params string[] arguments) =>
        CodeCommandsTests.Scratch.Git(repo, arguments);

    static async Task Commit(string repo, string file, string text)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, file), text);
        Git(repo, "add", file);
        Git(repo, "commit", "-m", $"{file} {text}");
    }

    // A repository on develop, made from main, with a commit of its own.
    static async Task<string> OnDevelop(CodeCommandsTests.Scratch code, string file = "develop.txt", string text = "develop")
    {
        var repo = code.CreateRepo("Repo");
        Git(repo, "switch", "-c", "develop");
        await Commit(repo, file, text);
        return repo;
    }

    static GitMerging.Preview Preview(string repo, string picked) =>
        GitMerging.IntoOf(repo, "develop", picked, Cancel.None);

    // Main with nothing develop hasn't is moved up to it, no merge commit made, as git merge
    // would. Develop stays checked out, and what is uncommitted in the folder stays as it was.
    [Test]
    public async Task FastForwardsWithoutCheckingOut()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await OnDevelop(code);
        await File.WriteAllTextAsync(Path.Combine(repo, "loose.txt"), "loose");

        var preview = Preview(repo, "main");
        var result = GitMerging.Into(repo, "develop", "main", push: false, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will fast-forward main by 1 commit to match develop, leaving develop checked out.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).IsEqualTo("Fast-forwarded main by 1 commit to match develop.");
        await Assert.That(Git(repo, "rev-parse", "main")).IsEqualTo(Git(repo, "rev-parse", "develop"));
        await Assert.That(Git(repo, "branch", "--show-current")).IsEqualTo("develop");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("?? loose.txt");
        await Assert.That(Git(repo, "reflog", "-1", "--format=%gs", "main")).IsEqualTo("merge develop: Fast-forward");
        await Assert.That(Preview(repo, "main")).IsEqualTo(new("main already has every commit on develop.", false));
    }

    // Main with a commit of its own gets a merge commit, of main then develop, holding both, as
    // git would merge them, made without the folder.
    [Test]
    public async Task MergesWithAMergeCommit()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await OnDevelop(code);
        Git(repo, "switch", "main");
        await Commit(repo, "main.txt", "main");
        var main = Git(repo, "rev-parse", "main");
        Git(repo, "switch", "develop");
        var develop = Git(repo, "rev-parse", "develop");

        var preview = Preview(repo, "main");
        var result = GitMerging.Into(repo, "develop", "main", push: false, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will merge 1 commit from develop into main, with a merge commit, leaving develop checked out.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).IsEqualTo("Merged 1 commit from develop into main.");
        await Assert.That(Git(repo, "log", "-1", "--format=%P", "main")).IsEqualTo($"{main} {develop}");
        await Assert.That(Git(repo, "log", "-1", "--format=%s", "main")).IsEqualTo("Merge branch 'develop' into main");
        await Assert.That(Git(repo, "ls-tree", "--name-only", "main").Split('\n')).Contains("develop.txt");
        await Assert.That(Git(repo, "ls-tree", "--name-only", "main").Split('\n')).Contains("main.txt");
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(develop);
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // One that would conflict can't be pressed, as the conflicts could only be settled with main
    // checked out, and done anyway changes nothing.
    [Test]
    public async Task AConflictChangesNothing()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await OnDevelop(code, "shared.txt", "develop");
        Git(repo, "switch", "main");
        await Commit(repo, "shared.txt", "main");
        var main = Git(repo, "rev-parse", "main");
        Git(repo, "switch", "develop");

        var preview = Preview(repo, "main");
        var result = GitMerging.Into(repo, "develop", "main", push: false, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("There will be 1 conflicted file when merging develop into main, which can only be settled with main checked out: switch to it, and merge develop into it there.\nshared.txt", false));
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).StartsWith("Merging develop into main would conflict, so nothing was merged");
        await Assert.That(Git(repo, "rev-parse", "main")).IsEqualTo(main);
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // A branch checked out in another worktree is left to be merged into there, as moving it
    // would leave that folder out of step with it.
    [Test]
    public async Task NotIntoABranchCheckedOutElsewhere()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await OnDevelop(code);
        var worktree = code.At("Main");
        Git(repo, "worktree", "add", worktree, "main");

        var preview = Preview(repo, "main");

        await Assert.That(preview).IsEqualTo(new($"main is checked out in {worktree}, so it can't be changed from here: merge develop into it there.", false));
    }

    // A branch only on origin gets a local branch of its name, at the merge, tracking origin's,
    // and is pushed where asked.
    [Test]
    public async Task IntoABranchOnlyOnOriginAndPushed()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = await OnDevelop(code);
        Git(repo, "push", "origin", "main:release");
        Git(repo, "fetch", "origin");

        var preview = Preview(repo, "origin/release");
        var result = GitMerging.Into(repo, "develop", "origin/release", push: true, Cancel.None);

        await Assert.That(preview).IsEqualTo(new("This will fast-forward release by 1 commit to match develop, leaving develop checked out.\n\nA local release is made for it, tracking origin/release.", true));
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-parse", "release")).IsEqualTo(Git(repo, "rev-parse", "develop"));
        await Assert.That(Git(repo, "rev-parse", "--abbrev-ref", "release@{upstream}")).IsEqualTo("origin/release");
        await Assert.That(Git(code.Origin("Repo"), "rev-parse", "release")).IsEqualTo(Git(repo, "rev-parse", "develop"));
        await Assert.That(Git(repo, "branch", "--show-current")).IsEqualTo("develop");
    }

    // Several repositories at once, each merging the branch it has checked out into main and
    // staying on it: Ahead is fast-forwarded, Apart gets a merge commit, Clash would conflict so
    // is left as it is, and OnMain, on main already, is left out. What each comes to is said
    // first, read as it would merge them.
    [Test]
    public async Task SeveralRepositories()
    {
        using var code = new CodeCommandsTests.Scratch();
        var ahead = code.CreateRepo("Ahead");
        var apart = code.CreateRepo("Apart");
        var clash = code.CreateRepo("Clash");
        var onMain = code.CreateRepo("OnMain");
        foreach (var repo in (string[]) [ahead, apart, clash, onMain])
        {
            Git(repo, "switch", "-c", "develop");
            await Commit(repo, "shared.txt", "develop");
        }

        Git(apart, "switch", "main");
        await Commit(apart, "main.txt", "main");
        Git(apart, "switch", "develop");
        Git(clash, "switch", "main");
        await Commit(clash, "shared.txt", "main");
        Git(clash, "switch", "develop");
        var clashMain = Git(clash, "rev-parse", "main");
        Git(onMain, "switch", "main");
        var onMainHead = Git(onMain, "rev-parse", "HEAD");

        var reads = new[] { ahead, apart, clash, onMain }
            .Select(CodeMenu.ReadForSwitch)
            .ToList();
        var preview = CodeMenu.MergeIntoPreview(reads, "main", Cancel.None);

        await Assert.That(CodeMenu.SwitchChoices(reads).Select(_ => _.Name)).Contains("main");
        await Assert.That(preview.CanGo).IsTrue();
        await Assert.That(preview.Text).IsEqualTo(
            """
            This will merge develop into main in 2 repositories, leaving each on the branch it has checked out.

            Fast-forwarded: Ahead.
            With a merge commit: Apart.
            Would conflict, so left as they are: Clash.
            On main, so left out: OnMain.
            """.ReplaceLineEndings("\n"));

        // As the dialog runs them, once main is picked.
        var results = CodeMenu.MergeIntoTargets(reads, "main")
            .Select(_ => GitMerging.Into(_.Read.Repo, _.Read.Listing.Current!, _.Branch.Name, push: false, Cancel.None))
            .ToList();

        await Assert.That(results.Select(_ => (Path.GetFileName(_.Repo), _.Succeeded)))
            .IsEquivalentTo([("Ahead", true), ("Apart", true), ("Clash", false)], CollectionOrdering.Matching);
        await Assert.That(Git(ahead, "rev-parse", "main")).IsEqualTo(Git(ahead, "rev-parse", "develop"));
        await Assert.That(Git(apart, "log", "-1", "--format=%s", "main")).IsEqualTo("Merge branch 'develop' into main");
        await Assert.That(Git(clash, "rev-parse", "main")).IsEqualTo(clashMain);
        await Assert.That(Git(onMain, "rev-parse", "HEAD")).IsEqualTo(onMainHead);
        foreach (var repo in (string[]) [ahead, apart, clash])
        {
            await Assert.That(Git(repo, "branch", "--show-current")).IsEqualTo("develop");
        }
    }

    // The dialog for several: the branches every one has, what each would come to, and whether
    // to push.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task SeveralDialog()
    {
        GitBranches.Branch[] choices =
        [
            new("master", false, null, false),
            new("release", false, null, false)
        ];
        using var form = new QuietBranchForm(
            "Merge current branch into, in 4 repositories",
            "Merge",
            new(null, null, [], choices),
            (_, _) => new(
                """
                This will merge develop into master in 2 repositories, leaving each on the branch it has checked out.

                Fast-forwarded: Utilities.Web.
                With a merge commit: Utilities.Processes.
                Would conflict, so left as they are: Utilities.NServiceBus.
                On master, so left out: Utilities.EF.Deployment.
                """.ReplaceLineEndings("\n"),
                true),
            pushOffer: name => $"Push {name} afterwards, in each");
        form.Show();
        await Pump.Until(() => form.Previewing.IsCompleted);
        form.ActiveControl = null;
        form.Refresh();
        await Verify(form);
    }

    sealed class QuietBranchForm(string title, string accept, GitBranches.Listing listing, Func<string, Cancel, GitMerging.Preview> previewOf, Func<string, string> pushOffer) :
        BranchActionForm(title, accept, listing, previewOf, pushOffer: pushOffer)
    {
        protected override bool ShowWithoutActivation => true;
    }

    // The box for pushing says where the branch goes: the remote it tracks, or for one only on a
    // remote, that one, by the name it gets here.
    [Test]
    public async Task PushOfferNamesWhereItGoes()
    {
        GitBranches.Branch[] branches =
        [
            new("main", false, null, false, "origin/main"),
            new("upstream/release", true, null, false),
            new("local", false, null, false)
        ];

        await Assert.That(CodeMenu.PushOffer(branches, "main")).IsEqualTo("Push main to origin afterwards");
        await Assert.That(CodeMenu.PushOffer(branches, "upstream/release")).IsEqualTo("Push release to upstream afterwards");
        await Assert.That(CodeMenu.PushOffer(branches, "local")).IsEqualTo("Push local to origin afterwards");
    }
}
