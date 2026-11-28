/// <summary>
/// The right-click entries for folders under the code folder: git fetch, pull, sync fork, switch
/// branch, switch to the default branch, update from it, merge, merge current branch into another, squash and merge, rebase, push, tag, discard and purge across their repositories, and Update NuGets (optionally
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
    /// Rebase are for one repository only, as they pick another of its branches. Merge current
    /// branch into picks one every repository has.
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

        commands.Add(LodeActions.Command("gitSwitchBranch", () => SwitchBranches(owner, folders, settings.CodeDirectory), text: "Git switch branch…"));

        if (!onDefault)
        {
            var defaultName = "";
            if (allInRepos)
            {
                defaultName = DefaultNames(repos.Select(_ => CodeCommands.RecordedDefaultName(_!)));
            }

            commands.Add(WithSuffix(LodeActions.Command("gitSwitchToDefault", () => SwitchToDefaultBranch(folders, settings.CodeDirectory)), defaultName));
            commands.Add(WithSuffix(LodeActions.Command("gitUpdateFromDefault", () => UpdateFromDefault(folders, settings.CodeDirectory)), defaultName));
        }

        // Another branch is picked of one repository's, so these are for one repository only,
        // but for Merge current branch into, which picks one every repository has.
        var single = allInRepos &&
                     repos.Count == 1;
        if (single)
        {
            commands.Add(LodeActions.Command("gitMerge", () => MergeBranch(owner, repos[0]!, GitMerging.Kind.Merge), text: "Git merge into current branch…"));
        }

        commands.Add(LodeActions.Command("gitMergeInto", () => MergeIntoAll(owner, folders, settings.CodeDirectory), text: "Git merge current branch into…"));
        if (single)
        {
            var repo = repos[0]!;
            commands.AddRange(
            [
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

    /// <summary>
    /// What follows Switch to and Update from default branch: the default branches' names,
    /// " (main)" say, or " (develop, master)" where the repositories' differ, the most of them
    /// first, as many as three. Those with none recorded say nothing; nothing where none has.
    /// </summary>
    internal static string DefaultNames(IEnumerable<string?> names)
    {
        var known = names
            .OfType<string>()
            .GroupBy(_ => _, StringComparer.Ordinal)
            .OrderByDescending(_ => _.Count())
            .ThenBy(_ => _.Key, StringComparer.OrdinalIgnoreCase)
            .Select(_ => _.Key)
            .ToList();
        if (known.Count == 0)
        {
            return "";
        }

        var shown = string.Join(", ", known.Take(3));
        if (known.Count > 3)
        {
            shown += ", …";
        }

        return $" ({shown})";
    }

    // The command, its name and its name nested under Git both ending with the suffix.
    static QuickMenu.Command WithSuffix(QuickMenu.Command command, string suffix)
    {
        if (suffix.Length == 0)
        {
            return command;
        }

        return command with
        {
            Text = command.Text + suffix,
            NestedText = command.NestedText + suffix
        };
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

        // Left part way by conflicts, Changes shows, where they are settled.
        if (!owner.IsDisposed &&
            owner.FindForm() is MainForm window &&
            GitChanges.IsMerging(repo) &&
            ChangesPane.SameRepo(window.GitPanes.Repo, repo))
        {
            window.GitPanes.Show(GitPane.Changes, focus: true);
        }
    }

    /// <summary>
    /// Merge current branch into: asks which branch, <paramref name="branch"/> picked first
    /// where given, one right-clicked in the status bar's branches say, saying what it would do
    /// with it, and whether to push it afterwards, then merges the branch checked out into it in
    /// the Actions panel, without checking it out: the branch checked out stays so. Detached,
    /// there is no branch to merge.
    /// </summary>
    internal static async void MergeInto(Control owner, string repo, string? branch = null)
    {
        var listing = await Task.Run(() => GitBranches.Read(repo, Cancel.None));
        if (owner.IsDisposed ||
            OnBranch(owner, listing, "Merge current branch into") is not { } current)
        {
            return;
        }

        var branches = BranchActionForm.BranchesOf(listing);
        using var form = new BranchActionForm(
            $"Merge {current} into another branch",
            "Merge",
            listing,
            (name, cancel) => GitMerging.IntoOf(repo, current, name, cancel),
            branch,
            name => PushOffer(branches, name));
        if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
            form.Branch is not { } picked)
        {
            return;
        }

        var push = form.Push;
        _ = RunOnBranch($"Git merge {current} into {picked}", repo, cancel => GitMerging.Into(repo, current, picked, push, cancel));
    }

    // Merge current branch into, for the repositories the folders are or hold: for one, its own
    // branches; for several, those every one has, the one with the most recent commit first, as
    // Switch branch lists them, saying what each repository would come to. Each merges the
    // branch it has checked out into the one picked, staying on it. One on the branch picked,
    // or on none, is left out.
    static async void MergeIntoAll(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var reads = await Task.Run(
            () => CodeCommands.InParallel(
                CodeCommands.FindRepos(folders, codeFolder),
                ReadForSwitch));
        if (reads.Count == 0)
        {
            NoRepos("Git merge current branch into", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        if (reads.Count == 1)
        {
            MergeInto(owner, reads[0].Repo);
            return;
        }

        var choices = SwitchChoices(reads);
        if (choices.Count == 0)
        {
            MessageBox.Show(owner, $"No other branch is in every one of the {Repositories(reads.Count)}.", "Merge current branch into", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new BranchActionForm(
            $"Merge current branch into, in {Repositories(reads.Count)}",
            "Merge",
            new(null, null, [], choices),
            (name, cancel) => MergeIntoPreview(reads, name, cancel),
            pushOffer: name => $"Push {name} afterwards, in each");
        if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
            form.Branch is not { } picked)
        {
            return;
        }

        var push = form.Push;
        var targets = MergeIntoTargets(reads, picked);
        var byRepo = targets.ToDictionary(_ => _.Read.Repo);
        var repos = targets
            .Select(_ => _.Read.Repo)
            .ToList();
        _ = ActionLog.Run(
            $"Git merge into {picked}",
            folders,
            async (progress, cancel) =>
            {
                var results = await CodeCommands.ForEach(
                    repos,
                    progress,
                    cancel,
                    repo =>
                    {
                        var target = byRepo[repo];
                        return GitMerging.Into(repo, target.Read.Listing.Current!, target.Branch.Name, push, cancel);
                    });
                GitCache.MarkStale(repos);
                return CodeCommands.GitOutcome(results);
            });
    }

    /// <summary>
    /// The repositories Merge current branch into merges in, each with its branch of the name
    /// picked: those with a branch checked out other than it.
    /// </summary>
    internal static List<SwitchTarget> MergeIntoTargets(IReadOnlyList<SwitchRead> reads, string picked) =>
        SwitchTargets(reads, picked)
            .Where(_ => _.Read.Listing.Current != null)
            .ToList();

    // What Merge current branch into would come to in a repository.
    sealed record IntoRead(string Repo, GitMerging.IntoCheck Check);

    /// <summary>
    /// What Merge current branch into would do in each of several repositories with
    /// <paramref name="picked"/>, read as it would merge them, grouped by what each comes to:
    /// fast-forwarded, merged with a merge commit, up to date already, conflicting, so left as
    /// it is, or not to be changed from here, each with why, then those on it, or on no branch,
    /// left out. It can go where any would be merged.
    /// </summary>
    internal static GitMerging.Preview MergeIntoPreview(IReadOnlyList<SwitchRead> reads, string picked, Cancel cancel)
    {
        var targets = MergeIntoTargets(reads, picked);
        var byRepo = targets.ToDictionary(_ => _.Read.Repo);
        var checks = CodeCommands.InParallel(
            targets
                .Select(_ => _.Read.Repo)
                .ToList(),
            repo =>
            {
                var target = byRepo[repo];
                return new IntoRead(repo, GitMerging.CheckInto(repo, target.Read.Listing.Current!, target.Branch.Name, cancel));
            },
            cancel: cancel);
        cancel.ThrowIfCancellationRequested();

        List<string> Named(GitMerging.IntoKind kind) =>
            checks
                .Where(_ => _.Check.Kind == kind)
                .Select(_ => Path.GetFileName(_.Repo))
                .ToList();

        var fastForward = Named(GitMerging.IntoKind.FastForward);
        var merge = Named(GitMerging.IntoKind.Merge);
        var going = fastForward.Count + merge.Count;

        // The branch they merge from, where every one has the same checked out: develop, say.
        var from = "each one's branch checked out";
        if (targets
                .Select(_ => _.Read.Listing.Current)
                .Distinct()
                .ToList() is [{ } only])
        {
            from = only;
        }

        var lines = new List<string>();
        if (going == 0)
        {
            lines.Add($"Nothing would be merged into {picked}.");
        }
        else
        {
            lines.Add($"This will merge {from} into {picked} in {Repositories(going)}, leaving each on the branch it has checked out.");
        }

        if (fastForward.Count > 0)
        {
            lines.Add($"Fast-forwarded: {NameList(fastForward)}.");
        }

        if (merge.Count > 0)
        {
            lines.Add($"With a merge commit: {NameList(merge)}.");
        }

        var upToDate = Named(GitMerging.IntoKind.UpToDate);
        if (upToDate.Count > 0)
        {
            lines.Add($"Up to date already: {NameList(upToDate)}.");
        }

        var conflict = Named(GitMerging.IntoKind.Conflict);
        if (conflict.Count > 0)
        {
            lines.Add($"Would conflict, so left as they are: {NameList(conflict)}.");
        }

        foreach (var read in checks.Where(_ => _.Check.Kind == GitMerging.IntoKind.Cannot))
        {
            lines.Add($"{Path.GetFileName(read.Repo)}: {read.Check.Preview.Text}");
        }

        var onIt = reads
            .Where(_ => _.Listing.Current == picked)
            .Select(_ => Path.GetFileName(_.Repo))
            .ToList();
        if (onIt.Count > 0)
        {
            lines.Add($"On {picked}, so left out: {NameList(onIt)}.");
        }

        var detached = reads
            .Where(_ => _.Listing.Current == null)
            .Select(_ => Path.GetFileName(_.Repo))
            .ToList();
        if (detached.Count > 0)
        {
            lines.Add($"On no branch, so left out: {NameList(detached)}.");
        }

        // A line for each group under what it does, so a conflict shows without scrolling.
        return new($"{lines[0]}\n\n{string.Join('\n', lines.Skip(1))}".Trim(), going > 0);
    }

    /// <summary>
    /// What Merge current branch into's box for pushing says of a branch: where it would go, the
    /// remote it tracks, or for one only on a remote, that one, otherwise origin.
    /// </summary>
    internal static string PushOffer(IReadOnlyList<GitBranches.Branch> branches, string name)
    {
        var branch = branches.FirstOrDefault(_ => _.Name == name);
        var remote = "origin";
        if (branch is { Remote: true })
        {
            remote = branch.Name[..branch.Name.IndexOf('/')];
        }
        else if (branch?.Upstream is { } upstream &&
                 upstream.IndexOf('/') is > 0 and var slash)
        {
            remote = upstream[..slash];
        }

        var local = name;
        if (branch != null)
        {
            local = GitBranches.LocalName(branch);
        }

        return $"Push {local} to {remote} afterwards";
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

    /// <summary>
    /// What Switch branch knows of a repository as it asks: its branches, and its state, for
    /// the branch checked out and what is uncommitted. A state that couldn't be read is null.
    /// </summary>
    internal sealed record SwitchRead(string Repo, GitBranches.Listing Listing, GitState? State);

    /// <summary>
    /// A repository Switch branch switches, and the branch of its own it switches to: a local
    /// one, or one only on a remote, which gets a local branch tracking it.
    /// </summary>
    internal sealed record SwitchTarget(SwitchRead Read, GitBranches.Branch Branch);

    // Reads each repository afresh, off the UI thread, then asks which branch, from those of
    // every one, saying which would switch, then whether to leave what is uncommitted or bring
    // it, where anything is. A repository on it already stays as it is.
    static async void SwitchBranches(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var reads = await Task.Run(
            () => CodeCommands.InParallel(
                CodeCommands.FindRepos(folders, codeFolder),
                ReadForSwitch));
        if (reads.Count == 0)
        {
            NoRepos("Git switch branch", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        if (reads is [{ Listing.Error: { } error }])
        {
            MessageBox.Show(owner, $"Couldn't read the branches:\n{error}", "Switch branch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var title = "Switch branch";
        if (reads.Count == 1)
        {
            title = $"Switch branch in {Path.GetFileName(reads[0].Repo)}";
        }

        var choices = SwitchChoices(reads);
        if (choices.Count == 0)
        {
            var none = "There is no other branch to switch to.";
            if (reads.Count > 1)
            {
                none = $"No other branch is in every one of the {Repositories(reads.Count)}.";
            }

            MessageBox.Show(owner, none, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new SwitchBranchForm(
            title,
            SwitchHeading(reads),
            choices,
            name => SwitchPreview(reads, name).Text);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK ||
            form.Branch is not { } picked)
        {
            return;
        }

        var targets = SwitchTargets(reads, picked);
        if (targets.Count == 0)
        {
            return;
        }

        var local = GitBranches.LocalName(targets[0].Branch);
        var withChanges = targets
            .Where(_ => _.Read.State is { Branch: not null, Changes: > 0 })
            .ToList();
        var leave = false;
        if (withChanges.Count > 0)
        {
            bool? answer;
            if (reads.Count == 1)
            {
                answer = AskLeaveChanges(owner, withChanges[0].Read.State!.Branch!, local);
            }
            else
            {
                answer = AskLeaveChangesIn(owner, withChanges.Count, local);
            }

            if (answer is not { } leaveChanges)
            {
                return;
            }

            leave = leaveChanges;
        }

        var byRepo = targets.ToDictionary(_ => _.Read.Repo);
        var repos = targets
            .Select(_ => _.Read.Repo)
            .ToList();
        _ = ActionLog.Run(
            $"Git switch to {local}",
            folders,
            async (progress, cancel) =>
            {
                var results = await CodeCommands.ForEach(
                    repos,
                    progress,
                    cancel,
                    repo =>
                    {
                        var target = byRepo[repo];
                        return GitBranches.Switch(repo, target.Read.State?.Branch, target.Branch, leave, cancel);
                    });
                GitCache.MarkStale(repos);
                return CodeCommands.GitOutcome(results);
            });
    }

    // Above the branches: what is switched, "ElectorateReportingApi from develop to:" say.
    internal static string SwitchHeading(IReadOnlyList<SwitchRead> reads)
    {
        if (reads is [{ Listing.Current: { } current } read])
        {
            return $"Switch {Path.GetFileName(read.Repo)} from {current} to:";
        }

        if (reads.Count == 1)
        {
            return $"Switch {Path.GetFileName(reads[0].Repo)} to:";
        }

        return $"Switch {Repositories(reads.Count)} to:";
    }

    internal static SwitchRead ReadForSwitch(string repo) =>
        new(repo, GitBranches.Read(repo, Cancel.None), GitState.FromGit(repo, Cancel.None));

    /// <summary>
    /// The branches to pick from: those every repository has, by the name each is checked out
    /// as, feature for origin/feature, the one with the most recent commit first. Not one every
    /// repository is on already, as nothing would switch.
    /// </summary>
    internal static List<GitBranches.Branch> SwitchChoices(IReadOnlyList<SwitchRead> reads) =>
        reads
            .SelectMany(_ => AllBranches(_.Listing)
                .DistinctBy(GitBranches.LocalName)
                .Select(branch => (repo: _.Repo, branch)))
            .GroupBy(_ => GitBranches.LocalName(_.branch), StringComparer.Ordinal)
            .Where(_ => _.Count() == reads.Count)
            .Select(_ => new GitBranches.Branch(_.Key, false, _.Max(entry => entry.branch.Committed), false))
            .Where(_ => SwitchTargets(reads, _.Name).Count > 0)
            .OrderByDescending(_ => _.Committed)
            .ThenBy(_ => _.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    static IEnumerable<GitBranches.Branch> AllBranches(GitBranches.Listing listing)
    {
        if (listing.Default != null)
        {
            yield return listing.Default;
        }

        foreach (var branch in listing.Recent.Concat(listing.Other))
        {
            yield return branch;
        }
    }

    /// <summary>
    /// The repositories that would switch to <paramref name="picked"/>, each with its branch of
    /// that name: one of the name itself, local before remote, otherwise one only on a remote
    /// checked out as that name, origin's before another's. Not those on it already, or without it.
    /// </summary>
    internal static List<SwitchTarget> SwitchTargets(IReadOnlyList<SwitchRead> reads, string picked) =>
        reads
            .Select(_ => (read: _, branch: BranchFor(_, picked)))
            .Where(_ => _.branch != null &&
                        _.read.Listing.Current != GitBranches.LocalName(_.branch))
            .Select(_ => new SwitchTarget(_.read, _.branch!))
            .ToList();

    static GitBranches.Branch? BranchFor(SwitchRead read, string picked)
    {
        var all = AllBranches(read.Listing).ToList();
        var named = all
            .Where(_ => _.Name == picked)
            .OrderBy(_ => _.Remote)
            .FirstOrDefault();
        if (named != null)
        {
            return named;
        }

        return all
            .Where(_ => _.Remote &&
                        GitBranches.LocalName(_) == picked)
            .OrderBy(_ => !_.Name.StartsWith("origin/", StringComparison.Ordinal))
            .FirstOrDefault();
    }

    /// <summary>
    /// What Switch branch would do with <paramref name="picked"/>: which repositories switch to
    /// it, which are on it already, and which have changes, that it asks
    /// about. It can go where any would switch.
    /// </summary>
    internal static GitMerging.Preview SwitchPreview(IReadOnlyList<SwitchRead> reads, string picked)
    {
        var targets = SwitchTargets(reads, picked);
        var local = picked;
        if (targets.Count > 0)
        {
            local = GitBranches.LocalName(targets[0].Branch);
        }

        if (reads.Count == 1)
        {
            if (targets is not [var target])
            {
                return new($"{Path.GetFileName(reads[0].Repo)} is on {local} already.", false);
            }

            var lines = new List<string>();
            if (target.Read.State?.Branch is { } current)
            {
                lines.Add($"Switch {Path.GetFileName(target.Read.Repo)} from {current} to {local}.");
            }
            else
            {
                lines.Add($"Switch {Path.GetFileName(target.Read.Repo)} to {local}.");
            }

            if (target.Branch.Remote)
            {
                lines.Add($"A local {local} is made, tracking {target.Branch.Name}.");
            }

            if (target.Read.State is { Changes: > 0 } state)
            {
                lines.Add($"Its {GitState.CountOf(state.Changes, "uncommitted change")}: you'll be asked whether to leave them or bring them.");
            }

            return new(string.Join("\n\n", lines), true);
        }

        var switching = targets
            .Select(_ => Path.GetFileName(_.Read.Repo))
            .ToList();
        var onIt = reads
            .Where(_ => _.Listing.Current == local)
            .Select(_ => Path.GetFileName(_.Repo))
            .ToList();
        var notes = new List<string>();
        if (switching.Count == 0)
        {
            notes.Add($"Every repository is on {local} already.");
        }
        else
        {
            notes.Add($"Switch {switching.Count} of {Repositories(reads.Count)} to {local}: {NameList(switching)}.");
        }

        if (onIt.Count > 0)
        {
            notes.Add($"On it already: {NameList(onIt)}.");
        }

        var changed = targets
            .Where(_ => _.Read.State is { Changes: > 0 })
            .Select(_ => Path.GetFileName(_.Read.Repo))
            .ToList();
        if (changed.Count > 0)
        {
            notes.Add($"With uncommitted changes, which you'll be asked whether to leave or bring: {NameList(changed)}.");
        }

        return new(string.Join("\n\n", notes), switching.Count > 0);
    }

    static string Repositories(int count)
    {
        if (count == 1)
        {
            return "1 repository";
        }

        return $"{count:N0} repositories";
    }

    // "A, B and C", or the first eight and how many more.
    static string NameList(IReadOnlyList<string> names)
    {
        if (names.Count > 8)
        {
            return $"{string.Join(", ", names.Take(8))} and {names.Count - 8} more";
        }

        if (names.Count == 1)
        {
            return names[0];
        }

        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
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
        AskLeaveOrBring(
            owner,
            $"There are uncommitted changes on {current}",
            new(
                $"Leave my changes on {current}",
                $"They are stashed, and put back when you switch to {current} again from the status bar."),
            new(
                $"Bring my changes to {target}",
                "They come along. If git would have to overwrite any of them, nothing is switched."));

    /// <summary>
    /// Asks, for <paramref name="count"/> repositories with uncommitted changes, whether to leave
    /// each one's on the branch it has checked out or bring them to <paramref name="target"/>:
    /// true to leave them, null when cancelled. Tests answer for the user.
    /// </summary>
    internal static Func<Control, int, string, bool?> AskLeaveChangesIn = (owner, count, target) =>
        AskLeaveOrBring(
            owner,
            $"{Repositories(count)} {Has(count)} uncommitted changes",
            new(
                "Leave my changes on their branches",
                "They are stashed in each, and put back when you switch to its branch again from the status bar."),
            new(
                $"Bring my changes to {target}",
                "They come along. Where git would have to overwrite any of them, that repository isn't switched."));

    static string Has(int count)
    {
        if (count == 1)
        {
            return "has";
        }

        return "have";
    }

    static bool? AskLeaveOrBring(Control owner, string heading, TaskDialogCommandLinkButton leave, TaskDialogCommandLinkButton bring)
    {
        var page = new TaskDialogPage
        {
            Caption = "Switch branch",
            Heading = heading,
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
    }

    /// <summary>
    /// Asks for the name of a new branch, starting from <paramref name="name"/>, then makes it at
    /// the commit checked out, or, for History's Create branch from commit, at
    /// <paramref name="at"/>, and switches to it. What is uncommitted comes along.
    /// </summary>
    public static async void NewBranch(Control owner, string repo, string name, GitCommit? at = null)
    {
        using var form = new GitTextForm(
            "New branch",
            NewBranchQuestion(repo, at),
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
        _ = RunOnBranch($"Git new branch {branch}", repo, cancel => GitBranches.Create(repo, branch, cancel, at?.Sha));
    }

    /// <summary>
    /// What New branch asks: where the branch is made, the commit checked out or the one History
    /// picked, its short SHA and subject, and what becomes of what is uncommitted.
    /// </summary>
    internal static string NewBranchQuestion(string repo, GitCommit? at)
    {
        var name = Path.GetFileName(repo);
        if (at == null)
        {
            return $"Make a branch in {name} at the commit checked out, and switch to it. Uncommitted changes come along.";
        }

        return $"Make a branch in {name} at this commit, and switch to it:\n{at.ShortSha} {at.Subject}\n\nUncommitted changes come along, unless git would have to overwrite them, when nothing is made.";
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
                new($"Merge {checkedOut} into this…", () => MergeInto(owner, repo, branch.Name))
                {
                    Glyph = Icons.MergeInto
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
        var (repos, untracked, changed) = await Task.Run(
            () =>
            {
                var found = CodeCommands.FindRepos(folders, codeFolder);
                return (found, CodeCommands.FindUntracked(found), CodeCommands.AnyChanges(found));
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
            message = AskCommitMessage(owner, repos, untracked);
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
    /// checked out, and pushes the tag, with the commit, and those before it, where origin's
    /// branch hasn't got it yet. What goes is read off the UI thread first, for the question to
    /// say. The repository is read again once it is done, for History to show the tag, and the
    /// commits pushed as pushed.
    /// </summary>
    internal static async void TagCommit(Control owner, string repo, GitCommit commit)
    {
        var goes = await Task.Run(() => CodeCommands.BranchToPushWithTag(repo, commit.Sha));
        if (owner.IsDisposed)
        {
            return;
        }

        using var form = new GitTextForm(
            "Git tag",
            TagCommitQuestion(repo, commit, goes),
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
    /// What History's Tag asks: the commit picked, its short SHA and subject, then what else
    /// goes, as Tag's question says it: the commit, and those before it, not yet on origin's
    /// branch, which go with the tag; a branch origin hasn't got, which goes too; or a branch
    /// diverged from origin's, left till pulled.
    /// </summary>
    internal static string TagCommitQuestion(string repo, GitCommit commit, CodeCommands.WithTag goes = default)
    {
        var question = $"Tag this commit in {Path.GetFileName(repo)}, and push the tag to origin:\n{commit.ShortSha} {commit.Subject}";
        var branch = goes.Branch;
        if (goes.Failed != null)
        {
            return $"{question}\n\n{branch} and origin's have each got commits the other hasn't, so it can't be tagged till pulled.";
        }

        if (goes.BranchRef == null)
        {
            return question;
        }

        if (goes.NewBranch)
        {
            return $"{question}\n\n{branch} goes to origin with the tag, up to this commit, as origin hasn't got it yet.";
        }

        if (goes.Ahead > 1)
        {
            var before = goes.Ahead - 1;
            return $"{question}\n\nIt and the {GitState.CountOf(before, "commit")} before it not yet pushed go with the tag, so origin's {branch} has the commit tagged.";
        }

        return $"{question}\n\nIt isn't pushed yet, so it goes with the tag, and origin's {branch} has the commit tagged.";
    }

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
    /// History's Checkout commit: asks, then checks the commit picked out on no branch, as GitHub
    /// Desktop's does, and reads the repository again.
    /// </summary>
    internal static void CheckoutCommit(Control owner, string repo, GitCommit commit)
    {
        var answer = MessageBox.Show(
            owner,
            CheckoutCommitQuestion(repo, commit),
            "Git checkout commit",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
        {
            return;
        }

        _ = RunOnBranch($"Git checkout {commit.ShortSha}", repo, cancel => GitBranches.CheckoutCommit(repo, commit.Sha, cancel));
    }

    /// <summary>
    /// What History's Checkout commit asks: which commit, by its short SHA and subject, what
    /// becomes of what is uncommitted, and of a commit made there.
    /// </summary>
    internal static string CheckoutCommitQuestion(string repo, GitCommit commit) =>
        $"Check out {commit.ShortSha} \"{commit.Subject}\" in {Path.GetFileName(repo)}, on no branch?\n\nUncommitted changes come along, unless git would have to overwrite them, when nothing is checked out. A commit made there is on no branch until one is made at it, with Create branch from commit.";

    /// <summary>
    /// History's Cherry-pick: asks, then makes the commit picked, of another branch, again on
    /// <paramref name="branch"/>, the branch checked out, without pushing it, and reads the
    /// repository again, for History to show it. Stopped by conflicts, it is left part way, and
    /// Changes shows it, to settle them and continue it, or abort it.
    /// </summary>
    internal static void CherryPick(Control owner, string repo, GitCommit commit, string branch)
    {
        var answer = MessageBox.Show(
            owner,
            CherryPickQuestion(repo, commit, branch),
            "Git cherry-pick",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git cherry-pick",
            [repo],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => GitMerging.CherryPick(repo, commit, cancel), cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome([result]);
            });
    }

    /// <summary>
    /// What History's Cherry-pick asks: which commit, by its short SHA and subject, goes onto
    /// which branch, and what happens should it conflict.
    /// </summary>
    internal static string CherryPickQuestion(string repo, GitCommit commit, string branch) =>
        $"Cherry-pick {commit.ShortSha} \"{commit.Subject}\" onto {branch} in {Path.GetFileName(repo)}?\n\nA new commit on {branch} makes its changes, with its message and author. It isn't pushed. Should it conflict, it is left part way, for the conflicts to be settled in Changes, where it can be continued or aborted.";

    /// <summary>
    /// History's Undo commit: asks, then takes the last commit off the branch, its changes left
    /// uncommitted, and reads the repository again. True once it is undone, for its message to go
    /// in Changes, to commit again.
    /// </summary>
    internal static async Task<bool> UndoCommit(Control owner, string repo, GitCommit commit, bool uncommitted)
    {
        var draft = CommitDrafts.Of(repo);
        var typed = draft.Summary.Trim().Length > 0 ||
                    draft.Description.Trim().Length > 0;
        var answer = MessageBox.Show(
            owner,
            UndoCommitQuestion(repo, commit, uncommitted, typed),
            "Git undo commit",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
        {
            return false;
        }

        var entry = await ActionLog.Run(
            "Git undo commit",
            [repo],
            async (_, cancel) =>
            {
                var results = await CodeCommands.UndoCommit(repo, commit, cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
        return entry.State == ActionLog.State.Succeeded;
    }

    /// <summary>
    /// What History's Undo commit asks: the commit, what becomes of its changes and its message,
    /// of what is typed in Changes already, and of what is uncommitted already; for a merge, of
    /// the commits it merged; and for the first commit, of the branch.
    /// </summary>
    internal static string UndoCommitQuestion(string repo, GitCommit commit, bool uncommitted, bool typed)
    {
        var question = $"Undo {commit.ShortSha} \"{commit.Subject}\" in {Path.GetFileName(repo)}?\n\nIt comes off the branch, its changes left uncommitted, and its summary and description go in Changes, to commit again.";
        if (typed)
        {
            question += " They replace what is typed there.";
        }

        if (uncommitted)
        {
            question += "\n\nWhat is uncommitted already stays as it is, among its changes.";
        }

        if (commit.Parents.Count > 1)
        {
            question += "\n\nIt is a merge: what it brought in is left uncommitted, and the commits it merged are no longer on the branch.";
        }
        else if (commit.Parents.Count == 0)
        {
            question += "\n\nIt is the first commit: the branch is left with none, its files staged.";
        }

        return question;
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

    // Discards what is uncommitted in the folders, having asked: a folder inside a repository
    // alone, as Purge purges it alone, otherwise every repository it holds, the files as they are
    // going to the Recycle Bin first, as the Changes pane's Discard sends them.
    internal static async void Discard(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var targets = await Task.Run(() => CodeCommands.DiscardTargets(folders, codeFolder));
        if (targets.Count == 0)
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
            DiscardQuestion(targets),
            "Git discard",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK)
        {
            return;
        }

        var window = owner.FindForm()?.Handle ?? owner.Handle;
        _ = ActionLog.Run(
            "Git discard",
            folders,
            async (progress, cancel) =>
            {
                var results = await CodeCommands.Discard(
                    targets,
                    paths => FileOperations.Delete(window, paths, permanent: false),
                    progress,
                    cancel);
                return CodeCommands.GitOutcome(results);
            });
    }

    /// <summary>
    /// What Discard asks: where it discards, a folder inside a repository named for where it is
    /// in it, and that the files as they are go to the Recycle Bin.
    /// </summary>
    internal static string DiscardQuestion(IReadOnlyList<CodeCommands.Discarding> targets)
    {
        var where = Describe(targets.Select(_ => _.Repo).ToList());
        if (targets.Any(_ => _.Folders.Count > 0))
        {
            // Each, not how many: folders counted would read as repositories.
            var places = targets
                .SelectMany(Places)
                .ToList();
            where = string.Join(", ", places.Take(8));
            if (places.Count > 8)
            {
                where += $", and {places.Count - 8} more";
            }
        }

        return $"Discard every uncommitted change, and delete untracked files, in {where}?\n\nThe files as they are now go to the Recycle Bin.";

        static IEnumerable<string> Places(CodeCommands.Discarding target)
        {
            var name = Path.GetFileName(target.Repo);
            if (target.Folders.Count == 0)
            {
                return [name];
            }

            return target.Folders.Select(_ => $"{Path.GetRelativePath(target.Repo, _)} in {name}");
        }
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
    static string? AskCommitMessage(Control owner, IReadOnlyList<string> repos, CodeCommands.Untracked untracked)
    {
        using var form = new GitTextForm(
            "Git push",
            PushQuestion(repos, untracked.NewFiles, untracked.Repos),
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
    /// track them yet and a stray one, not ignored, would go in too; then the repositories of
    /// their own inside, <paramref name="leftOut"/>, a clone or a worktree say, which it leaves
    /// out, as git would commit each only as a link to its commit.
    /// </summary>
    internal static string PushQuestion(IReadOnlyList<string> repos, int newFiles, IReadOnlyList<string>? leftOut = null)
    {
        var question = $"Commit all changes in {Describe(repos)} and push to origin.";
        if (newFiles > 0)
        {
            question = $"Commit all changes in {Describe(repos)}, including {GitState.CountOf(newFiles, "new file")}, and push to origin.";
        }

        if (leftOut is not { Count: > 0 })
        {
            return question;
        }

        // Each where it is in its repository, or, of several repositories, from the folder
        // holding them, so it says which repository it is in.
        var from = repos[0];
        if (repos.Count > 1)
        {
            from = CodeCommands.CommonFolder(repos);
        }

        var names = leftOut
            .Take(8)
            .Select(_ => Path.GetRelativePath(from, _))
            .ToList();
        if (leftOut.Count > 8)
        {
            names.Add($"and {leftOut.Count - 8} more");
        }

        if (leftOut.Count == 1)
        {
            return $"{question}\n\nLeaves out {names[0]}, a repository of its own inside, which git would commit only as a link to its commit.";
        }

        return $"{question}\n\nLeaves out {leftOut.Count} repositories of their own inside, which git would commit only as links to their commits: {string.Join(", ", names)}.";
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

        // Whether what is typed is made a name git takes: a new branch's or tag's.
        bool makesName;

        public string Value
        {
            get
            {
                var typed = input.Text.Trim();
                if (!makesName)
                {
                    return typed;
                }

                return RefName.Sanitized(typed);
            }
        }

        public GitTextForm(string title, string description, string prompt, string text, string accept, IReadOnlyList<string> choices, bool isTagName = false, string? namedAs = null)
        {
            makesName = namedAs != null;
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
