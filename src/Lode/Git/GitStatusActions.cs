/// <summary>
/// What clicking a part of the status bar's git state offers: the commits behind, to pull or
/// fetch; those ahead, to push, or, diverged, to pull then push; the changes, to commit and push
/// or discard; in a fork, those behind upstream, to sync the fork or fetch. Each asks first,
/// saying what it would do, then runs in the Actions panel.
/// </summary>
static class GitStatusActions
{
    public enum Choice
    {
        Pull,
        PullThenPush,
        Fetch,
        Push,
        CommitAndPush,
        Discard,
        SyncFork
    }

    /// <summary>
    /// One way to act, as the question offers it: what it is, and a line saying what it does.
    /// </summary>
    public sealed record Option(Choice Choice, string Text, string Note);

    /// <summary>
    /// The question asked: a heading, a line under it, the ways to act, the first being the
    /// default, and, to show on asking, the files it is about.
    /// </summary>
    public sealed record Question(string Caption, string Heading, string Text, IReadOnlyList<Option> Options, string? Details = null);

    /// <summary>
    /// Asks <paramref name="question"/>, giving the way picked, or null when cancelled. Tests
    /// answer for the user.
    /// </summary>
    internal static Func<Control, Question, Choice?> Ask = AskInDialog;

    /// <summary>
    /// Acts on the part of <paramref name="state"/> clicked, in <paramref name="repo"/>: asks
    /// what to do, then does it. A part with nothing to do, no commits yet say, does nothing.
    /// </summary>
    public static async void Act(Control owner, GitState.PartKind part, string repo, GitState state, string? codeFolder)
    {
        // Named in the question, origin/main and upstream/main say: more git, so off the UI
        // thread.
        var (upstream, fork) = await Task.Run(() => (UpstreamOf(repo), ForkParentOf(repo)));
        if (owner.IsDisposed ||
            QuestionFor(part, state, upstream, fork) is not { } question ||
            Ask(owner, question) is not { } choice)
        {
            return;
        }

        Run(owner, choice, repo, codeFolder);
    }

    // The branch of the repository a fork was made from that Sync fork brings in, upstream/main
    // say, or just upstream where it has no HEAD recorded. Null for a repository that isn't a
    // fork.
    static string? ForkParentOf(string repo)
    {
        if (!CodeCommands.IsFork(repo))
        {
            return null;
        }

        if (CodeCommands.RecordedDefaultBranch(repo, Cancel.None, "upstream") is { } branch)
        {
            return $"upstream/{branch}";
        }

        return "upstream";
    }

    static string UpstreamOf(string repo)
    {
        var result = CodeCommands.Git(repo, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
        if (result is {Succeeded: true, Output.Length: > 0})
        {
            return result.Output;
        }

        return "the branch it tracks";
    }

    /// <summary>
    /// What clicking <paramref name="part"/> asks, for a branch tracking
    /// <paramref name="upstream"/>, in a fork made from <paramref name="fork"/>, upstream/main
    /// say, where it is one. Null for a part with nothing to do.
    /// </summary>
    internal static Question? QuestionFor(GitState.PartKind part, GitState state, string upstream, string? fork = null)
    {
        var branch = state.Branch ?? "this commit";
        return part switch
        {
            GitState.PartKind.Behind when state.Behind > 0 => BehindQuestion(state, branch, upstream, fork),
            GitState.PartKind.Ahead when state.Ahead > 0 => AheadQuestion(state, branch, upstream, fork),
            GitState.PartKind.Changes when state.Changes > 0 => ChangesQuestion(state, branch),
            GitState.PartKind.ForkBehind when state.ForkBehind > 0 => ForkQuestion(state, branch, fork ?? "upstream"),
            _ => null
        };
    }

    // Sync fork does what GitHub Desktop's Fetch origin, Update from upstream and Push origin do.
    // Detached, there is no branch to bring upstream's commits into, nor to push.
    static Question ForkQuestion(GitState state, string branch, string fork)
    {
        var heading = $"{branch} is {GitState.CountOf(state.ForkBehind, "commit")} behind {fork}";
        var fetch = FetchOption(branch, "origin", fork);
        if (state.Branch == null)
        {
            return new("Git sync fork", heading, "As of the last fetch. Check out a branch to bring them into it.", [fetch]);
        }

        var note = $"Fetch origin and upstream, bring {fork} into {branch}, merging where {branch} has commits of its own, and push {branch} to origin.";
        if (state.Changes > 0)
        {
            note += " Uncommitted changes stay, unless git would have to overwrite any, when nothing is merged.";
        }

        return new(
            "Git sync fork",
            heading,
            "As of the last fetch.",
            [
                new(Choice.SyncFork, "Sync fork", note),
                fetch
            ]);
    }

    static Question BehindQuestion(GitState state, string branch, string upstream, string? fork)
    {
        // Pull fetches first, so takes whatever origin has as it runs, not only what the last
        // fetch counted.
        var commits = GitState.CountOf(state.Behind, "commit");
        var pullNote = $"Fetch, then bring {upstream} into {branch}: the {commits} known now, and any since.";
        if (state.Ahead > 0)
        {
            pullNote = $"Fetch, then bring {upstream} into {branch}, merging or rebasing as your git config says: the {commits} known now, and any since.";
        }

        if (state.Changes > 0)
        {
            pullNote += " Uncommitted changes stay, unless git would have to overwrite any, when nothing is pulled.";
        }

        List<Option> options = [new(Choice.Pull, "Pull", pullNote)];
        if (state.Ahead > 0)
        {
            options.Add(new(Choice.PullThenPush, "Pull, then push", $"Then send the {GitState.CountOf(state.Ahead, "commit")} here to {upstream}."));
        }

        options.Add(FetchOption(branch, upstream, fork));
        return new(
            "Git pull",
            Heading(state, branch, upstream),
            Diverged(state, upstream),
            options);
    }

    static Question AheadQuestion(GitState state, string branch, string upstream, string? fork)
    {
        var commits = GitState.CountOf(state.Ahead, "commit");
        var uncommitted = "";
        if (state.Changes > 0)
        {
            uncommitted = "Uncommitted changes aren't pushed: commit them first.";
        }

        if (state.Behind > 0)
        {
            return new(
                "Git push",
                Heading(state, branch, upstream),
                $"{Diverged(state, upstream)} {uncommitted}".Trim(),
                [
                    new(
                        Choice.PullThenPush,
                        "Pull, then push",
                        $"{upstream} would refuse the push while it has commits {branch} hasn't: bring those in first, then send the {commits}."),
                    FetchOption(branch, upstream, fork)
                ]);
        }

        return new(
            "Git push",
            Heading(state, branch, upstream),
            uncommitted,
            [new(Choice.Push, "Push", $"Send the {commits} to {upstream}.")]);
    }

    static Question ChangesQuestion(GitState state, string branch)
    {
        var untracked = state.Files.Count(_ => _.Change == GitChange.Untracked);
        var options = new List<Option>();

        // Push adds every change before it commits, new files included, as the Changes pane
        // commits them, though not what git ignores, which isn't a change at all. Detached,
        // nowhere to push.
        if (state.Branch != null)
        {
            var note = "Commit every change, with a message you give, and push.";
            if (untracked > 0)
            {
                note = "Commit every change, new files included, with a message you give, and push.";
            }

            options.Add(new(Choice.CommitAndPush, "Commit and push…", note));
        }

        options.Add(new(Choice.Discard, "Discard…", "Undo every uncommitted change, and delete the files git doesn't track. Asks again first."));
        var where = $"on {branch}";
        if (state.Branch == null)
        {
            where = "here";
        }

        return new(
            "Uncommitted changes",
            $"{GitState.CountOf(state.Changes, "uncommitted change")} {where}",
            "",
            options,
            FileList(state.Files));
    }

    // A fork's fetch learns what upstream has too.
    static Option FetchOption(string branch, string upstream, string? fork)
    {
        if (fork != null)
        {
            return new(Choice.Fetch, "Fetch", $"See what origin and upstream have now, without changing {branch}.");
        }

        var remote = upstream;
        var slash = upstream.IndexOf('/');
        if (slash > 0)
        {
            remote = upstream[..slash];
        }

        return new(Choice.Fetch, "Fetch", $"See what {remote} has now, without changing {branch}.");
    }

    static string Heading(GitState state, string branch, string upstream)
    {
        if (state is {Ahead: > 0, Behind: > 0})
        {
            return $"{branch} has diverged from {upstream}";
        }

        if (state.Behind > 0)
        {
            return $"{branch} is {GitState.CountOf(state.Behind, "commit")} behind {upstream}";
        }

        return $"{branch} is {GitState.CountOf(state.Ahead, "commit")} ahead of {upstream}";
    }

    // Counts are as of the last fetch: origin may have more since.
    static string Diverged(GitState state, string upstream)
    {
        if (state is {Ahead: > 0, Behind: > 0})
        {
            return $"{GitState.CountOf(state.Ahead, "commit")} here aren't on {upstream}, and {GitState.CountOf(state.Behind, "commit")} there aren't here, as of the last fetch.";
        }

        return "As of the last fetch.";
    }

    // A line for each file, what has become of it then where it is, up to a screenful.
    const int listed = 30;

    internal static string FileList(IReadOnlyList<GitFile> files)
    {
        var lines = files
            .Take(listed)
            .Select(_ => $"{_.Change}: {_.Path}")
            .ToList();
        if (files.Count > listed)
        {
            lines.Add($"and {files.Count - listed} more");
        }

        return string.Join('\n', lines);
    }

    static Choice? AskInDialog(Control owner, Question question)
    {
        var buttons = question.Options
            .Select(_ => (_.Choice, Button: new TaskDialogCommandLinkButton(_.Text, _.Note)))
            .ToList();
        var page = new TaskDialogPage
        {
            Caption = question.Caption,
            Heading = question.Heading,
            Text = question.Text,
            AllowCancel = true,
            DefaultButton = buttons[0].Button
        };
        foreach (var (_, button) in buttons)
        {
            page.Buttons.Add(button);
        }

        page.Buttons.Add(TaskDialogButton.Cancel);
        if (question.Details != null)
        {
            page.Expander = new(question.Details)
            {
                CollapsedButtonText = "Show the changes",
                ExpandedButtonText = "Hide the changes"
            };
        }

        var answer = TaskDialog.ShowDialog(owner, page);
        foreach (var (choice, button) in buttons)
        {
            if (answer == button)
            {
                return choice;
            }
        }

        return null;
    }

    // Each in the Actions panel. Committing and discarding ask as they do from the right-click
    // menu: for the message, and whether to discard, which can't be undone.
    static void Run(Control owner, Choice choice, string repo, string? codeFolder)
    {
        switch (choice)
        {
            case Choice.Pull:
                Start("Git pull", repo, CodeCommands.Pull);
                return;
            case Choice.PullThenPush:
                Start("Git pull and push", repo, CodeCommands.PullThenPush);
                return;
            case Choice.Fetch:
                Start("Git fetch", repo, CodeCommands.Fetch);
                return;
            case Choice.Push:
                Start("Git push", repo, CodeCommands.PushCommits);
                return;
            case Choice.CommitAndPush:
                CodeMenu.Push(owner, [repo], codeFolder);
                return;
            case Choice.Discard:
                CodeMenu.Discard(owner, [repo], codeFolder);
                return;
            case Choice.SyncFork:
                Start("Git sync fork", repo, CodeCommands.SyncFork);
                return;
        }
    }

    static void Start(string title, string repo, Func<IReadOnlyList<string>, IProgress<string>?, Cancel, Task<List<CodeCommands.RepoResult>>> command) =>
        _ = ActionLog.Run(
            title,
            [repo],
            async (progress, cancel) =>
            {
                var results = await command([repo], progress, cancel);
                // Before the entry ends, which has the windows read the states again: the watch
                // may not have heard of it yet, and the state would still be fresh.
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
}
