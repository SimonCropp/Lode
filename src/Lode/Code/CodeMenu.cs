/// <summary>
/// The right-click entries for folders under the code folder: git pull, switch to the default
/// branch, push, discard and purge across their repositories, and Update NuGets (optionally
/// then building). With several folders selected, each command takes in all of them at once,
/// as one entry in the actions panel. Each runs in the background and shows in the actions panel under the tree, without
/// holding up the rest of Lode. Push, discard and purge ask first.
/// </summary>
static class CodeMenu
{
    const string defaultCommitMessage = "refs or cleanup";

    /// <summary>
    /// The entries for the folders, after a separator, or none unless every one is under the
    /// code folder. Switch to default branch is left off when every repository is on it, and
    /// Push when every one has nothing to push, as <paramref name="stateOf"/> has it, which
    /// gives a repository's state where it has been read.
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

        List<QuickMenu.Command?> commands =
        [
            null,
            LodeActions.Command("gitPull", () => Pull(folders, settings.CodeDirectory)) with
            {
                NestedText = "Pull"
            }
        ];
        if (!onDefault)
        {
            commands.Add(
                LodeActions.Command("gitSwitchToDefault", () => SwitchToDefaultBranch(folders, settings.CodeDirectory)) with
                {
                    NestedText = "Switch to default branch"
                });
        }

        if (!nothingToPush)
        {
            commands.Add(
                LodeActions.Command("gitPush", () => Push(owner, folders, settings.CodeDirectory), text: "Git push…") with
                {
                    NestedText = "Push…"
                });
        }

        commands.Add(
            LodeActions.Command("gitTag", () => Tag(owner, folders, settings.CodeDirectory), text: "Git tag…") with
            {
                NestedText = "Tag…"
            });
        commands.Add(
            LodeActions.Command("gitDeleteTag", () => DeleteTag(owner, folders, settings.CodeDirectory), text: "Git delete tag…") with
            {
                NestedText = "Delete tag…"
            });

        commands.AddRange(
        [
            LodeActions.Command("gitDiscard", () => Discard(owner, folders, settings.CodeDirectory), text: "Git discard…") with
            {
                NestedText = "Discard…"
            },
            LodeActions.Command("gitPurge", () => Purge(owner, settings, folders), text: "Git purge…") with
            {
                NestedText = "Purge…"
            },
            LodeActions.Command("updateNugets", () => UpdateNugets(folders)),
            LodeActions.Command("updateNugetsAndBuild", () => UpdateNugetsAndBuild(folders))
        ]);
        return commands;
    }

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

        _ = ActionLog.Run(
            $"Git switch to {target.Name}",
            [repo],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => GitBranches.Switch(repo, current, target, leave, cancel), cancel);
                // Before the entry ends, which has the windows read the states again: the
                // watch may not have heard of the switch yet, and the state would still be fresh.
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome([result]);
            });
    }

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
            isTagName: true);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var branch = form.Value;
        _ = ActionLog.Run(
            $"Git new branch {branch}",
            [repo],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => GitBranches.Create(repo, branch, cancel), cancel);
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome([result]);
            });
    }

    // Looking for the repositories reads two levels of folders, so that is off the UI thread, as
    // Pull's is, and Discard's. Then it asks for the commit message.
    static async void Push(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder));
        if (repos.Count == 0)
        {
            NoRepos("Git push", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        var message = AskCommitMessage(owner, repos);
        if (message == null)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git push",
            folders,
            async (progress, cancel) => CodeCommands.GitOutcome(await CodeCommands.Push(repos, message, progress, cancel)));
    }

    // Asks for the tag's name, then tags the commit each repository is at and pushes the tag.
    static async void Tag(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var repos = await Task.Run(() => CodeCommands.FindRepos(folders, codeFolder));
        if (repos.Count == 0)
        {
            NoRepos("Git tag", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        using var form = new GitTextForm(
            "Git tag",
            $"Tag the commit {Describe(repos)} is at, and push the tag to origin.",
            "Tag:",
            "",
            "Tag",
            [],
            isTagName: true);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var name = form.Value;
        _ = ActionLog.Run(
            "Git tag",
            folders,
            async (progress, cancel) => CodeCommands.GitOutcome(await CodeCommands.Tag(repos, name, progress, cancel)));
    }

    // Asks which tag, offering those there are, newest first, then deletes it from each
    // repository and from origin.
    static async void DeleteTag(Control owner, IReadOnlyList<string> folders, string? codeFolder)
    {
        var (repos, tags) = await Task.Run(
            () =>
            {
                var found = CodeCommands.FindRepos(folders, codeFolder);
                return (found, CodeCommands.Tags(found));
            });
        if (repos.Count == 0)
        {
            NoRepos("Git delete tag", folders);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        using var form = new GitTextForm(
            "Git delete tag",
            $"Delete a tag from {Describe(repos)}, and from origin. This cannot be undone.",
            "Tag:",
            tags.FirstOrDefault() ?? "",
            "Delete",
            tags,
            isTagName: true);
        if (await form.ShowDialogAsync(owner) != DialogResult.OK)
        {
            return;
        }

        var name = form.Value;
        _ = ActionLog.Run(
            "Git delete tag",
            folders,
            async (progress, cancel) => CodeCommands.GitOutcome(await CodeCommands.DeleteTag(repos, name, progress, cancel)));
    }

    static async void Discard(Control owner, IReadOnlyList<string> folders, string? codeFolder)
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
    static string? AskCommitMessage(Control owner, IReadOnlyList<string> repos)
    {
        using var form = new GitTextForm(
            "Git push",
            $"Commit all changes in {Describe(repos)} and push to origin.",
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
    /// Asks for the text a git action needs: Push's commit message, or a tag. With
    /// <paramref name="choices"/>, a drop-down of them, which can still be typed in.
    /// </summary>
    internal sealed class GitTextForm : Form
    {
        readonly Control input;

        public string Value => input.Text.Trim();

        public GitTextForm(string title, string description, string prompt, string text, string accept, IReadOnlyList<string> choices, bool isTagName = false)
        {
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
            var label = new Label
            {
                AutoSize = true,
                MaximumSize = new(380, 0),
                Margin = new(3, 3, 3, 6),
                Text = $"{description}\n\n{prompt}"
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

            // Git refuses an empty commit message, and a tag with a space in it.
            input.TextChanged += (_, _) => push.Enabled = IsValid(Value, isTagName);
            push.Enabled = IsValid(Value, isTagName);

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
            layout.Controls.Add(label);
            layout.Controls.Add(input);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
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
}
