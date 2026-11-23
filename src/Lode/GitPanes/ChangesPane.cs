/// <summary>
/// The Changes pane, laid out as GitHub Desktop's Changes tab: what is uncommitted in the
/// repository, file by file, each ticked to go in the next commit, and the commit's summary and
/// description beneath, with Commit to the branch. Ticks and what is typed are kept for each
/// repository while Lode runs, so going to another and back loses neither. Double-click a file to
/// see its changes; right-click to discard them, open it, show it in its folder, copy its path, or
/// have git ignore it.
/// </summary>
sealed class ChangesPane : UserControl
{
    // Pixels at 100% scale.
    const int inset = DesktopStyle.Inset;
    const int tickGap = 7;

    PaneHeader header = new("Changes", Icons.Changes);
    FramedTextBox filterFrame = new();
    Panel filterRow;
    TickRow tickRow;
    RowList list;
    Panel commitArea = new();
    FramedTextBox summaryFrame = new();
    FramedArea descriptionFrame = new();

    // The summary, and at its right the button that shows the description, which is hidden until
    // asked for or holding something.
    Panel summaryRow = new()
    {
        Dock = DockStyle.Top
    };

    GlyphButton describeButton = new()
    {
        Dock = DockStyle.Right,
        Width = 28,
        TabStop = false,
        Text = Icons.Description.ToString()
    };

    Panel descriptionSpacer = Spacer();
    bool describing;
    AccentButton commitButton = new();
    ToolTip tip = new();
    RowTip rowTip;
    Settings settings;

    string? repo;
    GitChanges changes = GitChanges.Empty;
    IReadOnlyList<Change> shown = [];
    bool showing;
    bool stale = true;
    bool reading;
    bool readAgain;
    bool committing;
    int version;
    bool loadingDraft;

    // What is typed, and what is left unticked, in each repository, while Lode runs.
    sealed class Draft
    {
        public HashSet<string> Unticked { get; } = new(StringComparer.Ordinal);
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
    }

    static Dictionary<string, Draft> drafts = new(StringComparer.OrdinalIgnoreCase);
    Draft draft = new();

    /// <summary>
    /// Reads what is uncommitted. Tests answer for git.
    /// </summary>
    internal static Func<string, Cancel, GitChanges?> Read { get; set; } = GitChanges.Read;

    public event Action<string>? ShowInFolderRequested;
    public event Action? EscapePressed;

    /// <summary>
    /// A commit or discard finished, for the window to read the repository's state again.
    /// </summary>
    public event Action? Committed;

    public PaneHeader Header => header;

    /// <summary>
    /// Whether one of the pane's menus is open, so it doesn't slide away from under it.
    /// </summary>
    public bool MenuOpen { get; private set; }

    /// <summary>
    /// The list of files. For tests.
    /// </summary>
    internal RowList List => list;

    /// <summary>
    /// What was last read. For tests.
    /// </summary>
    internal GitChanges Changes => changes;

    /// <summary>
    /// The reading under way, for tests to wait on.
    /// </summary>
    internal Task Reading { get; private set; } = Task.CompletedTask;

    internal TextBox SummaryBox => summaryFrame.Box;
    internal TextBox DescriptionBox => descriptionFrame.Box;
    internal Button CommitButton => commitButton;

    public ChangesPane(Settings settings)
    {
        this.settings = settings;
        BackColor = ExplorerStyle.Window;
        list = new(RowHeight(Font));
        tickRow = new(this);

        filterFrame.Dock = DockStyle.Top;
        filterFrame.Desktop = true;
        filterFrame.Box.PlaceholderText = "Filter";
        filterFrame.Box.AccessibleName = "Filter changed files";
        filterFrame.Box.TextChanged += (_, _) => ShowRows();
        // The filter and the tick for every file in one grey block above the files, as
        // Desktop's header over its list is. Hidden until the button beside how many files
        // have changed shows it.
        filterRow = new()
        {
            Dock = DockStyle.Top,
            Padding = new(inset, 7, inset, 2),
            Height = filterFrame.Height + 9,
            BackColor = DesktopStyle.AltBackground,
            Visible = false
        };
        filterRow.Controls.Add(filterFrame);
        tickRow.FilterButton.Click += (_, _) => ShowFilter(!filtering);
        ShowFilterTip();

        list.AccessibleName = "Changed files";
        list.DrawRow += DrawRow;
        list.MouseDown += OnListMouseDown;
        list.MouseUp += OnListMouseUp;
        list.KeyDown += OnListKeyDown;
        list.ItemActivate += (_, _) => OpenDiff(list.FocusedItem?.Index ?? -1);
        rowTip = new(list, _ => ChangeRows.TipAt(list, _, shown));
        list.ShowItemToolTips = false;
        list.GotFocus += (_, _) => list.Invalidate();
        list.LostFocus += (_, _) => list.Invalidate();

        summaryFrame.Dock = DockStyle.Fill;
        summaryFrame.Desktop = true;
        summaryFrame.Box.PlaceholderText = "Summary (required)";
        summaryFrame.Box.AccessibleName = "Summary";
        summaryFrame.Box.TextChanged += (_, _) => OnTyped();
        summaryRow.Height = summaryFrame.Height;

        // Docked from the last added: the button at the right, a gap, and the summary across
        // what is left.
        summaryRow.Controls.Add(summaryFrame);
        summaryRow.Controls.Add(
            new Panel
            {
                Dock = DockStyle.Right,
                Width = 4
            });
        summaryRow.Controls.Add(describeButton);
        describeButton.Click += (_, _) => ToggleDescription();

        descriptionFrame.Dock = DockStyle.Top;
        descriptionFrame.Desktop = true;
        descriptionFrame.Height = 84;
        descriptionFrame.Visible = false;
        descriptionSpacer.Visible = false;
        descriptionFrame.Box.PlaceholderText = "Description";
        descriptionFrame.Box.AccessibleName = "Description";
        descriptionFrame.Box.TextChanged += (_, _) => OnDescriptionTyped();
        ShowDescriptionTip();
        commitButton.Dock = DockStyle.Top;
        commitButton.Height = 28;
        commitButton.Click += (_, _) => Commit();
        tip.SetToolTip(commitButton, "Commit the ticked files (Ctrl+Enter)");

        commitArea.Dock = DockStyle.Bottom;
        commitArea.Padding = new(inset, 10, inset, inset);
        commitArea.BackColor = DesktopStyle.AltBackground;
        commitArea.Paint += (_, args) =>
        {
            using var pen = new Pen(DesktopStyle.Border);
            args.Graphics.DrawLine(pen, 0, 0, commitArea.Width, 0);
        };

        // Docked from the last added: so the summary is at the top, the button at the bottom.
        commitArea.Controls.Add(commitButton);
        commitArea.Controls.Add(Spacer());
        commitArea.Controls.Add(descriptionFrame);
        commitArea.Controls.Add(descriptionSpacer);
        commitArea.Controls.Add(summaryRow);

        // The list fills what the rest leave.
        Controls.Add(list);
        Controls.Add(commitArea);
        Controls.Add(tickRow);
        Controls.Add(filterRow);
        Controls.Add(header);
        ShowCommitButton();
    }

    bool filtering;

    /// <summary>
    /// Whether the filter shows. For tests.
    /// </summary>
    internal bool Filtering => filtering;

    internal TextBox FilterBox => filterFrame.Box;

    internal GlyphButton FilterButton => tickRow.FilterButton;

    /// <summary>
    /// Shows the filter, with the focus in it, or hides it again. Hidden, it filters nothing: what
    /// was typed is cleared, every file listed again, and the focus, if it was in it, goes to them.
    /// </summary>
    internal void ShowFilter(bool show)
    {
        if (filtering == show)
        {
            if (show)
            {
                filterFrame.Box.Focus();
            }

            return;
        }

        var focused = filterFrame.ContainsFocus;
        filtering = show;
        filterRow.Visible = show;
        tickRow.FilterButton.Checked = show;
        ShowFilterTip();
        if (show)
        {
            filterFrame.Box.Focus();
            return;
        }

        filterFrame.Box.Text = "";
        if (focused)
        {
            list.Focus();
        }
    }

    void ShowFilterTip()
    {
        var button = tickRow.FilterButton;
        button.AccessibleName = "Filter changed files";
        button.AccessibleDescription = "Filter the files by name";
        if (filtering)
        {
            button.AccessibleName = "Hide the filter";
            button.AccessibleDescription = "Hide the filter (Esc), and list every file again";
        }

        tip.SetToolTip(button, button.AccessibleDescription);
    }

    /// <summary>
    /// Whether the description shows. For tests.
    /// </summary>
    internal bool Describing => describing;

    internal GlyphButton DescribeButton => describeButton;

    // The button beside the summary: the description shown, with the focus in it, or, shown and
    // empty, put away again. Shown with something in it, the button only puts the focus there:
    // hidden, what is in it would go into the commit unseen.
    void ToggleDescription()
    {
        if (describing &&
            descriptionFrame.Box.TextLength == 0)
        {
            ShowDescription(false);
            return;
        }

        ShowDescription(true);
    }

    /// <summary>
    /// Shows the description, the focus going into it when <paramref name="focus"/>, or hides it,
    /// the focus going to the summary if it was in it.
    /// </summary>
    internal void ShowDescription(bool show, bool focus = true)
    {
        if (describing != show)
        {
            var focused = descriptionFrame.ContainsFocus;
            describing = show;
            descriptionFrame.Visible = show;
            descriptionSpacer.Visible = show;
            describeButton.Checked = show;
            ShowDescriptionTip();
            PerformLayout();
            if (!show &&
                focused)
            {
                summaryFrame.Box.Focus();
            }
        }

        if (show &&
            focus)
        {
            descriptionFrame.Box.Focus();
        }
    }

    // Typed into, or filled from the draft: shown whenever it holds something.
    void OnDescriptionTyped()
    {
        if (descriptionFrame.Box.TextLength > 0)
        {
            ShowDescription(true, focus: false);
        }

        ShowDescriptionTip();
        OnTyped();
    }

    void ShowDescriptionTip()
    {
        describeButton.AccessibleName = "Add a description";
        describeButton.AccessibleDescription = "Add a description";
        if (describing &&
            descriptionFrame.Box.TextLength == 0)
        {
            describeButton.AccessibleName = "Hide the description";
            describeButton.AccessibleDescription = "Hide the description (Esc)";
        }
        else if (describing)
        {
            describeButton.AccessibleName = "Description";
            describeButton.AccessibleDescription = "The description goes in the commit: clear it to hide it";
        }

        tip.SetToolTip(describeButton, describeButton.AccessibleDescription);
    }

    static Panel Spacer() =>
        new()
        {
            Dock = DockStyle.Top,
            Height = 8
        };

    static int RowHeight(Font font) =>
        DesktopStyle.RowHeight(font);

    protected override void OnLayout(LayoutEventArgs args)
    {
        // As tall as what is in it, which the summary's font decides, the description giving up
        // room for at least three files when the pane is short: sharing the column with History.
        var height = commitArea.Padding.Vertical;
        foreach (Control control in commitArea.Controls)
        {
            if (control != descriptionFrame &&
                (control != descriptionSpacer || describing))
            {
                height += control.Height;
            }
        }

        var filterHeight = 0;
        if (filtering)
        {
            filterHeight = filterRow.Height;
        }

        if (describing)
        {
            var room = Height - header.Height - filterHeight - tickRow.Height - height - RowHeight(Font) * 3;
            var description = Math.Clamp(room, LogicalToDeviceUnits(36), LogicalToDeviceUnits(84));
            if (descriptionFrame.Height != description)
            {
                descriptionFrame.Height = description;
            }

            height += description;
        }

        if (commitArea.Height != height)
        {
            commitArea.Height = height;
        }

        base.OnLayout(args);
    }

    /// <summary>
    /// The repository the pane is about, or null outside one. A new one is read once the pane
    /// shows.
    /// </summary>
    public void Bind(string? repo)
    {
        if (SameRepo(this.repo, repo))
        {
            return;
        }

        KeepDraft();
        this.repo = repo;
        version++;
        changes = GitChanges.Empty;
        draft = DraftOf(repo);
        loadingDraft = true;
        summaryFrame.Box.Text = draft.Summary;
        descriptionFrame.Box.Text = draft.Description;
        loadingDraft = false;
        ShowDescription(draft.Description.Length > 0, focus: false);
        filterFrame.Box.Text = "";
        ShowRows();
        stale = true;
        if (showing)
        {
            Reload();
        }
    }

    internal static bool SameRepo(string? first, string? second)
    {
        if (first == null ||
            second == null)
        {
            return first == second;
        }

        return Locations.Same(first, second);
    }

    static Draft DraftOf(string? repo)
    {
        if (repo == null)
        {
            return new();
        }

        if (!drafts.TryGetValue(repo, out var found))
        {
            found = new();
            drafts[repo] = found;
        }

        return found;
    }

    void KeepDraft()
    {
        draft.Summary = summaryFrame.Box.Text;
        draft.Description = descriptionFrame.Box.Text;
    }

    void OnTyped()
    {
        if (!loadingDraft)
        {
            KeepDraft();
        }

        ShowCommitButton();
    }

    /// <summary>
    /// Puts the focus in the list of files, on the first when none is picked, or in the summary
    /// when nothing has changed.
    /// </summary>
    public void FocusContent()
    {
        if (shown.Count == 0)
        {
            summaryFrame.Box.Focus();
            return;
        }

        if (list.SelectedIndices.Count == 0)
        {
            list.SelectedIndices.Add(0);
            list.FocusedItem = list.Items[0];
        }

        list.Focus();
    }

    /// <summary>
    /// Whether the pane can be seen, pinned or slid out. Only then is it read.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Showing
    {
        get => showing;
        set
        {
            showing = value;
            if (value &&
                stale)
            {
                Reload();
            }
        }
    }

    /// <summary>
    /// Reads the repository again, once it shows: something in it changed.
    /// </summary>
    public void Reload()
    {
        stale = true;
        if (!showing ||
            repo == null)
        {
            return;
        }

        if (reading)
        {
            readAgain = true;
            return;
        }

        Reading = ReadAsync();
    }

    async Task ReadAsync()
    {
        var reading = repo!;
        var asked = ++version;
        this.reading = true;
        stale = false;
        try
        {
            var read = await Task.Run(() => Read(reading, Cancel.None));
            if (asked != version ||
                IsDisposed)
            {
                return;
            }

            changes = read ?? GitChanges.Empty;

            // Unticked files no longer changed are forgotten: changed again, they start ticked.
            draft.Unticked.IntersectWith(changes.Files.Select(_ => _.Path));
            ShowRows();
        }
        finally
        {
            this.reading = false;
        }

        if (readAgain &&
            !IsDisposed)
        {
            readAgain = false;
            Reload();
        }
    }

    // The rows for what is read and what the filter matches, keeping the selection by path.
    void ShowRows()
    {
        var selected = list.Selected()
            .Where(_ => _ < shown.Count)
            .Select(_ => shown[_].Path)
            .ToHashSet(StringComparer.Ordinal);
        var focused = list.FocusedItem?.Index ?? -1;
        string? focusedPath = null;
        if (focused >= 0 &&
            focused < shown.Count)
        {
            focusedPath = shown[focused].Path;
        }

        shown = CommandPalette.Match(changes.Files, _ => _.Path, filterFrame.Box.Text);
        list.BeginUpdate();
        list.SelectedIndices.Clear();
        list.SetRows(shown.Count);
        for (var index = 0; index < shown.Count; index++)
        {
            if (selected.Contains(shown[index].Path))
            {
                list.SelectedIndices.Add(index);
            }

            if (shown[index].Path == focusedPath)
            {
                list.FocusedItem = list.Items[index];
            }
        }

        list.EndUpdate();
        list.Invalidate();
        tickRow.Invalidate();
        header.Count = changes.Files.Count;
        ShowCommitButton();
    }

    bool IsTicked(Change change) =>
        changes.Merging ||
        !draft.Unticked.Contains(change.Path);

    IReadOnlyList<Change> Ticked() =>
        changes.Files
            .Where(IsTicked)
            .ToList();

    /// <summary>
    /// Whether every file showing is ticked, none is, or some are, for the tick above the list.
    /// </summary>
    bool? AllTicked()
    {
        var ticked = shown.Count(IsTicked);
        if (ticked == 0)
        {
            return false;
        }

        if (ticked == shown.Count)
        {
            return true;
        }

        return null;
    }

    // Ticks every file showing, or, with every one ticked, none.
    void TickAll()
    {
        if (changes.Merging)
        {
            return;
        }

        var tick = AllTicked() != true;
        foreach (var change in shown)
        {
            SetTick(change, tick);
        }

        list.Invalidate();
        tickRow.Invalidate();
        ShowCommitButton();
    }

    void SetTick(Change change, bool tick)
    {
        if (tick)
        {
            draft.Unticked.Remove(change.Path);
            return;
        }

        draft.Unticked.Add(change.Path);
    }

    void Toggle(IReadOnlyList<int> rows)
    {
        if (changes.Merging ||
            rows.Count == 0)
        {
            return;
        }

        // As a list of checkboxes does with several selected: all as the first becomes.
        var tick = !IsTicked(shown[rows[0]]);
        foreach (var row in rows)
        {
            SetTick(shown[row], tick);
        }

        list.Invalidate();
        tickRow.Invalidate();
        ShowCommitButton();
    }

    void ShowCommitButton()
    {
        var branch = changes.Branch;
        if (changes.Merging)
        {
            commitButton.Text = "Commit merge";
            commitButton.Bold = "";
        }
        else if (branch == null)
        {
            commitButton.Text = "Commit";
            commitButton.Bold = "";
        }
        else
        {
            // As Desktop says it: "Commit 2 files to main", or "Commit to main" with none ticked.
            commitButton.Text = "Commit to ";
            var ticked = changes.Files.Count(IsTicked);
            if (ticked > 0)
            {
                commitButton.Text = $"Commit {GitState.CountOf(ticked, "file")} to ";
            }

            commitButton.Bold = branch;
        }

        commitButton.Enabled = CanCommit;
        commitButton.Invalidate();
    }

    bool CanCommit =>
        repo != null &&
        !committing &&
        summaryFrame.Box.Text.Trim().Length > 0 &&
        !changes.Conflicted &&
        changes.Files.Any(IsTicked);

    /// <summary>
    /// Commits the ticked files with the summary and description, in the Actions panel. What is
    /// typed is cleared once it has gone in.
    /// </summary>
    internal async void Commit()
    {
        if (!CanCommit)
        {
            return;
        }

        var committingIn = repo!;
        var ticked = Ticked();
        var merging = changes.Merging;
        var message = GitChanges.Message(summaryFrame.Box.Text, descriptionFrame.Box.Text);
        committing = true;
        ShowCommitButton();
        try
        {
            var entry = await ActionLog.Run(
                "Git commit",
                [committingIn],
                async (_, cancel) =>
                {
                    var result = await Task.Run(() => GitChanges.Commit(committingIn, ticked, merging, message, cancel), cancel);
                    GitCache.MarkStale([committingIn]);
                    return CodeCommands.GitOutcome([result]);
                });
            if (entry.State == ActionLog.State.Succeeded)
            {
                var committed = DraftOf(committingIn);
                committed.Summary = "";
                committed.Description = "";
                if (SameRepo(committingIn, repo))
                {
                    loadingDraft = true;
                    summaryFrame.Box.Text = "";
                    descriptionFrame.Box.Text = "";
                    loadingDraft = false;
                    ShowDescription(false);
                }
            }
        }
        finally
        {
            committing = false;
            if (!IsDisposed)
            {
                ShowCommitButton();
                Reload();
                Committed?.Invoke();
            }
        }
    }

    // Puts the files back as the last commit has them, having asked, the files as they are going
    // to the Recycle Bin first.
    async void Discard(IReadOnlyList<Change> discarding)
    {
        if (repo is not { } discardingIn ||
            discarding.Count == 0)
        {
            return;
        }

        var what = $"the changes to {discarding[0].Path}";
        if (discarding.Count > 1)
        {
            what = $"the changes to {discarding.Count:N0} files";
        }

        if (AskDiscard(this, $"Discard {what}?\n\nThe files as they are now go to the Recycle Bin.") != DialogResult.OK)
        {
            return;
        }

        var onDisk = discarding
            .Where(_ => _.Kind != GitChange.Deleted)
            .Select(_ => FullPath(discardingIn, _.Path))
            .Where(File.Exists)
            .ToList();
        if (!await FileOperations.Delete(Handle, onDisk, permanent: false))
        {
            return;
        }

        var hasCommits = changes.HasCommits;
        await ActionLog.Run(
            "Git discard",
            [discardingIn],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => GitChanges.Restore(discardingIn, discarding, hasCommits, cancel), cancel);
                GitCache.MarkStale([discardingIn]);
                return CodeCommands.GitOutcome([result]);
            });
        if (!IsDisposed)
        {
            Reload();
            Committed?.Invoke();
        }
    }

    /// <summary>
    /// Asks whether to discard. Tests answer for the user.
    /// </summary>
    internal static Func<Control, string, DialogResult> AskDiscard { get; set; } = (owner, text) =>
        MessageBox.Show(owner, text, "Discard changes", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

    static string FullPath(string repo, string path) =>
        Path.Combine(repo, path.Replace('/', '\\'));

    void OpenDiff(int row)
    {
        if (repo == null ||
            row < 0 ||
            row >= shown.Count)
        {
            return;
        }

        GitDiff.OfWorkingFile(this, settings, repo, shown[row]);
    }

    void Open(Change change)
    {
        if (repo == null)
        {
            return;
        }

        var path = FullPath(repo, change.Path);
        if (TextEditors.IsText(path))
        {
            TextEditors.Open(this, settings, [path]);
            return;
        }

        FileOperations.Open(this, path);
    }

    void Ignore(IReadOnlyList<Change> ignoring)
    {
        if (repo == null)
        {
            return;
        }

        foreach (var change in ignoring)
        {
            GitChanges.Ignore(repo, change.Path);
        }

        Reload();
    }

    // Where a click in a row's tick lands: from the left edge to just past the tick.
    bool OnTick(Point point) =>
        point.X < LogicalToDeviceUnits(inset + Ticks.Size + tickGap / 2);

    void OnListMouseDown(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left ||
            !OnTick(args.Location) ||
            list.HitTest(args.Location).Item is not { } item)
        {
            return;
        }

        // The rows selected all take the tick's new state when the tick of one of them is
        // clicked; otherwise just the row clicked.
        var rows = list.Selected();
        if (!rows.Contains(item.Index))
        {
            rows = [item.Index];
        }

        Toggle(rows);
    }

    void OnListMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right ||
            repo is not { } menuRepo)
        {
            return;
        }

        if (list.HitTest(args.Location).Item is { } item &&
            !list.SelectedIndices.Contains(item.Index))
        {
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(item.Index);
            list.FocusedItem = item;
        }

        var selected = list.Selected()
            .Where(_ => _ < shown.Count)
            .Select(_ => shown[_])
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        PaneMenu.Show(list, args.Location, MenuFor(menuRepo, selected), open => MenuOpen = open);
    }

    List<QuickMenu.Command?> MenuFor(string menuRepo, IReadOnlyList<Change> selected)
    {
        var commands = new List<QuickMenu.Command?>
        {
            new("Discard changes…", () => Discard(selected))
            {
                Glyph = Icons.Discard
            }
        };
        if (selected is [var one])
        {
            var path = FullPath(menuRepo, one.Path);
            var exists = File.Exists(path);
            commands.Add(null);
            commands.Add(
                new("Show changes", () => GitDiff.OfWorkingFile(this, settings, menuRepo, one))
                {
                    Shortcut = "Enter"
                });
            commands.Add(
                new("Open", () => Open(one), exists)
                {
                    Glyph = Icons.Open
                });
            commands.Add(
                new("Show in folder", () => ShowInFolderRequested?.Invoke(path), exists)
                {
                    Glyph = Icons.Folder
                });
        }

        commands.Add(
            new("Copy path", () => FolderView.SetClipboardText(string.Join(Environment.NewLine, selected.Select(_ => FullPath(menuRepo, _.Path)))))
            {
                Glyph = Icons.CopyAsPath
            });
        var untracked = selected
            .Where(_ => _.Kind == GitChange.Untracked)
            .ToList();
        if (untracked.Count > 0)
        {
            commands.Add(null);
            var text = "Ignore file";
            if (untracked.Count > 1)
            {
                text = $"Ignore {untracked.Count:N0} files";
            }

            commands.Add(new(text, () => Ignore(untracked)));
        }

        return commands;
    }

    void OnListKeyDown(object? sender, KeyEventArgs args)
    {
        switch (args.KeyData)
        {
            case Keys.Space:
                Toggle(list.Selected());
                args.Handled = true;
                args.SuppressKeyPress = true;
                return;
            case Keys.Delete:
                Discard(list.Selected().Select(_ => shown[_]).ToList());
                args.Handled = true;
                return;
            case Keys.Control | Keys.A:
                list.BeginUpdate();
                for (var index = 0; index < shown.Count; index++)
                {
                    list.SelectedIndices.Add(index);
                }

                list.EndUpdate();
                args.Handled = true;
                return;
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter) &&
            (summaryFrame.Box.Focused || descriptionFrame.Box.Focused))
        {
            Commit();
            return true;
        }

        if (keyData == Keys.Escape)
        {
            // In the filter, Escape puts it away first, and so in the description, empty.
            if (filtering &&
                filterFrame.ContainsFocus)
            {
                ShowFilter(false);
                return true;
            }

            if (describing &&
                descriptionFrame is { ContainsFocus: true, Box.TextLength: 0 })
            {
                ShowDescription(false);
                return true;
            }

            EscapePressed?.Invoke();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    void DrawRow(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= shown.Count)
        {
            return;
        }

        var change = shown[index];
        var (background, text, secondary, onBlue) = list.Colours(index);
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        RowList.DrawRowLine(graphics, bounds);
        var tickSize = LogicalToDeviceUnits(Ticks.Size);
        var box = new Rectangle(bounds.Left + LogicalToDeviceUnits(inset), bounds.Top + (bounds.Height - tickSize) / 2, tickSize, tickSize);
        Ticks.Draw(graphics, this, box, IsTicked(change), !changes.Merging);
        var room = Rectangle.FromLTRB(box.Right + LogicalToDeviceUnits(tickGap), bounds.Top, bounds.Right, bounds.Bottom);
        ChangeRows.Draw(graphics, this, list.Font, room, change, text, secondary, onBlue);
    }

    // Slid out, a line down the left edge, which the padding leaves room for.
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (Padding.Left > 0)
        {
            using var pen = new Pen(ExplorerStyle.Divider);
            args.Graphics.DrawLine(pen, 0, 0, 0, Height);
        }
    }

    protected override void OnPaddingChanged(EventArgs args)
    {
        base.OnPaddingChanged(args);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            KeepDraft();
            tip.Dispose();
            rowTip.Dispose();
        }

        base.Dispose(disposing);
    }

    // The tick for every file showing, how many have changed, and at the right the button that
    // shows the filter.
    sealed class TickRow : Control
    {
        ChangesPane pane;

        public GlyphButton FilterButton { get; } = new()
        {
            Dock = DockStyle.Right,
            Width = 28,
            TabStop = false,
            Text = Icons.Filter.ToString()
        };

        public TickRow(ChangesPane pane)
        {
            this.pane = pane;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Dock = DockStyle.Top;
            Height = 29;
            BackColor = DesktopStyle.AltBackground;
            ForeColor = DesktopStyle.Text;
            AccessibleRole = AccessibleRole.CheckButton;
            Cursor = Cursors.Default;

            // The button inset from the edge and the line along the foot, as the pin is.
            Padding = new(0, 2, 4, 3);
            Controls.Add(FilterButton);
        }

        // The button docked inside the padding. A Control, unlike a Panel, docks its children over
        // all of itself, so the button ran to the foot of the row, over its line.
        public override Rectangle DisplayRectangle
        {
            get
            {
                var bounds = ClientRectangle;
                return Rectangle.FromLTRB(
                    bounds.Left + Padding.Left,
                    bounds.Top + Padding.Top,
                    Math.Max(bounds.Left + Padding.Left, bounds.Right - Padding.Right),
                    Math.Max(bounds.Top + Padding.Top, bounds.Bottom - Padding.Bottom));
            }
        }

        string Caption()
        {
            var count = pane.changes.Files.Count;
            if (count == 1)
            {
                return "1 changed file";
            }

            return $"{count:N0} changed files";
        }

        protected override void OnMouseClick(MouseEventArgs args)
        {
            base.OnMouseClick(args);
            if (args.Button == MouseButtons.Left)
            {
                pane.TickAll();
            }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(BackColor);
            var tickSize = LogicalToDeviceUnits(Ticks.Size);
            var left = LogicalToDeviceUnits(inset);
            var box = new Rectangle(left, (Height - tickSize) / 2, tickSize, tickSize);
            if (pane.shown.Count > 0)
            {
                Ticks.Draw(graphics, this, box, pane.AllTicked(), !pane.changes.Merging);
            }

            var textBounds = Rectangle.FromLTRB(box.Right + LogicalToDeviceUnits(tickGap), 0, FilterButton.Left - LogicalToDeviceUnits(4), Height);
            TextRenderer.DrawText(
                graphics,
                Caption(),
                Font,
                textBounds,
                ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            using var pen = new Pen(DesktopStyle.Border);
            graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
