/// <summary>
/// The right-click entries for folders under the code folder: git fetch, pull, sync fork, switch
/// to the default branch, update from it, merge, squash and merge, rebase, push, tag, discard and purge across their repositories, and Update NuGets (optionally
/// then building). With several folders selected, each command takes in all of them at once,
/// as one entry in the actions panel. Each runs in the background and shows in the actions panel under the tree, without
/// holding up the rest of Lode. Push, discard and purge ask first.
/// </summary>
static class CodeMenu
{
    const string defaultCommitMessage = "refs or cleanup";

    /// <summary>
    /// The entries for the folders, after a separator, or none unless every one is under the
    /// code folder. Switch to default branch is left off when every repository is on it, Push
    /// when every one has nothing to push, and Discard when every one has nothing uncommitted,
    /// as <paramref name="stateOf"/> has it, which gives a repository's state where it has been
    /// read. Sync fork only where there may be a fork: a repository with an upstream remote, or a
    /// folder holding repositories. Tag only for repositories, not a folder holding them. Update
    /// from default branch goes with Switch to default branch, and Merge, Squash and merge and
    /// Rebase are for one repository only, as they pick another of its branches.
    /// Deleting a tag is History's, on the commit picked.
    /// </summary>
    public static List<QuickMenu.Command?> Commands(Control owner, Settings settings, IReadOnlyList<string> folders, Func<string, GitState?>? stateOf = null)
    {
        if (folders.Count == 0 ||
            !folders.All(settings.IsCode))
        {
            return [];
        }

        // Each folder's repository. A folder in none acts on every repository below it, which
        // there is no telling about without reading them, so everything is offered.
        var repos = folders
            .Select(_ => CodeCommands.RepoContaining(_, settings.CodeDirectory ?? _))
            .ToList();
        var allInRepos = repos.All(_ => _ != null);
        var onDefault = allInRepos &&
                        repos.All(_ => CodeCommands.OnDefaultBranch(_!));
        var nothingToPush = allInRepos &&
                            stateOf != null &&
                            repos.All(_ => stateOf(_!) is { NothingToPush: true });
        // Discard resets what is tracked and deletes what is new but not ignored: the files the
        // state lists.
        var nothingToDiscard = allInRepos &&
                               stateOf != null &&
                               repos.All(_ => stateOf(_!) is { Changes: 0 });

        List<QuickMenu.Command?> commands =
        [
            null,
            LodeActions.Command("gitFetch", () => Fetch(folders, settings.CodeDirectory)),
            LodeActions.Command("gitPull", () => Pull(folders, settings.CodeDirectory))
        ];

        // A folder only holding repositories may hold forks.
        if (!allInRepos ||
            repos.Any(_ => CodeCommands.IsFork(_!)))
        {
            commands.Add(LodeActions.Command("gitSyncFork", () => SyncFork(folders, settings.CodeDirectory)));
        }

        if (!onDefault)
        {
            commands.Add(LodeActions.Command("gitSwitchToDefault", () => SwitchToDefaultBranch(folders, settings.CodeDirectory)));
            commands.Add(LodeActions.Command("gitUpdateFromDefault", () => UpdateFromDefault(folders, settings.CodeDirectory)));
        }

        // Another branch is picked of one repository's, so these are for one repository only.
        if (allInRepos &&
            repos.Count == 1)
        {
            var repo = repos[0]!;
            commands.AddRange(
            [
                LodeActions.Command("gitMerge", () => MergeBranch(owner, repo, GitMerging.Kind.Merge), text: "Git merge into current branch…"),
                LodeActions.Command("gitSquashMerge", () => MergeBranch(owner, repo, GitMerging.Kind.Squash), text: "Git squash and merge into current branch…"),
                LodeActions.Command("gitRebase", () => MergeBranch(owner, repo, GitMerging.Kind.Rebase), text: "Git rebase current branch…")
            ]);
        }

        if (!nothingToPush)
        {
            commands.Add(LodeActions.Command("gitPush", () => Push(owner, folders, settings.CodeDirectory), text: "Git push…"));
        }

        // A tag names a commit of a repository: for a folder only holding repositories, it would
        // be the same tag in every one below it, which is never what is meant.
        if (allInRepos)
        {
            commands.Add(LodeActions.Command("gitTag", () => Tag(owner, folders, settings.CodeDirectory), text: "Git tag…"));
        }

        if (!nothingToDiscard)
        {
            commands.Add(LodeActions.Command("gitDiscard", () => Discard(owner, folders, settings.CodeDirectory), text: "Git discard…"));
        }

        commands.AddRange(
        [
            LodeActions.Command("gitPurge", () => Purge(owner, settings, folders), text: "Git purge…"),
            LodeActions.Command("updateNugets", () => UpdateNugets(folders)),
            LodeActions.Command("updateNugetsAndBuild", () => UpdateNugetsAndBuild(folders))
        ]);
        return commands;
    }

    // What origin has now, for every repository, without changing any branch: the Git column's
    // ahead and behind are as of the last fetch. They are read again as it ends, rather than
    // once the watch has heard of each.
    static void Fetch(IReadOnlyList<string> folders, string? codeFolder) =>
        _ = ActionLog.Run(
            "Git fetch",
            folders,
            async (progress, cancel) =>
            {
                var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder), cancel);
                var results = await CodeCommands.Fetch(repos, progress, cancel);
                GitCache.MarkStale(repos);
                return CodeCommands.GitOutcome(results);
            });

    // Each runs in the background and shows in the actions panel; Lode stays usable. A folder
    // inside a repository, src say, acts on that repository.
    static void Pull(IReadOnlyList<string> folders, string? codeFolder) =>
        _ = ActionLog.Run(
            "Git pull",
            folders,
            async (progress, cancel) =>
            {
                // Looking for repositories reads folders, so that is off the UI thread too.
                var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder), cancel);
                return CodeCommands.GitOutcome(await CodeCommands.Pull(repos, progress, cancel));
            });

    // Only the forks among the repositories, a fork being one with an upstream remote. Nothing to
    // ask about, as Pull asks nothing: it merges and pushes only to the user's own fork, and a
    // merge that would conflict is undone. Read again as it ends, as Fetch's are, for how far
    // behind upstream each is.
    static void SyncFork(IReadOnlyList<string> folders, string? codeFolder) =>
        _ = ActionLog.Run(
            "Git sync fork",
            folders,
            async (progress, cancel) =>
            {
                var forks = await Task.Run(
                    () => CodeCommands
                        .FindRepos(folders, codeFolder)
                        .Where(CodeCommands.IsFork)
                        .ToList(),
                    cancel);
                var results = await CodeCommands.SyncFork(forks, progress, cancel);
                GitCache.MarkStale(forks);
                return CodeCommands.GitOutcome(results, "No forks: no repository has an upstream remote");
            });

    // Nothing to ask about: a repository with uncommitted changes fails rather than switching,
    // so nothing is lost.
    static void SwitchToDefaultBranch(IReadOnlyList<string> folders, string? codeFolder) =>
        _ = ActionLog.Run(
            "Git switch to default branch",
            folders,
            async (progress, cancel) =>
            {
                var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder), cancel);
                return CodeCommands.GitOutcome(await CodeCommands.SwitchToDefaultBranch(repos, progress, cancel));
            });

    // Nothing to ask about, as Pull asks nothing: it merges without pushing, and a merge that
    // would conflict is undone. Read again as it ends, for how far ahead each branch is now.
    static void UpdateFromDefault(IReadOnlyList<string> folders, string? codeFolder) =>
        _ = ActionLog.Run(
            "Git update from default branch",
            folders,
            async (progress, cancel) =>
            {
                var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder), cancel);
                var results = await GitMerging.UpdateFromDefault(repos, progress, cancel);
                GitCache.MarkStale(repos);
                return CodeCommands.GitOutcome(results);
            });

    /// <summary>
    /// Merge, Squash and merge, or Rebase: asks which branch, <paramref name="branch"/> picked
    /// first where given, one right-clicked in the status bar's branches say, saying what it
    /// would do with it, then does it in the Actions panel. Detached, there is no branch to
    /// bring another into.
    /// </summary>
    internal static async void MergeBranch(Control owner, string repo, GitMerging.Kind kind, string? branch = null)
    {
        var listing = await Task.Run(() => GitBranches.Read(repo, Cancel.None));
        if (owner.IsDisposed ||
            OnBranch(owner, listing, MergeTitle(kind, "the branch")) is not { } current)
        {
            return;
        }

        var accept = kind switch
        {
            GitMerging.Kind.Squash => "Squash and merge",
            GitMerging.Kind.Rebase => "Rebase",
            _ => "Create a merge commit"
        };
        using var form = new BranchActionForm(
            MergeTitle(kind, current),
            accept,
            listing,
            (name, cancel) => GitMerging.Of(repo, current, name, kind, cancel),
            branch);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
            form.Branch is not { } picked)
        {
            return;
        }

        var forcePush = form.ForcePush;
        if (kind == GitMerging.Kind.Rebase)
        {
            _ = RunOnBranch($"Git rebase {current} onto {picked}", repo, cancel => GitMerging.Rebase(repo, picked, forcePush, cancel));
            return;
        }

        var squash = kind == GitMerging.Kind.Squash;
        var title = $"Git merge {picked} into {current}";
        if (squash)
        {
            title = $"Git squash and merge {picked} into {current}";
        }

        await RunOnBranch(title, repo, cancel => GitMerging.Merge(repo, picked, squash, cancel));

        // Left part way by conflicts, Changes slides out, where they are settled.
        if (!owner.IsDisposed &&
            owner.FindForm() is MainForm window &&
            GitChanges.IsMerging(repo) &&
            ChangesPane.SameRepo(window.GitPanes.Repo, repo))
        {
            window.GitPanes.Float(GitPane.Changes, focus: true);
        }
    }

    /// <summary>
    /// What Merge, Squash and merge, and Rebase dialogs are called, as GitHub Desktop's are.
    /// </summary>
    internal static string MergeTitle(GitMerging.Kind kind, string current) =>
        kind switch
        {
            GitMerging.Kind.Squash => $"Squash and merge into {current}",
            GitMerging.Kind.Rebase => $"Rebase {current}",
            _ => $"Merge into {current}"
        };

    // The branch checked out, or null, having said why there is none: detached, or the branches
    // couldn't be read.
    static string? OnBranch(Control owner, GitBranches.Listing listing, string caption)
    {
        if (listing.Error != null)
        {
            MessageBox.Show(owner, $"Couldn't read the branches:\n{listing.Error}", caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        if (listing.Current == null)
        {
            MessageBox.Show(owner, "No branch is checked out. Check one out first.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return listing.Current;
    }

    /// <summary>
    /// Compare to branch: asks which branch, unless <paramref name="branch"/> is given, then has
    /// History list how the branch checked out and it differ. Only for the repository the
    /// window's panes show, as History shows only that.
    /// </summary>
    internal static async void CompareToBranch(Control owner, string repo, string? branch = null)
    {
        if (owner.FindForm() is not MainForm window)
        {
            return;
        }

        if (branch == null)
        {
            var listing = await Task.Run(() => GitBranches.Read(repo, Cancel.None));
            if (owner.IsDisposed ||
                OnBranch(owner, listing, "Compare to branch") is not { } current)
            {
                return;
            }

            using var form = new BranchActionForm(
                "Compare to branch",
                "Compare",
                listing,
                (name, cancel) => ComparePreview(repo, current, name, cancel));
            if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
                form.Branch is not { } picked)
            {
                return;
            }

            branch = picked;
        }

        if (!window.GitPanes.Compare(repo, branch))
        {
            MessageBox.Show(owner, $"History shows the repository open here. Open {Path.GetFileName(repo)} to compare its branches.", "Compare to branch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // What comparing would show: how many commits each has that the other hasn't.
    static GitMerging.Preview ComparePreview(string repo, string current, string branch, Cancel cancel)
    {
        if (GitHistory.Apart(repo, branch, cancel) is not { } apart)
        {
            return new($"Couldn't compare {current} with {branch}.", false);
        }

        return new($"{current} is {GitState.CountOf(apart.Behind, "commit")} behind {branch}, and {GitState.CountOf(apart.Ahead, "commit")} ahead of it.", true);
    }

    /// <summary>
    /// Switches the repository to <paramref name="target"/>, picked from the status bar's
    /// branches. With anything uncommitted, as <paramref name="state"/> has it, asks first, as
    /// GitHub Desktop does, whether to leave the changes on the branch, stashed till it is come
    /// back to, or bring them along. Detached, there is no branch to leave them on, so they come.
    /// </summary>
    public static void SwitchBranch(Control owner, string repo, GitState? state, GitBranches.Branch target)
    {
        var current = state?.Branch;
        var leave = false;
        if (current != null &&
            state is { Changes: > 0 })
        {
            if (AskLeaveChanges(owner, current, target.Name) is not { } answer)
            {
                return;
            }

            leave = answer;
        }

        _ = RunOnBranch($"Git switch to {target.Name}", repo, cancel => GitBranches.Switch(repo, current, target, leave, cancel));
    }

    // In the Actions panel, off the UI thread. The repository is marked stale before the entry
    // ends, which has the windows read the states again: the watch may not have heard of the
    // change yet, and the state would still be fresh.
    static Task<ActionLog.Entry> RunOnBranch(string title, string repo, Func<Cancel, CodeCommands.RepoResult> work, bool stays = false) =>
        ActionLog.Run(
            title,
            [repo],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => work(cancel), cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome([result]) with
                {
                    Stays = stays
                };
            });

    /// <summary>
    /// Asks whether to leave the changes on the branch checked out or bring them to the one
    /// switched to: true to leave them, null when cancelled. Tests answer for the user.
    /// </summary>
    internal static Func<Control, string, string, bool?> AskLeaveChanges = (owner, current, target) =>
    {
        var leave = new TaskDialogCommandLinkButton(
            $"Leave my changes on {current}",
            $"They are stashed, and put back when you switch to {current} again from the status bar.");
        var bring = new TaskDialogCommandLinkButton(
            $"Bring my changes to {target}",
            "They come along. If git would have to overwrite any of them, nothing is switched.");
        var page = new TaskDialogPage
        {
            Caption = "Switch branch",
            Heading = $"There are uncommitted changes on {current}",
            Text = "Where should they go?",
            Buttons =
            {
                leave,
                bring,
                TaskDialogButton.Cancel
            },
            DefaultButton = leave,
            AllowCancel = true
        };
        var answer = TaskDialog.ShowDialog(owner, page);
        if (answer == leave)
        {
            return true;
        }

        if (answer == bring)
        {
            return false;
        }

        return null;
    };

    /// <summary>
    /// Asks for the name of a new branch, starting from <paramref name="name"/>, then makes it at
    /// the commit checked out and switches to it. What is uncommitted comes along.
    /// </summary>
    public static async void NewBranch(Control owner, string repo, string name)
    {
        using var form = new GitTextForm(
            "New branch",
            $"Make a branch in {Path.GetFileName(repo)} at the commit checked out, and switch to it. Uncommitted changes come along.",
            "Name:",
            name,
            "Create branch",
            [],
            namedAs: "Will be created as");
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var branch = form.Value;
        _ = RunOnBranch($"Git new branch {branch}", repo, cancel => GitBranches.Create(repo, branch, cancel));
    }

    /// <summary>
    /// The right-click entries for a branch in the status bar's branches, as GitHub Desktop's:
    /// Rename… for a local one, Copy branch name, Checkout in new worktree… for any but the one
    /// checked out here, which git won't check out twice, then Delete…. Once made, the worktree
    /// is opened with <paramref name="openFolder"/>.
    /// </summary>
    public static List<QuickMenu.Command?> BranchCommands(Control owner, string repo, GitBranches.Listing listing, GitBranches.Branch branch, Action<string> openFolder)
    {
        var current = !branch.Remote &&
                      branch.Name == listing.Current;
        List<QuickMenu.Command?> commands =
        [
            new("Rename…", () => RenameBranch(owner, repo, branch), Enabled: !branch.Remote),
            new("Copy branch name", () => FolderView.SetClipboardText(branch.Name)),
            new("Checkout in new worktree…", () => AddWorktree(owner, repo, branch, openFolder), Enabled: !current)
        ];

        // Another branch, with one checked out to bring it into, or compare it with.
        if (!current &&
            listing.Current is { } checkedOut)
        {
            commands.AddRange(
            [
                null,
                new($"Merge into {checkedOut}…", () => MergeBranch(owner, repo, GitMerging.Kind.Merge, branch.Name))
                {
                    Glyph = Icons.Branch
                },
                new($"Squash and merge into {checkedOut}…", () => MergeBranch(owner, repo, GitMerging.Kind.Squash, branch.Name))
                {
                    Glyph = Icons.Squash
                },
                new($"Rebase {checkedOut} onto this…", () => MergeBranch(owner, repo, GitMerging.Kind.Rebase, branch.Name))
                {
                    Glyph = Icons.Rebase
                },
                new($"Compare {checkedOut} to this", () => CompareToBranch(owner, repo, branch.Name))
                {
                    Glyph = Icons.History
                }
            ]);
        }

        commands.Add(null);
        commands.Add(new("Delete…", () => DeleteBranch(owner, repo, listing, branch)));
        return commands;
    }

    static async void RenameBranch(Control owner, string repo, GitBranches.Branch branch)
    {
        var description = $"Rename {branch.Name} in {Path.GetFileName(repo)}.";
        if (branch.Upstream != null)
        {
            description += $" The branch it tracks, {branch.Upstream}, keeps its name.";
        }

        using var form = new GitTextForm("Rename branch", description, "Name:", branch.Name, "Rename", [], namedAs: "Will be renamed to");
        if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
            form.Value == branch.Name)
        {
            return;
        }

        var name = form.Value;
        _ = RunOnBranch($"Git rename {branch.Name} to {name}", repo, cancel => GitBranches.Rename(repo, branch.Name, name, cancel));
    }

    // Asks where, beside the repository unless told otherwise, then opens the worktree once it
    // is made: in a tab, as a repository in the code folder, so its branch is in the status bar.
    static async void AddWorktree(Control owner, string repo, GitBranches.Branch branch, Action<string> openFolder)
    {
        using var form = new GitTextForm(
            "Checkout in new worktree",
            $"Check {branch.Name} out in a folder of its own, sharing {Path.GetFileName(repo)}'s commits and branches, and open it.",
            "Folder:",
            GitBranches.WorktreeFolder(repo, branch),
            "Create worktree",
            []);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var folder = Path.GetFullPath(form.Value, Path.GetDirectoryName(repo) ?? repo);
        var entry = await RunOnBranch($"Git worktree for {branch.Name}", repo, cancel => GitBranches.AddWorktree(repo, branch, folder, cancel));
        if (entry.State == ActionLog.State.Succeeded &&
            !owner.IsDisposed)
        {
            openFolder(folder);
        }
    }

    // Asks first, as it can't be undone. The branch checked out is switched off first, to the
    // default branch, or else the one switched to most recently, as GitHub Desktop does.
    static void DeleteBranch(Control owner, string repo, GitBranches.Listing listing, GitBranches.Branch branch)
    {
        GitBranches.Branch? switchTo = null;
        if (!branch.Remote &&
            branch.Name == listing.Current)
        {
            switchTo = SwitchOffTo(listing, branch);
            if (switchTo == null)
            {
                MessageBox.Show(owner, $"{branch.Name} is the only branch, so can't be deleted.", "Delete branch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        if (AskDelete(owner, branch, switchTo) is not { } alsoOnRemote)
        {
            return;
        }

        // A local branch's entry stays until cleared, rather than leaving after a minute as one
        // that went well does: the commit the branch was at, which is what gets it back, is
        // nowhere else, and the prompt says it is there.
        _ = RunOnBranch(
            $"Git delete branch {branch.Name}",
            repo,
            cancel => GitBranches.Delete(repo, branch, alsoOnRemote, switchTo, cancel),
            stays: !branch.Remote);
    }

    /// <summary>
    /// The branch to switch to before deleting <paramref name="branch"/>, the one checked out:
    /// the default branch, else the one switched to most recently, else any other. Null when
    /// it is the only one.
    /// </summary>
    internal static GitBranches.Branch? SwitchOffTo(GitBranches.Listing listing, GitBranches.Branch branch)
    {
        if (listing.Default is { } defaultBranch &&
            defaultBranch.Name != branch.Name)
        {
            return defaultBranch;
        }

        return listing.Recent
            .Concat(listing.Other)
            .FirstOrDefault(_ => _.Name != branch.Name);
    }

    /// <summary>
    /// Asks whether to delete <paramref name="branch"/>, switching first to
    /// <paramref name="switchTo"/> where that isn't null: null when not, otherwise whether to
    /// delete the branch it tracks on the remote too. Tests answer for the user.
    /// </summary>
    internal static Func<Control, GitBranches.Branch, GitBranches.Branch?, bool?> AskDelete = (owner, branch, switchTo) =>
    {
        var heading = $"Delete branch {branch.Name}?";
        var text = "This can't be undone. The commit it was at is in the Actions panel's details.";
        if (branch.Remote)
        {
            heading = $"Delete {branch.Name} from {branch.Name[..branch.Name.IndexOf('/')]}?";
            text = "This can't be undone.";
        }

        if (switchTo != null)
        {
            text = $"It's checked out, so {switchTo.Name} will be, first. {text}";
        }

        var delete = new TaskDialogButton("Delete");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "Delete branch",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Warning,
            Buttons =
            {
                delete,
                cancel
            },
            DefaultButton = cancel,
            AllowCancel = true
        };
        if (branch is {Remote: false, Upstream: not null})
        {
            page.Verification = new($"Also delete {branch.Upstream}");
        }

        if (TaskDialog.ShowDialog(owner, page) != delete)
        {
            return null;
        }

        return page.Verification?.Checked == true;
    };

    // Looking for the repositories reads two levels of folders, so that is off the UI thread, as
    // Pull's is, and Discard's. Then it asks for the commit message, where there is anything to
    // commit, or only whether to push, where there isn't.
    internal static async void Push(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var (repos, newFiles, changed) = await Task.Run(
            () =>
            {
                var found = CodeCommands.FindRepos(folders, codeFolder);
                return (found, CodeCommands.NewFiles(found), CodeCommands.AnyChanges(found));
            });
        if (repos.Count == 0)
        {
            NoRepos("Git push", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        string? message = null;
        if (changed)
        {
            message = AskCommitMessage(owner, repos, newFiles);
            if (message == null)
            {
                return;
            }
        }
        else if (MessageBox.Show(owner, PushOnlyQuestion(repos), "Git push", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git push",
            folders,
            async (progress, cancel) => CodeCommands.GitOutcome(await CodeCommands.Push(repos, message, progress, cancel)));
    }

    /// <summary>
    /// What Tag knows of a repository as it asks: its state, the tags its commit checked out has
    /// already, and the commit's subject. A state that couldn't be read is null, and says nothing.
    /// </summary>
    internal sealed record TagRead(string Repo, GitState? State, IReadOnlyList<string> AtHead, string? Subject = null)
    {
        /// <summary>
        /// The commit checked out, as History shows it: its short SHA, then its subject. Null
        /// before the first commit, or where the state couldn't be read.
        /// </summary>
        public string? Commit
        {
            get
            {
                if (State?.Commit is not { } sha)
                {
                    return null;
                }

                var shortSha = sha[..Math.Min(7, sha.Length)];
                if (string.IsNullOrEmpty(Subject))
                {
                    return shortSha;
                }

                return $"{shortSha} {Subject}";
            }
        }
    }

    // Reads each repository afresh, off the UI thread, as what the menu knew may be a while old,
    // then asks for the tag's name, saying what goes with it.
    static async void Tag(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var reads = await Task.Run(
            () => CodeCommands.InParallel(
                CodeCommands.FindRepos(folders, codeFolder),
                ReadForTag));
        if (reads.Count == 0)
        {
            NoRepos("Git tag", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        using var form = new TagForm(reads);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var name = form.TagName;
        var message = form.CommitMessage;
        var repos = reads
            .Select(_ => _.Repo)
            .ToList();
        _ = ActionLog.Run(
            "Git tag",
            folders,
            async (progress, cancel) =>
            {
                var results = await CodeCommands.Tag(repos, name, progress, cancel, message);
                GitCache.MarkStale(repos);
                return CodeCommands.GitOutcome(results);
            });
    }

    internal static TagRead ReadForTag(string repo)
    {
        var state = GitState.FromGit(repo, Cancel.None);
        var atHead = CodeCommands.Git(repo, "tag", "--points-at", "HEAD");
        List<string> tags = [];
        if (atHead.Succeeded)
        {
            tags = [.. atHead.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        string? subject = null;
        var log = CodeCommands.Git(repo, "log", "-1", "--format=%s", "HEAD");
        if (log.Succeeded)
        {
            subject = log.Output.Trim();
        }

        return new(repo, state, tags, subject);
    }

    /// <summary>
    /// Whether the branch checked out has diverged from the one it tracks, as of the last fetch:
    /// Tag leaves it, as origin would refuse the push.
    /// </summary>
    internal static bool Diverged(GitState? state) =>
        state is
        {
            Upstream: true,
            Ahead: > 0,
            Behind: > 0
        };

    /// <summary>
    /// What Tag asks: what it tags, each commit with its short SHA and subject, then what else goes, or doesn't: commits not yet pushed,
    /// which go with the tag, a branch origin hasn't got, which goes too, no branch, so the tag
    /// goes alone, a branch diverged from origin's, left till pulled, the tags the commit has
    /// already, and changes not committed, which aren't in the tag unless committed first.
    /// </summary>
    internal static string TagQuestion(IReadOnlyList<TagRead> reads)
    {
        var lines = new List<string>();
        if (reads.Count == 1)
        {
            var read = reads[0];
            var name = Path.GetFileName(read.Repo);
            if (read.Commit is { } commit)
            {
                lines.Add($"Tag the commit {name} has checked out, and push the tag to origin:");
                lines.Add(commit);
            }
            else
            {
                lines.Add($"Tag the commit {name} has checked out, and push the tag to origin.");
            }

            if (read.State is { } state)
            {
                if (Diverged(state))
                {
                    lines.Add($"{state.Branch} and origin's have each got commits the other hasn't, so it can't be tagged till pulled.");
                }
                else if (state is { Upstream: true, Ahead: > 0 })
                {
                    var go = state.Ahead == 1 ? "goes" : "go";
                    lines.Add($"Its {GitState.CountOf(state.Ahead, "commit")} not yet pushed {go} with the tag, so origin's {state.Branch} has the commit tagged.");
                }
                else if (state is { Upstream: false, Branch: { } branch, Commit: not null })
                {
                    lines.Add($"{branch} goes to origin with the tag, as origin hasn't got it yet.");
                }
                else if (state is { Branch: null, Commit: not null })
                {
                    lines.Add("No branch is checked out, so the tag goes alone.");
                }

                if (state.Changes > 0)
                {
                    lines.Add($"Its {GitState.CountOf(state.Changes, "uncommitted change")} won't be in the tag unless committed first, when the tag goes on the new commit instead.");
                }
            }

            if (read.AtHead.Count > 0)
            {
                lines.Add($"The commit is tagged {string.Join(", ", read.AtHead)} already.");
            }

            return string.Join("\n\n", lines);
        }

        // Each repository with its commit, as many as fit, rather than Describe's names alone.
        lines.Add($"Tag the commit each of {reads.Count} repositories has checked out, and push the tag to origin:");
        var listed = reads
            .Take(8)
            .Select(_ =>
            {
                var name = Path.GetFileName(_.Repo);
                if (_.Commit is { } commit)
                {
                    return $"{name}: {commit}";
                }

                return name;
            })
            .ToList();
        if (reads.Count > 8)
        {
            listed.Add($"and {reads.Count - 8} more");
        }

        lines.Add(string.Join("\n", listed));
        void Note(Func<TagRead, bool> applies, string one, string several)
        {
            var names = reads
                .Where(applies)
                .Select(_ => Path.GetFileName(_.Repo))
                .ToList();
            if (names.Count == 0)
            {
                return;
            }

            var list = names[0];
            if (names.Count > 1)
            {
                list = $"{string.Join(", ", names[..^1])} and {names[^1]}";
            }

            if (names.Count == 1)
            {
                lines.Add($"{list} {one}");
            }
            else
            {
                lines.Add($"{list} {several}");
            }
        }

        Note(_ => Diverged(_.State), "has diverged from origin, so won't be tagged till pulled.", "have diverged from origin, so won't be tagged till pulled.");
        Note(_ => !Diverged(_.State) && _.State is { Upstream: true, Ahead: > 0 }, "has commits not yet pushed, which go with the tag.", "have commits not yet pushed, which go with the tag.");
        Note(_ => _.State is { Upstream: false, Branch: not null, Commit: not null }, "is on a branch origin hasn't got yet, which goes with the tag.", "are on branches origin hasn't got yet, which go with the tag.");
        Note(_ => _.State is { Branch: null, Commit: not null }, "has no branch checked out, so the tag goes alone.", "have no branch checked out, so the tag goes alone.");
        Note(_ => _.AtHead.Count > 0, "is tagged already.", "are tagged already.");
        Note(_ => _.State is { Changes: > 0 }, "has uncommitted changes, which won't be in the tag unless committed first.", "have uncommitted changes, which won't be in the tag unless committed first.");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// The tag to offer: the one after each repository's most recent, 2.0.1 after 2.0.0, v1.3
    /// after v1.2, where it is the same for every one. Nothing where they differ, or a tag isn't
    /// numbers and dots, a pre-release say, whose next there is no telling.
    /// </summary>
    internal static string SuggestTag(IReadOnlyList<TagRead> reads)
    {
        var next = reads
            .Select(_ => NextTag(_.State?.Tag))
            .Distinct()
            .ToList();
        if (next is [{ } only])
        {
            return only;
        }

        return "";
    }

    internal static string? NextTag(string? tag)
    {
        if (tag == null)
        {
            return null;
        }

        var prefix = "";
        var numbers = tag;
        if (tag.StartsWith('v') ||
            tag.StartsWith('V'))
        {
            prefix = tag[..1];
            numbers = tag[1..];
        }

        var parts = numbers.Split('.');
        if (parts.Length == 0 ||
            !parts.All(_ => _.Length > 0 && _.All(char.IsAsciiDigit)) ||
            !long.TryParse(parts[^1], out var last))
        {
            return null;
        }

        parts[^1] = (last + 1).ToString(CultureInfo.InvariantCulture);
        return prefix + string.Join('.', parts);
    }

    /// <summary>
    /// History's Tag: asks for the tag's name, then tags the commit picked, rather than the one
    /// checked out, and pushes the tag. The repository is read again once it is done, for History
    /// to show the tag.
    /// </summary>
    internal static async void TagCommit(Control owner, string repo, GitCommit commit)
    {
        using var form = new GitTextForm(
            "Git tag",
            TagCommitQuestion(repo, commit),
            "Tag:",
            "",
            "Tag",
            [],
            namedAs: "Will be created as");
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var name = form.Value;
        _ = ActionLog.Run(
            "Git tag",
            [repo],
            async (progress, cancel) =>
            {
                var results = await CodeCommands.Tag([repo], name, commit.Sha, progress, cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
    }

    /// <summary>
    /// What History's Tag asks: the commit picked, its short SHA and subject.
    /// </summary>
    internal static string TagCommitQuestion(string repo, GitCommit commit) =>
        $"Tag this commit in {Path.GetFileName(repo)}, and push the tag to origin:\n{commit.ShortSha} {commit.Subject}";

    /// <summary>
    /// What History's Delete tag asks: the commit picked, its short SHA and subject.
    /// </summary>
    internal static string DeleteTagQuestion(string repo, GitCommit commit) =>
        $"Delete a tag of this commit from {Path.GetFileName(repo)}, and from origin:\n{commit.ShortSha} {commit.Subject}\n\nThis cannot be undone.";

    /// <summary>
    /// History's Revert commit: asks, then commits what undoes the commit picked, on the branch
    /// checked out, without pushing it, and reads the repository again, for History to show it.
    /// </summary>
    internal static void RevertCommit(Control owner, string repo, GitCommit commit)
    {
        var answer = MessageBox.Show(
            owner,
            $"Revert {commit.ShortSha} \"{commit.Subject}\" in {Path.GetFileName(repo)}?\n\nA new commit undoes its changes. It isn't pushed.",
            "Git revert",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git revert",
            [repo],
            async (_, cancel) =>
            {
                var results = await CodeCommands.Revert(repo, commit, cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
    }

    /// <summary>
    /// History's Delete tag: asks which of the commit's tags, then deletes it from the repository
    /// and from origin, and reads the repository again.
    /// </summary>
    internal static async void DeleteCommitTag(Control owner, string repo, GitCommit commit)
    {
        var first = "";
        if (commit.Tags.Count > 0)
        {
            first = commit.Tags[0];
        }

        using var form = new GitTextForm(
            "Git delete tag",
            DeleteTagQuestion(repo, commit),
            "Tag:",
            first,
            "Delete",
            commit.Tags,
            isTagName: true);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var name = form.Value;
        _ = ActionLog.Run(
            "Git delete tag",
            [repo],
            async (progress, cancel) =>
            {
                var results = await CodeCommands.DeleteTag([repo], name, progress, cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
    }

    internal static async void Discard(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder));
        if (repos.Count == 0)
        {
            NoRepos("Git discard", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        var answer = MessageBox.Show(
            owner,
            $"Discard every uncommitted change, and delete untracked files, in {Describe(repos)}?\n\nThis cannot be undone.",
            "Git discard",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git discard",
            folders,
            async (progress, cancel) => CodeCommands.GitOutcome(await CodeCommands.Discard(repos, progress, cancel)));
    }

    // Deletes what git doesn't track, ignored files too. Finding the repositories can mean
    // reading a whole tree of folders, so that is off the UI thread. It asks once the dry run, in
    // the Actions panel, has found what will go, saying how much, and where it goes.
    static async void Purge(Control owner, Settings settings, IReadOnlyList<string> selected)
    {
        var folders = await Task.Run(() => GitPurge.Folders(selected, settings.CodeDirectory ?? selected[0]));
        if (folders.Count == 0)
        {
            NoRepos("Git purge", selected);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        var permanent = settings.PurgePermanently;
        var window = owner.FindForm()?.Handle ?? owner.Handle;
        _ = ActionLog.Run(
            "Git purge",
            selected,
            progress => GitPurge.Run(folders, permanent, window, progress, question => Ask(owner, question)));
    }

    // The window may have closed while the dry run ran.
    static bool Ask(Control owner, string question)
    {
        if (owner.IsDisposed)
        {
            return MessageBox.Show(question, "Git purge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        return MessageBox.Show(owner, question, "Git purge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    static void UpdateNugets(IReadOnlyList<string> folders) =>
        _ = ActionLog.Run("Update NuGets", folders, progress => CodeCommands.UpdateNugets(folders, progress));

    static void UpdateNugetsAndBuild(IReadOnlyList<string> folders) =>
        _ = ActionLog.Run("Update NuGets and build", folders, progress => CodeCommands.UpdateNugets(folders, progress, build: true));

    // Nothing to ask about, but still worth a line in the panel saying so.
    static void NoRepos(string title, IReadOnlyList<string> folders) =>
        _ = ActionLog.Run(title, folders, _ => Task.FromResult(CodeCommands.GitOutcome([])));

    // "Lode", or "12 repositories" with the first few named.
    internal static string Describe(IReadOnlyList<string> repos)
    {
        if (repos.Count == 1)
        {
            return Path.GetFileName(repos[0]);
        }

        var names = repos.Take(8).Select(Path.GetFileName);
        var more = "";
        if (repos.Count > 8)
        {
            more = $", and {repos.Count - 8} more";
        }

        return $"{repos.Count} repositories ({string.Join(", ", names)}{more})";
    }

    // Asks for the commit message, which doubles as the confirmation. Null when cancelled.
    static string? AskCommitMessage(Control owner, IReadOnlyList<string> repos, int newFiles)
    {
        using var form = new GitTextForm(
            "Git push",
            PushQuestion(repos, newFiles),
            "Commit message:",
            defaultCommitMessage,
            "Push",
            []);
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        return form.Value;
    }

    /// <summary>
    /// What Push asks: what it commits, and how many new files go in with it, as git doesn't
    /// track them yet and a stray one, not ignored, would go in too.
    /// </summary>
    internal static string PushQuestion(IReadOnlyList<string> repos, int newFiles)
    {
        if (newFiles == 0)
        {
            return $"Commit all changes in {Describe(repos)} and push to origin.";
        }

        return $"Commit all changes in {Describe(repos)}, including {GitState.CountOf(newFiles, "new file")}, and push to origin.";
    }

    /// <summary>
    /// What Push asks where nothing is uncommitted: only whether to push, there being nothing
    /// for a message to commit.
    /// </summary>
    internal static string PushOnlyQuestion(IReadOnlyList<string> repos) =>
        $"Push {Describe(repos)} to origin?\n\nNothing is uncommitted, so only what is committed goes.";

    /// <summary>
    /// Asks for the text a git action needs: Push's commit message, or a tag. With
    /// <paramref name="choices"/>, a drop-down of them, which can still be typed in. With
    /// <paramref name="namedAs"/>, "Will be created as" say, a new branch's or tag's name: what is
    /// typed is made a name git takes, as GitHub Desktop makes it, a line under the box saying
    /// what it will be where that isn't what was typed.
    /// </summary>
    internal sealed class GitTextForm : Form
    {
        Control input;
        string? namedAs;

        public string Value
        {
            get
            {
                var typed = input.Text.Trim();
                if (namedAs == null)
                {
                    return typed;
                }

                return RefName.Sanitized(typed);
            }
        }

        public GitTextForm(string title, string description, string prompt, string text, string accept, IReadOnlyList<string> choices, bool isTagName = false, string? namedAs = null)
        {
            this.namedAs = namedAs;
            if (choices.Count > 0)
            {
                var combo = new ComboBox
                {
                    Name = "value",
                    Width = 380,
                    DropDownStyle = ComboBoxStyle.DropDown
                };
                combo.Items.AddRange([.. choices]);
                input = combo;
            }
            else
            {
                input = new TextBox
                {
                    Name = "value",
                    Width = 380
                };
            }

            AutoScaleDimensions = new(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new(9);

            input.Text = text;
            // Selectable, for a SHA, subject or tag in it to be copied.
            var question = new SelectableText(description, 380)
            {
                Name = "question",
                Margin = new(3, 3, 3, 6)
            };
            var label = new Label
            {
                AutoSize = true,
                Margin = new(3, 6, 3, 6),
                Text = prompt
            };
            var push = new Button
            {
                Name = "accept",
                Text = accept,
                DialogResult = DialogResult.OK,
                AutoSize = true
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true
            };
            AcceptButton = push;
            CancelButton = cancel;
            var note = new Label
            {
                Name = "note",
                AutoSize = true,
                MaximumSize = new(380, 0),
                Margin = new(3, 6, 3, 0),
                Visible = false
            };

            // Git refuses an empty commit message, and a tag with a space in it.
            input.TextChanged += (_, _) => Changed();
            Changed();

            void Changed()
            {
                push.Enabled = IsValid(Value, isTagName);
                if (namedAs == null)
                {
                    return;
                }

                // Set only when it says something else: the form is laid out again for it.
                var text = NameNote(input.Text.Trim(), namedAs);
                if (note.Text != text)
                {
                    note.Text = text;
                }

                note.Visible = text.Length > 0;
            }

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Anchor = AnchorStyles.Right,
                Margin = new(0, 9, 0, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(push);

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Location = new(Padding.Left, Padding.Top)
            };
            layout.Controls.Add(question);
            layout.Controls.Add(label);
            layout.Controls.Add(input);
            layout.Controls.Add(note);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }

        /// <summary>
        /// What the line under the box says of <paramref name="typed"/>, as GitHub Desktop says
        /// it: the name it will be, after <paramref name="namedAs"/>, where that isn't what was
        /// typed, or that no name can be made of it. Nothing where it is a name git takes as it
        /// is, or nothing is typed.
        /// </summary>
        internal static string NameNote(string typed, string namedAs)
        {
            var name = RefName.Sanitized(typed);
            if (name == typed)
            {
                return "";
            }

            if (name.Length == 0)
            {
                return $"{typed} is not a valid name.";
            }

            return $"{namedAs} {name}.";
        }

        internal static bool IsValid(string value, bool isTagName)
        {
            if (value.Length == 0)
            {
                return false;
            }

            if (!isTagName)
            {
                return true;
            }

            return !value.Any(char.IsWhiteSpace);
        }
    }

    /// <summary>
    /// Text that reads as a label's does, wrapped to its width and as tall as it needs, but can be
    /// selected and copied: read-only, borderless, on the dialog's own colour, and passed over by
    /// Tab.
    /// </summary>
    internal sealed class SelectableText : TextBox
    {
        public SelectableText(string text, int width)
        {
            ReadOnly = true;
            Multiline = true;
            WordWrap = true;
            BorderStyle = BorderStyle.None;
            TabStop = false;
            BackColor = SystemColors.Control;
            Width = width;
            Text = text.ReplaceLineEndings("\r\n");
            FitHeight();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitHeight();
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            FitHeight();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitHeight();
        }

        // As tall as the text wrapped to the width, less the box's own margins either side, so
        // the last line never scrolls out of sight. Set only when it changes, as setting it lays
        // the form out again.
        void FitHeight()
        {
            var measured = TextRenderer.MeasureText(
                Text,
                Font,
                new(Math.Max(1, ClientSize.Width - 6), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            var height = measured.Height + 2;
            if (Height != height)
            {
                Height = height;
            }
        }
    }

    /// <summary>
    /// Tag's question: what goes with the tag, the tag's name, offered as the one after the
    /// latest, and, where anything is uncommitted, whether to commit it first, and with what
    /// message. Tag can't be pressed where every repository has diverged from origin, as none
    /// would be tagged.
    /// </summary>
    internal class TagForm : Form
    {
        TextBox name;
        CheckBox? commitFirst;
        TextBox? message;

        // Whether the message was typed in, so it no longer follows the tag, and whether it is
        // being set to the tag, which isn't typing.
        bool messageTyped;
        bool settingMessage;

        /// <summary>
        /// What was typed made a name git takes, as History's Tag makes it.
        /// </summary>
        public string TagName => RefName.Sanitized(name.Text.Trim());

        /// <summary>
        /// The message to commit the changes with first, or null to tag what is committed.
        /// </summary>
        public string? CommitMessage
        {
            get
            {
                if (commitFirst is not { Checked: true })
                {
                    return null;
                }

                return message!.Text.Trim();
            }
        }

        public TagForm(IReadOnlyList<TagRead> reads)
        {
            AutoScaleDimensions = new(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Git tag";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new(9);

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Location = new(Padding.Left, Padding.Top)
            };
            // Selectable, for the SHAs, subjects and tags in it to be copied.
            layout.Controls.Add(
                new SelectableText(TagQuestion(reads), 380)
                {
                    Name = "question",
                    Margin = new(3, 3, 3, 6)
                });
            layout.Controls.Add(
                new Label
                {
                    AutoSize = true,
                    Margin = new(3, 6, 3, 6),
                    Text = "Tag:"
                });
            name = new()
            {
                Name = "value",
                Width = 380,
                Text = SuggestTag(reads)
            };
            layout.Controls.Add(name);

            // What the tag will be, where that isn't what was typed, as History's Tag says it.
            var note = new Label
            {
                Name = "note",
                AutoSize = true,
                MaximumSize = new(380, 0),
                Margin = new(3, 6, 3, 0),
                Visible = false
            };
            layout.Controls.Add(note);

            var accept = new Button
            {
                Name = "accept",
                Text = "Tag",
                DialogResult = DialogResult.OK,
                AutoSize = true
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true
            };
            AcceptButton = accept;
            CancelButton = cancel;

            var blocked = reads.All(_ => Diverged(_.State));
            void UpdateAccept() =>
                accept.Enabled = !blocked &&
                                 GitTextForm.IsValid(TagName, isTagName: true) &&
                                 CommitMessage is not "";
            void NameChanged()
            {
                // Set only when it says something else: the form is laid out again for it.
                var text = GitTextForm.NameNote(name.Text.Trim(), "Will be created as");
                if (note.Text != text)
                {
                    note.Text = text;
                }

                note.Visible = text.Length > 0;

                // The message is the tag, as the commit is the release, until typed in itself.
                if (message != null &&
                    !messageTyped)
                {
                    settingMessage = true;
                    message.Text = TagName;
                    settingMessage = false;
                }

                UpdateAccept();
            }

            if (reads.Any(_ => _.State is { Changes: > 0 }))
            {
                commitFirst = new()
                {
                    Name = "commitFirst",
                    AutoSize = true,
                    Margin = new(3, 9, 3, 3),
                    Text = "Commit the changes first, with the message:"
                };
                message = new()
                {
                    Name = "message",
                    Width = 380,
                    Enabled = false
                };
                commitFirst.CheckedChanged += (_, _) =>
                {
                    message.Enabled = commitFirst.Checked;
                    UpdateAccept();
                };
                message.TextChanged += (_, _) =>
                {
                    if (!settingMessage)
                    {
                        messageTyped = true;
                    }

                    UpdateAccept();
                };
                layout.Controls.Add(commitFirst);
                layout.Controls.Add(message);
            }

            name.TextChanged += (_, _) => NameChanged();
            NameChanged();

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Anchor = AnchorStyles.Right,
                Margin = new(0, 9, 0, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(accept);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }
    }
}
