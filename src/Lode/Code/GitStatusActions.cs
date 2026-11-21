/// <summary>
/// What clicking a part of the status bar's git state offers: the commits behind, to pull or
/// fetch; those ahead, to push, or, diverged, to pull then push; the changes, to commit and push
/// or discard. Each asks first, saying what it would do, then runs in the Actions panel.
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
        Discard
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
        // Named in the question, origin/main say: one more git, so off the UI thread.
        var upstream = await Task.Run(() => UpstreamOf(repo));
        if (owner.IsDisposed ||
            QuestionFor(part, state, upstream) is not { } question ||
            Ask(owner, question) is not { } choice)
        {
            return;
        }

        Run(owner, choice, repo, codeFolder);
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
    /// <paramref name="upstream"/>. Null for a part with nothing to do.
    /// </summary>
    internal static Question? QuestionFor(GitState.PartKind part, GitState state, string upstream)
    {
        var branch = state.Branch ?? "this commit";
        return part switch
        {
            GitState.PartKind.Behind when state.Behind > 0 => BehindQuestion(state, branch, upstream),
            GitState.PartKind.Ahead when state.Ahead > 0 => AheadQuestion(state, branch, upstream),
            GitState.PartKind.Changes when state.Changes > 0 => ChangesQuestion(state, branch),
            _ => null
        };
    }

    static Question BehindQuestion(GitState state, string branch, string upstream)
    {
        var commits = GitState.CountOf(state.Behind, "commit");
        var pullNote = $"Bring the {commits} into {branch}.";
        if (state.Ahead > 0)
        {
            pullNote = $"Bring the {commits} into {branch}, merging or rebasing as your git config says.";
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

        options.Add(FetchOption(branch, upstream));
        return new(
            "Git pull",
            Heading(state, branch, upstream),
            Diverged(state, upstream),
            options);
    }

    static Question AheadQuestion(GitState state, string branch, string upstream)
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
                    FetchOption(branch, upstream)
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

        // Push commits with -a, which takes every change to a file git tracks and none it
        // doesn't: with only those, there would be nothing to commit. Detached, nowhere to push.
        if (state.Branch != null &&
            untracked < state.Changes)
        {
            var note = "Commit every change to a file git tracks, with a message you give, and push.";
            if (untracked > 0)
            {
                note += " Files git doesn't track stay as they are.";
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

    static Option FetchOption(string branch, string upstream)
    {
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
