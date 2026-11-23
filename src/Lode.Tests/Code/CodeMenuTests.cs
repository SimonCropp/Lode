/// <summary>
/// The code folder's right-click entries, against throwaway repositories in the temp folder.
/// </summary>
public class CodeMenuTests
{
    // On origin's default branch, as the files git keeps say, read as the menu opens rather than
    // asking git: a clone on it, or a worktree on it. Not on another branch, detached, or where
    // no default branch was recorded, as in a repository made by init and given an origin later.
    [Test]
    public async Task OnTheDefaultBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var made = code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var onMain = CodeCommands.OnDefaultBranch(clone);
        CodeCommandsTests.Scratch.Git(clone, "switch", "-c", "feature");
        var onFeature = CodeCommands.OnDefaultBranch(clone);
        var worktree = code.At("Worktree");
        CodeCommandsTests.Scratch.Git(clone, "worktree", "add", worktree, "main");
        var worktreeOnMain = CodeCommands.OnDefaultBranch(worktree);
        CodeCommandsTests.Scratch.Git(clone, "switch", "--detach");
        var detached = CodeCommands.OnDefaultBranch(clone);

        await Assert.That((onMain, onFeature, worktreeOnMain, detached)).IsEqualTo((true, false, true, false));
        await Assert.That(CodeCommands.OnDefaultBranch(made)).IsFalse();
    }

    // Switch to default branch is left off when every repository is on it, and Push when every
    // one is known to have nothing to push. Neither is left off for a folder holding repositories,
    // which can't be told without reading them all.
    [Test]
    public async Task SwitchAndPushOnlyWhenThereIsSomethingToDo()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var feature = code.Clone("Repo", "Feature");
        CodeCommandsTests.Scratch.Git(feature, "switch", "-c", "feature");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();
        var clean = new GitState("main", "abc1234", 0, 0, [new("new.txt", GitChange.Untracked)])
        {
            Upstream = true
        };
        string Offered(IReadOnlyList<string> folders, GitState? state)
        {
            var ids = CodeMenu.Commands(owner, settings, folders, _ => state)
                .Select(_ => _?.Id)
                .Where(_ => _ is "gitSwitchToDefault" or "gitPush")
                .Select(Name);
            return string.Join(", ", ids);
        }

        static string Name(string? id)
        {
            if (id == "gitPush")
            {
                return "push";
            }

            return "switch";
        }

        await Assert.That(Offered([clone], clean)).IsEqualTo("");
        await Assert.That(Offered([Path.Combine(clone, "sub")], clean)).IsEqualTo("");
        await Assert.That(Offered([feature], clean)).IsEqualTo("switch");
        await Assert.That(Offered([clone, feature], clean)).IsEqualTo("switch");
        await Assert.That(Offered([clone], clean with { Ahead = 1 })).IsEqualTo("push");
        await Assert.That(Offered([clone], clean with { Files = [new("tracked.txt", GitChange.Modified)] })).IsEqualTo("push");
        await Assert.That(Offered([clone], clean with { Upstream = false })).IsEqualTo("push");
        await Assert.That(Offered([clone], null)).IsEqualTo("push");
        await Assert.That(Offered([code.Root], clean)).IsEqualTo("switch, push");
    }

    // Tag and Delete tag for a repository, a folder in one, or several picked, but not for a
    // folder only holding repositories, where it would be the same tag in every one below it.
    [Test]
    public async Task TagOnlyForRepositories()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var other = code.Clone("Repo", "Other");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();
        bool Offered(IReadOnlyList<string> folders)
        {
            var ids = CodeMenu.Commands(owner, settings, folders)
                .Select(_ => _?.Id)
                .ToList();
            return ids.Contains("gitTag") && ids.Contains("gitDeleteTag");
        }

        await Assert.That(Offered([clone])).IsTrue();
        await Assert.That(Offered([Path.Combine(clone, "sub")])).IsTrue();
        await Assert.That(Offered([clone, other])).IsTrue();
        await Assert.That(Offered([code.Root])).IsFalse();
        await Assert.That(Offered([clone, code.Root])).IsFalse();
    }

    static GitBranches.Branch Branch(string name, bool remote = false) =>
        new(name, remote, null, false);

    static readonly GitBranches.Listing listing = new(
        "feature",
        Branch("main"),
        [Branch("feature"), Branch("fix")],
        [Branch("origin/elsewhere", remote: true)]);

    // As GitHub Desktop's: Rename only for a local branch, as one on a remote is renamed there;
    // no new worktree for the branch checked out here, which git won't check out twice.
    [Test]
    public async Task BranchEntries()
    {
        using var owner = new Control();
        string Describe(GitBranches.Branch branch) =>
            string.Join(
                ", ",
                CodeMenu.BranchCommands(owner, @"C:\Code\Repo", listing, branch, _ => { })
                    .Select(_ =>
                    {
                        if (_ == null)
                        {
                            return "----";
                        }

                        if (_.Enabled)
                        {
                            return _.Text;
                        }

                        return $"{_.Text} (disabled)";
                    }));

        await Assert.That(Describe(listing.Recent[0])).IsEqualTo("Rename…, Copy branch name, Checkout in new worktree… (disabled), ----, Delete…");
        await Assert.That(Describe(listing.Recent[1])).IsEqualTo("Rename…, Copy branch name, Checkout in new worktree…, ----, Delete…");
        await Assert.That(Describe(listing.Other[0])).IsEqualTo("Rename… (disabled), Copy branch name, Checkout in new worktree…, ----, Delete…");
    }

    // FolderViewTests swap the clipboard too.
    [Test]
    [NotInParallel("windows")]
    public async Task CopyBranchNameCopiesItAsListed()
    {
        using var owner = new Control();
        var copied = new List<string>();
        var original = FolderView.SetClipboardText;
        FolderView.SetClipboardText = copied.Add;
        try
        {
            CodeMenu.BranchCommands(owner, @"C:\Code\Repo", listing, listing.Other[0], _ => { })[1]!.Action();
        }
        finally
        {
            FolderView.SetClipboardText = original;
        }

        await Assert.That(copied).IsEquivalentTo(["origin/elsewhere"]);
    }

    // Deleting the branch checked out switches to the default branch first, or, deleting that,
    // the one switched to most recently, as GitHub Desktop does; with no other, it can't be.
    [Test]
    public async Task DeletingTheBranchCheckedOutSwitchesToTheDefault()
    {
        await Assert.That(CodeMenu.SwitchOffTo(listing, listing.Recent[0])?.Name).IsEqualTo("main");
        await Assert.That(CodeMenu.SwitchOffTo(listing with { Current = "main" }, listing.Default!)?.Name).IsEqualTo("feature");
        await Assert.That(CodeMenu.SwitchOffTo(new("main", Branch("main"), [], []), Branch("main"))).IsNull();
    }
}
