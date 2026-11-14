/// <summary>
/// The right-click entries for a folder under the code folder: git pull, push, discard and
/// purge across its repositories, and Update NuGets (optionally then building). Each runs in
/// the background and shows in the actions panel under the tree, without holding up the rest
/// of Lode. Push, discard and purge ask first.
/// </summary>
static class CodeMenu
{
    const string defaultCommitMessage = "refs or cleanup";

    /// <summary>
    /// The entries for the folder, after a separator, or none when it is not under the code
    /// folder.
    /// </summary>
    public static List<QuickMenu.Command?> Commands(Control owner, Settings settings, string folder)
    {
        if (!settings.IsCode(folder))
        {
            return [];
        }

        return
        [
            null,
            LodeActions.Submenu(
                "git",
                [
                    new("Pull", () => Pull(folder))
                    {
                        Glyph = Icons.Pull
                    },
                    new("Push…", () => Push(owner, folder))
                    {
                        Glyph = Icons.Push
                    },
                    new("Discard…", () => Discard(owner, folder))
                    {
                        Glyph = Icons.Discard
                    },
                    new("Purge…", () => Purge(owner, settings, folder))
                    {
                        Glyph = Icons.Purge
                    }
                ]),
            LodeActions.Command("updateNugets", () => UpdateNugets(folder)),
            LodeActions.Command("updateNugetsAndBuild", () => UpdateNugetsAndBuild(folder))
        ];
    }

    // Each runs in the background and shows in the actions panel; Lode stays usable.
    static void Pull(string folder) =>
        _ = ActionLog.Run(
            "Git pull",
            folder,
            async progress =>
            {
                // Looking for repositories reads folders, so that is off the UI thread too.
                var repos = await Task.Run(() => CodeCommands.FindRepos(folder));
                return CodeCommands.GitOutcome(await CodeCommands.Pull(repos, progress));
            });

    static void Push(Control owner, string folder)
    {
        var repos = CodeCommands.FindRepos(folder);
        if (repos.Count == 0)
        {
            NoRepos("Git push", folder);
            return;
        }

        var message = AskCommitMessage(owner, repos);
        if (message == null)
        {
            return;
        }

        _ = ActionLog.Run(
            "Git push",
            folder,
            async progress => CodeCommands.GitOutcome(await CodeCommands.Push(repos, message, progress)));
    }

    static void Discard(Control owner, string folder)
    {
        var repos = CodeCommands.FindRepos(folder);
        if (repos.Count == 0)
        {
            NoRepos("Git discard", folder);
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
            folder,
            async progress => CodeCommands.GitOutcome(await CodeCommands.Discard(repos, progress)));
    }

    // Deletes what git doesn't track, ignored files too. Finding the repositories can mean
    // reading a whole tree of folders, so that is off the UI thread; then it asks, saying where
    // the files go.
    static async void Purge(Control owner, Settings settings, string folder)
    {
        var folders = await Task.Run(() => GitPurge.Folders(folder, settings.CodeDirectory ?? folder));
        if (folders.Count == 0)
        {
            NoRepos("Git purge", folder);
            return;
        }

        if (owner.IsDisposed)
        {
            return;
        }

        var permanent = settings.PurgePermanently;
        var where = "They go to the Recycle Bin.";
        if (permanent)
        {
            where = "They are deleted permanently, as set in Options. This cannot be undone.";
        }

        var answer = MessageBox.Show(
            owner,
            $"Delete every file and folder git doesn't track, ignored ones included (such as bin and obj), in {Describe(folders)}?\n\n{where}",
            "Git purge",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK)
        {
            return;
        }

        var window = owner.FindForm()?.Handle ?? owner.Handle;
        _ = ActionLog.Run(
            "Git purge",
            folder,
            progress => GitPurge.Run(folders, permanent, window, progress));
    }

    static void UpdateNugets(string folder) =>
        _ = ActionLog.Run("Update NuGets", folder, progress => CodeCommands.UpdateNugets(folder, progress));

    static void UpdateNugetsAndBuild(string folder) =>
        _ = ActionLog.Run("Update NuGets and build", folder, progress => CodeCommands.UpdateNugets(folder, progress, build: true));

    // Nothing to ask about, but still worth a line in the panel saying so.
    static void NoRepos(string title, string folder) =>
        _ = ActionLog.Run(title, folder, () => Task.FromResult(CodeCommands.GitOutcome([])));

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
        using var form = new CommitMessageForm($"Commit all changes in {Describe(repos)} and push to origin.", defaultCommitMessage);
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        return form.Message;
    }

    internal sealed class CommitMessageForm : Form
    {
        readonly TextBox message = new()
        {
            Name = "message",
            Width = 380
        };

        public string Message => message.Text.Trim();

        public CommitMessageForm(string description, string defaultMessage)
        {
            AutoScaleDimensions = new(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Git push";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new(9);

            message.Text = defaultMessage;
            var label = new Label
            {
                AutoSize = true,
                MaximumSize = new(380, 0),
                Margin = new(3, 3, 3, 6),
                Text = $"{description}\n\nCommit message:"
            };
            var push = new Button
            {
                Name = "push",
                Text = "Push",
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

            // An empty message would make git refuse the commit.
            message.TextChanged += (_, _) => push.Enabled = Message.Length > 0;

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
            layout.Controls.Add(message);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }
    }
}
