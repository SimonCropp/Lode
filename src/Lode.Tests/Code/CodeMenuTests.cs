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
    // one is known to have nothing to push, a new file being something, as Push adds it. Neither
    // is left off for a folder holding repositories, which can't be told without reading them all.
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
        var clean = new GitState("main", "abc1234", 0, 0, [])
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
        await Assert.That(Offered([clone], clean with { Files = [new("new.txt", GitChange.Untracked)] })).IsEqualTo("push");
        await Assert.That(Offered([clone], clean with { Upstream = false })).IsEqualTo("push");
        await Assert.That(Offered([clone], null)).IsEqualTo("push");
        await Assert.That(Offered([code.Root], clean)).IsEqualTo("switch, push");
    }

    // Discard is left off when every repository is known to have nothing uncommitted, a new file
    // being something, as Discard deletes it, but commits not yet pushed being nothing, as it
    // keeps them. Not left off for a folder holding repositories, nor one whose state is unread.
    [Test]
    public async Task DiscardOnlyWhenThereIsSomethingToDiscard()
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
        var clean = new GitState("main", "abc1234", 0, 0, [])
        {
            Upstream = true
        };
        var changed = clean with
        {
            Files = [new("tracked.txt", GitChange.Modified)]
        };
        bool Offered(IReadOnlyList<string> folders, Func<string, GitState?>? stateOf) =>
            CodeMenu.Commands(owner, settings, folders, stateOf).Any(_ => _?.Id == "gitDiscard");

        GitState OtherChanged(string repo)
        {
            if (repo == other)
            {
                return changed;
            }

            return clean;
        }

        await Assert.That(Offered([clone], _ => clean)).IsFalse();
        await Assert.That(Offered([Path.Combine(clone, "sub")], _ => clean)).IsFalse();
        await Assert.That(Offered([clone], _ => clean with { Ahead = 1 })).IsFalse();
        await Assert.That(Offered([clone], _ => clean with { Upstream = false })).IsFalse();
        await Assert.That(Offered([clone], _ => changed)).IsTrue();
        await Assert.That(Offered([clone], _ => clean with { Files = [new("new.txt", GitChange.Untracked)] })).IsTrue();
        await Assert.That(Offered([clone, other], OtherChanged)).IsTrue();
        await Assert.That(Offered([clone, other], _ => clean)).IsFalse();
        await Assert.That(Offered([clone], _ => null)).IsTrue();
        await Assert.That(Offered([clone], null)).IsTrue();
        await Assert.That(Offered([code.Root], _ => clean)).IsTrue();
    }

    // Sync fork for a fork, a folder in one, or several picked of which one is, and for a folder
    // holding repositories, which may hold forks; not for a repository that isn't one.
    [Test]
    public async Task SyncForkOnlyWhereThereMayBeAFork()
    {
        using var code = new CodeCommandsTests.Scratch();
        var fork = code.CreateFork("Fork");
        var repo = code.CreateRepo("Repo");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();
        bool Offered(IReadOnlyList<string> folders) =>
            CodeMenu.Commands(owner, settings, folders).Any(_ => _?.Id == "gitSyncFork");

        await Assert.That(Offered([fork])).IsTrue();
        await Assert.That(Offered([Path.Combine(fork, "sub")])).IsTrue();
        await Assert.That(Offered([fork, repo])).IsTrue();
        await Assert.That(Offered([code.Root])).IsTrue();
        await Assert.That(Offered([repo])).IsFalse();
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
        await Assert.That(Describe(listing.Recent[1])).IsEqualTo("Rename…, Copy branch name, Checkout in new worktree…, ----, Merge into feature…, Merge feature into this…, Squash and merge into feature…, Rebase feature onto this…, Compare feature to this, ----, Delete…");
        await Assert.That(Describe(listing.Other[0])).IsEqualTo("Rename… (disabled), Copy branch name, Checkout in new worktree…, ----, Merge into feature…, Merge feature into this…, Squash and merge into feature…, Rebase feature onto this…, Compare feature to this, ----, Delete…");

        // Detached, there is no branch to bring another into.
        var detached = listing with
        {
            Current = null
        };
        await Assert.That(
                string.Join(", ", CodeMenu.BranchCommands(owner, @"C:\Code\Repo", detached, listing.Recent[1], _ => { }).Select(_ => _?.Text ?? "----")))
            .IsEqualTo("Rename…, Copy branch name, Checkout in new worktree…, ----, Delete…");
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

    // Tag for a repository, a folder in one, or several picked, but not for a folder only
    // holding repositories, where it would be the same tag in every one below it.
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
        bool Offered(IReadOnlyList<string> folders) =>
            CodeMenu.Commands(owner, settings, folders).Any(_ => _?.Id == "gitTag");

        await Assert.That(Offered([clone])).IsTrue();
        await Assert.That(Offered([Path.Combine(clone, "sub")])).IsTrue();
        await Assert.That(Offered([clone, other])).IsTrue();
        await Assert.That(Offered([code.Root])).IsFalse();
        await Assert.That(Offered([clone, code.Root])).IsFalse();
    }

    // Merge, Squash and merge and Rebase pick another branch of one repository's, so are for one
    // repository only. Update from default branch is for any off it, as Switch to default is.
    [Test]
    public async Task MergeAndRebaseOnlyForOneRepository()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var other = code.Clone("Repo", "Other");
        CodeCommandsTests.Scratch.Git(other, "switch", "-c", "feature");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();
        string Offered(IReadOnlyList<string> folders) =>
            string.Join(
                ", ",
                CodeMenu.Commands(owner, settings, folders)
                    .Select(_ => _?.Id)
                    .Where(_ => _ is "gitUpdateFromDefault" or "gitMerge" or "gitSquashMerge" or "gitRebase"));

        await Assert.That(Offered([clone])).IsEqualTo("gitMerge, gitSquashMerge, gitRebase");
        await Assert.That(Offered([other])).IsEqualTo("gitUpdateFromDefault, gitMerge, gitSquashMerge, gitRebase");
        await Assert.That(Offered([clone, other])).IsEqualTo("gitUpdateFromDefault");
        await Assert.That(Offered([code.Root])).IsEqualTo("gitUpdateFromDefault");
    }

    // The branch dialog lists every branch but the one checked out, says what it would do with
    // the one picked, and can be pressed only where that can go ahead. Force push is asked only
    // where origin has commits a rebase rewrites.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task BranchDialogSaysWhatItWouldDo()
    {
        var previews = new Dictionary<string, GitMerging.Preview>
        {
            ["main"] = new("This will merge 2 commits from main into feature.", true),
            ["fix"] = new("feature is already up to date with fix.", false),
            ["origin/elsewhere"] = new("This will update feature by applying its 1 commit on top of origin/elsewhere.", true, OnOrigin: 1)
        };
        using var form = new BranchActionForm("Merge into feature", "Create a merge commit", listing, (name, _) => previews[name]);
        await Pump.Until(() => form.Previewing.IsCompleted);

        await Assert.That(form.Listed).IsEquivalentTo(["main", "fix", "origin/elsewhere"]);
        await Assert.That((form.Branch, form.PreviewText, form.CanAccept, form.ForcePush)).IsEqualTo(("main", "This will merge 2 commits from main into feature.", true, false));

        form.Filter = "fix";
        await Pump.Until(() => form.Previewing.IsCompleted);
        await Assert.That((form.Branch, form.CanAccept)).IsEqualTo(("fix", false));

        form.Filter = "else";
        await Pump.Until(() => form.Previewing.IsCompleted);
        await Assert.That((form.Branch, form.CanAccept)).IsEqualTo(("origin/elsewhere", true));
        await Assert.That(form.OffersForcePush).IsTrue();

        form.Filter = "nothing like it";
        await Assert.That((form.Branch, form.CanAccept, form.PreviewText)).IsEqualTo(((string?) null, false, ""));
    }

    // A branch right-clicked in the status bar's list is picked as the dialog opens.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task BranchDialogPicksTheBranchGiven()
    {
        using var form = new BranchActionForm("Rebase feature", "Rebase", listing, (name, _) => new($"Onto {name}", true), "fix");
        await Pump.Until(() => form.Previewing.IsCompleted);

        await Assert.That((form.Branch, form.PreviewText)).IsEqualTo(("fix", "Onto fix"));
    }

    // Tag's question offers the tag after the latest, and to commit the changes first only where
    // there are any, which needs a message: the tag, until the message is typed in itself. A
    // name with a space is made one git takes, as History's Tag makes it, saying so. It can't be
    // pressed without a name to be had, or where every repository has diverged from origin.
    [Test]
    public async Task WhatTagsQuestionTakes()
    {
        var clean = new GitState("main", "abc1234", 0, 0, [])
        {
            Upstream = true,
            Tag = "2.0.0"
        };
        var changed = clean with
        {
            Files = [new("tracked.txt", GitChange.Modified)]
        };
        var diverged = clean with
        {
            Ahead = 1,
            Behind = 1
        };

        using var plain = new CodeMenu.TagForm([new("Lode", clean, [])]);
        var name = (TextBox) plain.Controls.Find("value", true).Single();
        var accept = plain.Controls.Find("accept", true).Single();
        await Assert.That(name.Text).IsEqualTo("2.0.1");
        await Assert.That(accept.Enabled).IsTrue();
        await Assert.That(plain.Controls.Find("commitFirst", true)).IsEmpty();
        await Assert.That(plain.CommitMessage).IsNull();
        var note = plain.Controls.Find("note", true).Single();
        await Assert.That(note.Visible).IsFalse();
        name.Text = "v 1";
        await Assert.That((accept.Enabled, plain.TagName, note.Text)).IsEqualTo((true, "v-1", "Will be created as v-1."));
        name.Text = "...";
        await Assert.That(accept.Enabled).IsFalse();
        name.Text = "";
        await Assert.That(accept.Enabled).IsFalse();

        using var withChanges = new CodeMenu.TagForm([new("Lode", changed, [])]);
        var commitFirst = (CheckBox) withChanges.Controls.Find("commitFirst", true).Single();
        var message = (TextBox) withChanges.Controls.Find("message", true).Single();
        accept = withChanges.Controls.Find("accept", true).Single();
        await Assert.That((message.Enabled, withChanges.CommitMessage)).IsEqualTo((false, (string?) null));
        commitFirst.Checked = true;
        await Assert.That((message.Enabled, withChanges.CommitMessage)).IsEqualTo((true, (string?) "2.0.1"));
        var changedName = (TextBox) withChanges.Controls.Find("value", true).Single();
        changedName.Text = "v3 beta";
        await Assert.That(withChanges.CommitMessage).IsEqualTo("v3-beta");
        message.Text = " ";
        await Assert.That(accept.Enabled).IsFalse();
        message.Text = "Release";
        await Assert.That((accept.Enabled, withChanges.CommitMessage)).IsEqualTo((true, (string?) "Release"));
        changedName.Text = "3.0.0";
        await Assert.That(withChanges.CommitMessage).IsEqualTo("Release");

        using var blocked = new CodeMenu.TagForm([new("Lode", diverged, [])]);
        await Assert.That(blocked.Controls.Find("accept", true).Single().Enabled).IsFalse();
        using var partly = new CodeMenu.TagForm([new("Lode", diverged, []), new("Argon", clean, [])]);
        await Assert.That(partly.Controls.Find("accept", true).Single().Enabled).IsTrue();
    }

    // What Tag asks is text to select and copy, the commit's SHA and subject say, not a label,
    // and is as tall as it needs, wrapped, with nothing scrolled out of sight.
    [Test]
    public async Task TagsQuestionCanBeSelected()
    {
        using var form = new QuietTagForm(
        [
            new(
                @"C:\Code\NServiceBus.Community.SqlNative",
                new("main", "eaae4361234", 0, 0, [new("tracked.txt", GitChange.Modified)])
                {
                    Upstream = true,
                    Tag = "17.1.0"
                },
                [],
                "refs or cleanup")
        ]);
        form.Show();
        Application.DoEvents();
        var question = (TextBox) form.Controls.Find("question", true).Single();
        question.SelectAll();

        await Assert.That(question.ReadOnly).IsTrue();
        await Assert.That(question.SelectedText).Contains("eaae436 refs or cleanup");
        await Assert.That(question.GetPositionFromCharIndex(question.TextLength - 1).Y).IsLessThan(question.ClientSize.Height);
    }

    // History's Tag and Delete tag say which commit, its SHA and subject, selectable as Tag's is.
    [Test]
    public async Task HistorysTagQuestionsCanBeSelected()
    {
        var repo = @"C:\Code\Lode";
        var commit = new GitCommit("eaae4361234", [], "Ada", DateTimeOffset.UnixEpoch, ["v1.0"], "refs or cleanup");

        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit)).IsEqualTo("Tag this commit in Lode, and push the tag to origin:\neaae436 refs or cleanup");
        await Assert.That(CodeMenu.DeleteTagQuestion(repo, commit)).IsEqualTo("Delete a tag of this commit from Lode, and from origin:\neaae436 refs or cleanup\n\nThis cannot be undone.");

        using var form = new CodeMenu.GitTextForm("Git delete tag", CodeMenu.DeleteTagQuestion(repo, commit), "Tag:", "v1.0", "Delete", commit.Tags, isTagName: true);
        _ = form.Handle;
        var question = (TextBox) form.Controls.Find("question", true).Single();
        _ = question.Handle;
        question.SelectAll();

        await Assert.That(question.ReadOnly).IsTrue();
        await Assert.That(question.SelectedText).Contains("eaae436 refs or cleanup");
        await Assert.That(question.GetPositionFromCharIndex(question.TextLength - 1).Y).IsLessThan(question.ClientSize.Height);
    }

    // History's Tag says what goes with the tag, as Tag does: the commit, and those before it,
    // where origin's branch hasn't got them, a branch origin hasn't got, or a branch diverged,
    // left till pulled. One on origin's already says nothing more.
    [Test]
    public async Task HistorysTagQuestionSaysWhatGoesWithTheTag()
    {
        var repo = @"C:\Code\Lode";
        var commit = new GitCommit("eaae4361234", [], "Ada", DateTimeOffset.UnixEpoch, [], "refs or cleanup");
        var asked = "Tag this commit in Lode, and push the tag to origin:\neaae436 refs or cleanup";

        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit, new(null, false, null, "main")))
            .IsEqualTo(asked);
        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit, new("eaae4361234:refs/heads/main", false, null, "main", 1)))
            .IsEqualTo($"{asked}\n\nIt isn't pushed yet, so it goes with the tag, and origin's main has the commit tagged.");
        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit, new("eaae4361234:refs/heads/main", false, null, "main", 3)))
            .IsEqualTo($"{asked}\n\nIt and the 2 commits before it not yet pushed go with the tag, so origin's main has the commit tagged.");
        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit, new("eaae4361234:refs/heads/feature", true, null, "feature")))
            .IsEqualTo($"{asked}\n\nfeature goes to origin with the tag, up to this commit, as origin hasn't got it yet.");
        await Assert.That(CodeMenu.TagCommitQuestion(repo, commit, new(null, false, new(repo, false, "Pull first."), "main", 1)))
            .IsEqualTo($"{asked}\n\nmain and origin's have each got commits the other hasn't, so it can't be tagged till pulled.");
    }

#if DEBUG
    [Test]
    public async Task TagsQuestion()
    {
        using var form = new QuietTagForm(
        [
            new(
                @"C:\Code\NServiceBus.Community.SqlNative",
                new("main", "eaae4361234", 1, 0, [new("tracked.txt", GitChange.Modified)])
                {
                    Upstream = true,
                    Tag = "17.1.0"
                },
                [],
                "refs or cleanup")
        ]);
        form.Show();
        Application.DoEvents();
        form.Refresh();
        await Verify(form);
    }

    // As the readme shows them: main checked out, its branches to pick from, and what the one
    // picked would do.
    static readonly GitBranches.Listing readmeListing = new(
        "main",
        Branch("main"),
        [Branch("feature/pin-network-folders"), Branch("fix/drive-letters")],
        [Branch("docs/clone-dialog"), Branch("origin/release-1.0", remote: true)]);

    // Merge into current branch, a branch picked, saying how many commits it brings in.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public Task MergeDialog() =>
        CaptureBranchDialog(
            CodeMenu.MergeTitle(GitMerging.Kind.Merge, "main"),
            "Create a merge commit",
            new("This will merge 3 commits from feature/pin-network-folders into main.", true));

    // Rebase current branch, onto a branch origin's copy of the one checked out is behind:
    // force push offered.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public Task RebaseDialog() =>
        CaptureBranchDialog(
            CodeMenu.MergeTitle(GitMerging.Kind.Rebase, "main"),
            "Rebase",
            new("This will update main by applying its 2 commits on top of feature/pin-network-folders. Should one conflict, the rebase is undone.\n\nOrigin has 2 commits of main that this rewrites, so pushing it afterwards needs force, and anyone else's copy of main is left behind.", true, OnOrigin: 2));

    static async Task CaptureBranchDialog(string title, string accept, GitMerging.Preview preview)
    {
        using var form = new QuietBranchForm(title, accept, readmeListing, (_, _) => preview);
        form.Show();
        await Pump.Until(() => form.Previewing.IsCompleted);
        form.ActiveControl = null;
        form.Refresh();
        await Verify(form);
    }

    sealed class QuietBranchForm(string title, string accept, GitBranches.Listing listing, Func<string, Cancel, GitMerging.Preview> previewOf) :
        BranchActionForm(title, accept, listing, previewOf)
    {
        protected override bool ShowWithoutActivation => true;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartPosition = FormStartPosition.Manual;
            Location = new(-20000, -20000);
        }
    }
#endif

    sealed class QuietTagForm(IReadOnlyList<CodeMenu.TagRead> reads) :
        CodeMenu.TagForm(reads)
    {
        protected override bool ShowWithoutActivation => true;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartPosition = FormStartPosition.Manual;
            Location = new(-20000, -20000);
        }
    }
}
