/// <summary>
/// The Changes pane, laid out as GitHub Desktop's Changes tab: what is uncommitted in the
/// repository, file by file, each ticked to go in the next commit, and the commit's summary and
/// description beneath, with Commit to the branch, or, once History's Amend commit asks, Amend
/// last commit, putting them in place of the last commit. Ticks and what is typed are kept for each
/// repository while Lode runs, so going to another and back loses neither. Double-click a file to
/// see its changes; right-click to discard them, stash every change, open it, show it in its
/// folder, copy its path, or have git ignore it. The repository's stashes are under the files, to
/// restore or discard.
/// </summary>
sealed class ChangesPane : UserControl
{
    // Pixels at 100% scale.
    const int inset = DesktopStyle.Inset;
    const int tickGap = 7;

    PaneHeader header = new(GitPane.Changes);
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

    // Amending, as GitHub Desktop's notice has it above the button: the commit goes in place of
    // the last, with a link back to a new one.
    LinkLabel amendNotice = new()
    {
        Name = "amendNotice",
        Dock = DockStyle.Top,
        AutoSize = false,
        UseMnemonic = false,
        Visible = false,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Text = "Your changes will modify your most recent commit. Stop amending to make these changes as a new commit."
    };

    Panel amendSpacer = Spacer();
    bool amendShown;
    AccentButton commitButton = new();
    ToolTip tip = new();

    // Part way through a merge, as GitHub Desktop's banner has it: what is being merged, how many
    // files are left to settle, and a button giving the merge up; part way through a rebase or a
    // cherry-pick, as its dialog has it, one going on with it too.
    TableLayoutPanel mergeBar = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 3,
        RowCount = 1,
        Visible = false
    };
    Label mergeText = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        UseMnemonic = false,
        Margin = new(0, 3, 6, 3)
    };
    Button continueButton = new()
    {
        Name = "continueRebase",
        Text = "Continue rebase",
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Visible = false
    };
    Button abortButton = new()
    {
        Name = "abortMerge",
        Text = "Abort merge",
        AutoSize = true,
        Anchor = AnchorStyles.Right
    };
    RowTip rowTip;
    Settings settings;

    // In place of the files, once they are read and there are none.
    NoChanges noChanges = new()
    {
        Dock = DockStyle.Fill,
        Visible = false
    };

    // Under the files, the repository's stashes, as they were read with them.
    StashList stashList = new();
    IReadOnlyList<GitStashes.Stash> stashes = [];
    bool stashing;

    string? repo;
    GitChanges changes = GitChanges.Empty;
    IReadOnlyList<Change> shown = [];

    // Whether what is uncommitted has been read for the repository, so it isn't said to be
    // nothing while it is read.
    bool readYet;

    // What git said when it last couldn't read the repository, shown in place of the files, or
    // null once it could.
    string? failure;
    bool noneShown;
    bool showing;
    bool stale = true;
    bool reading;
    bool readAgain;
    bool committing;
    int version;
    bool loadingDraft;

    // What is typed, what is left unticked, and the lines picked, in the repository, kept for
    // each while Lode runs, and shared with the diff.
    CommitDrafts.Draft draft = new();

    /// <summary>
    /// Reads what is uncommitted, or what git said where it couldn't. Tests answer for git.
    /// </summary>
    internal static Func<string, Cancel, GitChanges> Read { get; set; } = GitChanges.ReadOrWhyNot;

    /// <summary>
    /// Reads the repository's stashes, with what is uncommitted. Tests answer for git.
    /// </summary>
    internal static Func<string, Cancel, IReadOnlyList<GitStashes.Stash>> ReadStashes { get; set; } = GitStashes.Read;

    public event Action<string>? ShowInFolderRequested;
    public event Action? EscapePressed;

    /// <summary>
    /// A commit or discard finished, for the window to read the repository's state again.
    /// </summary>
    public event Action? Committed;

    /// <summary>
    /// A file picked, or several, or none, or one asked to be shown: for the diff to show it.
    /// </summary>
    public event Action<DiffRequest>? DiffRequested;

    /// <summary>
    /// What is uncommitted was read again, for a diff of one of its files to be read again too.
    /// </summary>
    public event Action<string>? ChangesRead;

    /// <summary>
    /// With nothing uncommitted, what to do next was clicked, for the part of the repository's
    /// state it is about to be asked about, as the status bar's part is.
    /// </summary>
    public event Action<GitState.PartKind>? ActRequested;

    /// <summary>
    /// Whether it says nothing is uncommitted, or what git said where it couldn't read it, in
    /// place of the files. For tests.
    /// </summary>
    internal bool NoneShown => noneShown;

    internal NoChanges NoChanges => noChanges;

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

    /// <summary>
    /// The stash being restored, discarded or made, for tests to wait on.
    /// </summary>
    internal Task Stashing { get; private set; } = Task.CompletedTask;

    internal StashList StashList => stashList;

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
        list.ItemActivate += (_, _) => Activate(list.FocusedItem?.Index ?? -1);
        list.SelectedIndexChanged += (_, _) => PickedChanged();
        list.VirtualItemsSelectionRangeChanged += (_, _) => PickedChanged();
        CommitDrafts.PicksChanged += OnPicksChanged;
        DiffView.LinesDiscarded += OnLinesDiscarded;
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

        amendNotice.ForeColor = DesktopStyle.Text;
        const string stop = "Stop amending";
        amendNotice.LinkArea = new(amendNotice.Text.IndexOf(stop, StringComparison.Ordinal), stop.Length);
        amendNotice.LinkClicked += (_, _) => StopAmending();
        amendSpacer.Visible = false;

        // Docked from the last added: so the summary is at the top, the button at the bottom.
        commitArea.Controls.Add(commitButton);
        commitArea.Controls.Add(amendSpacer);
        commitArea.Controls.Add(amendNotice);
        commitArea.Controls.Add(Spacer());
        commitArea.Controls.Add(descriptionFrame);
        commitArea.Controls.Add(descriptionSpacer);
        commitArea.Controls.Add(summaryRow);

        mergeBar.Padding = new(inset, 6, inset, 6);
        mergeBar.BackColor = DesktopStyle.AltBackground;
        mergeBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        mergeBar.ColumnStyles.Add(new(SizeType.AutoSize));
        mergeBar.ColumnStyles.Add(new(SizeType.AutoSize));
        mergeBar.Controls.Add(mergeText, 0, 0);
        mergeBar.Controls.Add(continueButton, 1, 0);
        mergeBar.Controls.Add(abortButton, 2, 0);
        mergeBar.SizeChanged += (_, _) => FitMergeText();
        mergeBar.Paint += (_, args) =>
        {
            using var pen = new Pen(DesktopStyle.Border);
            args.Graphics.DrawLine(pen, 0, mergeBar.Height - 1, mergeBar.Width, mergeBar.Height - 1);
        };
        abortButton.Click += (_, _) => AbortMerge();
        continueButton.Click += (_, _) => Commit();
        noChanges.ActRequested += _ => ActRequested?.Invoke(_);
        stashList.MenuRequested += (stash, location) => PaneMenu.Show(stashList.Rows, location, StashCommands(stash), open => MenuOpen = open);
        stashList.DiscardRequested += DiscardStash;

        // The list fills what the rest leave, or, with nothing uncommitted, what says so; the
        // stashes under it, over the commit.
        Controls.Add(noChanges);
        Controls.Add(list);
        Controls.Add(stashList);
        Controls.Add(commitArea);
        Controls.Add(tickRow);
        Controls.Add(filterRow);
        Controls.Add(mergeBar);
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
        if (amendShown)
        {
            // As many lines as the notice wraps to across the pane.
            var width = Math.Max(1, DisplayRectangle.Width - commitArea.Padding.Horizontal);
            var noticeHeight = amendNotice.GetPreferredSize(new(width, 0)).Height;
            if (amendNotice.Height != noticeHeight)
            {
                amendNotice.Height = noticeHeight;
            }
        }

        var height = commitArea.Padding.Vertical;
        foreach (Control control in commitArea.Controls)
        {
            if (control != descriptionFrame &&
                (control != descriptionSpacer || describing) &&
                (amendShown || (control != amendNotice && control != amendSpacer)))
            {
                height += control.Height;
            }
        }

        var filterHeight = 0;
        if (filtering)
        {
            filterHeight = filterRow.Height;
        }

        var stashesHeight = 0;
        if (stashList.Visible)
        {
            stashesHeight = stashList.Height;
        }

        if (describing)
        {
            var room = Height - header.Height - filterHeight - tickRow.Height - stashesHeight - height - RowHeight(Font) * 3;
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
        readYet = false;
        failure = null;
        changes = GitChanges.Empty;
        stashes = [];
        draft = DraftOf(repo);
        loadingDraft = true;
        summaryFrame.Box.Text = draft.Summary;
        descriptionFrame.Box.Text = draft.Description;
        loadingDraft = false;
        ShowDescription(draft.Description.Length > 0, focus: false);
        ShowAmending();
        filterFrame.Box.Text = "";
        ShowRows();
        PickedChanged();
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

    static CommitDrafts.Draft DraftOf(string? repo) =>
        CommitDrafts.Of(repo);

    // Lines picked in the diff, or files ticked here: their ticks, and how many the commit
    // takes, follow, counted once however many changed.
    void OnPicksChanged(string pickedIn, IReadOnlySet<string> paths)
    {
        if (!SameRepo(repo, pickedIn))
        {
            return;
        }

        list.Invalidate();
        tickRow.Invalidate();
        ShowCommitButton();
    }

    // Lines discarded in the diff, wherever it shows: what is uncommitted is read again, as
    // after Discard changes here.
    void OnLinesDiscarded(string discardedIn)
    {
        if (IsDisposed ||
            !SameRepo(repo, discardedIn))
        {
            return;
        }

        Reload();
        Committed?.Invoke();
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
    /// when nothing has changed, or git couldn't read the files.
    /// </summary>
    public void FocusContent()
    {
        if (shown.Count == 0 ||
            failure != null)
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
    /// Whether the pane can be seen, beside the list or slid out. Only then is it read.
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
            var (read, stashed) = await Task.Run(() => (Read(reading, Cancel.None), ReadStashes(reading, Cancel.None)));
            if (asked != version ||
                IsDisposed)
            {
                return;
            }

            // Git couldn't read it, refusing a repository owned by another user say: what was read
            // last stays, and the draft with it, rather than taken for nothing changed, which
            // would forget every file left unticked and line picked, and what git said shows in
            // place of the files.
            failure = read.Failure;
            if (failure != null)
            {
                ShowRows();
                return;
            }

            changes = read;
            stashes = stashed;
            readYet = true;

            // The commit being amended isn't the last any more, committed on or undone since, so
            // there is nothing to amend.
            if (draft.Amending is { } amending &&
                changes.Head != null &&
                changes.Head != amending)
            {
                draft.Amending = null;
            }

            // Part way through a merge, the summary starts as git's message for it, as GitHub
            // Desktop's does, unless something is typed already.
            if (changes is { Merging: true, MergeSummary: { } summary } &&
                summaryFrame.Box.TextLength == 0)
            {
                summaryFrame.Box.Text = summary;
            }

            // Unticked files no longer changed are forgotten: changed again, they start ticked.
            draft.Keep(changes.Files.Select(_ => _.Path));
            ShowAmending();
            ShowRows();
            ChangesRead?.Invoke(reading);
        }
        finally
        {
            this.reading = false;

            // Asked for again while this was read, something in the repository changed or another
            // bound, it is read again, whether what was read was kept or not: read for the
            // repository before, it wasn't, and the one bound since would otherwise go unread.
            if (readAgain &&
                !IsDisposed)
            {
                readAgain = false;
                Reload();
            }
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
        header.ChangesCount = changes.Files.Count;
        ShowMergeBar();
        ShowCommitButton();
        ShowNone();
        ShowStashes();
    }

    // The stashes under the files; none where git couldn't read the repository, as none could
    // be restored or discarded then.
    void ShowStashes()
    {
        var showing = stashList.Visible;
        stashList.SetStashes(failure == null ? stashes : []);
        if (stashList.Visible != showing)
        {
            PerformLayout();
        }
    }

    // Nothing uncommitted, once read, says so in place of the files, and git failing to read it
    // says what git said there instead. Part way through a merge, the merge is what there is to
    // commit, with or without files.
    void ShowNone()
    {
        noChanges.ShowFailure(failure);
        var none = repo != null &&
                   (failure != null ||
                    (readYet &&
                     changes.Files.Count == 0 &&
                     !changes.Merging));
        if (none == noneShown)
        {
            return;
        }

        noneShown = none;
        noChanges.Visible = none;
        list.Visible = !none;
    }

    /// <summary>
    /// The repository's state as last read, for what to do next with nothing uncommitted.
    /// </summary>
    public void ShowState(GitState? state) =>
        noChanges.ShowState(state);

    /// <summary>
    /// What the bar over the files says part way through a merge. For tests.
    /// </summary>
    internal string MergeText => mergeText.Text;

    internal bool MergeBarShown => changes.Merging;

    internal Button AbortButton => abortButton;

    internal Button ContinueButton => continueButton;

    void ShowMergeBar()
    {
        mergeBar.Visible = changes.Merging;
        if (!changes.Merging)
        {
            return;
        }

        var continued = Continued(changes);
        mergeText.Text = MergeBarText(changes);
        abortButton.Text = $"Abort {continued ?? "merge"}";
        continueButton.Visible = continued != null;
        if (continued != null)
        {
            continueButton.Text = $"Continue {continued}";
        }

        FitMergeText();
    }

    /// <summary>
    /// What is part way, as going on with it is named, where Changes goes on with it rather than
    /// commit: a rebase, whose commit being replayed keeps its own message, or a cherry-pick,
    /// whose commit picked does. Null for a merge, or a revert, or nothing part way.
    /// </summary>
    internal static string? Continued(GitChanges changes)
    {
        if (changes.Rebasing != null)
        {
            return "rebase";
        }

        if (changes.CherryPicking != null)
        {
            return "cherry-pick";
        }

        return null;
    }

    // The text wrapping in what the buttons beside it leave.
    void FitMergeText()
    {
        var buttons = abortButton.Width;
        if (continueButton.Visible)
        {
            buttons += continueButton.Width + continueButton.Margin.Horizontal;
        }

        mergeText.MaximumSize = new(Math.Max(1, mergeBar.ClientSize.Width - buttons - mergeBar.Padding.Horizontal - 12), 0);
    }

    /// <summary>
    /// What the bar says: what is being merged into what, rebased onto what, or cherry-picked
    /// onto what, and how many files are left to settle, or that the merge is ready to commit, or
    /// the rebase or cherry-pick to go on.
    /// </summary>
    internal static string MergeBarText(GitChanges changes)
    {
        var what = "Merging";
        var next = "Commit the merge.";
        if (changes.Rebasing is { } rebase)
        {
            what = "Rebasing";
            next = "Continue the rebase.";
            if (rebase.Branch is { } rebased)
            {
                what += $" {rebased}";
            }

            if (rebase.Onto is { } onto)
            {
                what += $" onto {onto}";
            }
        }
        else if (changes.CherryPicking is { } cherryPick)
        {
            what = $"Cherry-picking {cherryPick.Commit}";
            next = "Continue the cherry-pick.";
            if (changes.Branch is { } branch)
            {
                what += $" onto {branch}";
            }
        }
        else if (changes.MergingFrom is { } from)
        {
            what = $"Merging {from}";
            if (changes.Branch is { } branch)
            {
                what += $" into {branch}";
            }
        }

        var left = changes.ConflictsLeft;
        if (left == 0)
        {
            return $"{what}: no conflicts left. {next}";
        }

        return $"{what}: {GitState.CountOf(left, "conflicted file")} left. Right-click one to pick a side, or settle its markers in the editor.";
    }

    // Whether any of the file goes in the commit: all of it, or the lines picked.
    bool IsTicked(Change change)
    {
        if (changes.Merging)
        {
            return MergeTakes(change);
        }

        return !draft.Unticked.Contains(change.Path);
    }

    /// <summary>
    /// How a file's tick shows: ticked, not, or, with only some of its lines picked, partly.
    /// </summary>
    internal bool? TickOf(Change change)
    {
        if (changes.Merging)
        {
            return MergeTakes(change);
        }

        return draft.TickOf(change.Path);
    }

    // Part way through a merge, every file goes in its commit; part way through a rebase, every
    // file git tracks goes in the commit being replayed, one it doesn't being no part of the
    // branch rebased, and so for the commit cherry-picked.
    bool MergeTakes(Change change) =>
        Continued(changes) == null ||
        change.Kind != GitChange.Untracked;

    IReadOnlyList<Change> Ticked() =>
        changes.Files
            .Where(IsTicked)
            .ToList();

    /// <summary>
    /// Whether every file showing is ticked whole, none is ticked at all, or some, for the tick
    /// above the list.
    /// </summary>
    bool? AllTicked()
    {
        var ticks = shown.Select(TickOf).ToList();
        if (ticks.All(_ => _ == false))
        {
            return false;
        }

        if (ticks.All(_ => _ == true))
        {
            return true;
        }

        return null;
    }

    // Ticks every file showing, or, with every one ticked, none. Not the files read last, hidden
    // while git can't read them.
    internal void TickAll()
    {
        if (changes.Merging ||
            failure != null)
        {
            return;
        }

        SetTicks(shown, AllTicked() != true);
    }

    // Ticked whole, or not at all, the lines picked of them forgotten, and what shows them told
    // once, the pane itself and the diff showing one of them, rather than once a file.
    void SetTicks(IEnumerable<Change> setting, bool tick)
    {
        if (repo != null)
        {
            CommitDrafts.SetTicks(repo, setting.Select(_ => _.Path), tick);
        }
    }

    internal void Toggle(IReadOnlyList<int> rows)
    {
        if (changes.Merging ||
            rows.Count == 0)
        {
            return;
        }

        // As a list of checkboxes does with several selected: all as the first becomes. One only
        // partly ticked becomes ticked whole, as GitHub Desktop's does.
        var tick = TickOf(shown[rows[0]]) != true;
        SetTicks(rows.Select(_ => shown[_]), tick);
    }

    void ShowCommitButton()
    {
        var branch = changes.Branch;
        var tipText = "Commit the ticked files (Ctrl+Enter)";
        if (Amending)
        {
            commitButton.Text = "Amend last commit";
            commitButton.Bold = "";
            tipText = "Put the ticked files, and the summary and description, in the last commit (Ctrl+Enter)";
        }
        else if (changes.Rebasing != null)
        {
            commitButton.Text = "Continue rebase";
            commitButton.Bold = "";
            tipText = "Put the files git tracks in the commit being replayed, with its own message, and go on with the rebase (Ctrl+Enter)";
        }
        else if (changes.CherryPicking != null)
        {
            commitButton.Text = "Continue cherry-pick";
            commitButton.Bold = "";
            tipText = "Put the files settled in the commit being cherry-picked, with its own message, and make it (Ctrl+Enter)";
        }
        else if (changes.Merging)
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

        if (tip.GetToolTip(commitButton) != tipText)
        {
            tip.SetToolTip(commitButton, tipText);
        }

        commitButton.Enabled = CanCommit;
        continueButton.Enabled = CanCommit;
        commitButton.Invalidate();
    }

    // Amending, the summary alone can be changed, with no file ticked. Nothing is, where git
    // couldn't read the files, which are hidden then. Part way through a rebase, going on with
    // it needs neither, the commit being replayed keeping its own message, and taking whatever
    // there is, nothing where the conflicts were settled as what it goes onto had it; and so
    // part way through a cherry-pick.
    bool CanCommit =>
        repo != null &&
        !committing &&
        failure == null &&
        !changes.Conflicted &&
        (Continued(changes) != null ||
         (summaryFrame.Box.Text.Trim().Length > 0 &&
          (Amending || changes.Files.Any(IsTicked))));

    /// <summary>
    /// Whether the commit goes in place of the last, as Amend commit asked: never part way
    /// through a merge, which only a new commit can finish.
    /// </summary>
    internal bool Amending =>
        draft.Amending != null &&
        !changes.Merging;

    internal LinkLabel AmendNotice => amendNotice;

    /// <summary>
    /// A commit's message put in the summary and description of <paramref name="messageRepo"/>,
    /// in place of what was typed: a commit undone, to commit again, or, with
    /// <paramref name="amending"/>, the last commit, to amend, which Commit then does.
    /// </summary>
    public void TakeMessage(string messageRepo, string message, string? amending)
    {
        var (summary, description) = GitChanges.SplitMessage(message);
        var taking = DraftOf(messageRepo);
        taking.Summary = summary;
        taking.Description = description;
        taking.Amending = amending;
        if (!SameRepo(repo, messageRepo))
        {
            return;
        }

        loadingDraft = true;
        summaryFrame.Box.Text = summary;
        descriptionFrame.Box.Text = description;
        loadingDraft = false;
        ShowDescription(description.Length > 0, focus: false);
        ShowAmending();
        ShowCommitButton();
    }

    /// <summary>
    /// Puts the focus in the summary, its text selected, to be typed over or changed.
    /// </summary>
    public void FocusSummary()
    {
        summaryFrame.Box.Focus();
        summaryFrame.Box.SelectAll();
    }

    // Stop amending clicked: the commit is a new one again, with what is typed kept, as Desktop
    // keeps it.
    internal void StopAmending()
    {
        draft.Amending = null;
        ShowAmending();
        ShowCommitButton();
    }

    void ShowAmending()
    {
        var amending = Amending;
        if (amendShown == amending)
        {
            return;
        }

        amendShown = amending;
        amendNotice.Visible = amending;
        amendSpacer.Visible = amending;
        PerformLayout();
    }

    /// <summary>
    /// Commits the ticked files with the summary and description, in the Actions panel. What is
    /// typed is cleared once it has gone in. Part way through a rebase or a cherry-pick, goes on
    /// with it, what is typed kept, as it doesn't go in.
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
        var continued = Continued(changes);
        string? amending = null;
        var title = "Git commit";
        if (Amending)
        {
            amending = draft.Amending;
            title = "Git amend commit";
        }
        else if (continued != null)
        {
            title = $"Git continue {continued}";
        }

        var hasCommits = changes.HasCommits;
        var message = GitChanges.Message(summaryFrame.Box.Text, descriptionFrame.Box.Text);

        // Files only some of whose lines are picked go in as the patch of those lines; part way
        // through a merge, git commits everything.
        var picked = new List<(Change File, LinePicks Picks)>();
        if (!merging)
        {
            foreach (var change in ticked)
            {
                if (draft.Picks.TryGetValue(change.Path, out var picks))
                {
                    picked.Add((change, picks));
                }
            }
        }

        var whole = ticked
            .Where(_ => picked.All(partial => partial.File != _))
            .ToList();
        Change? changedSincePicked = null;
        committing = true;
        ShowCommitButton();
        try
        {
            var entry = await ActionLog.Run(
                title,
                [committingIn],
                async (_, cancel) =>
                {
                    var (result, changed) = await Task.Run(() => CommitPicked(committingIn, whole, picked, hasCommits, merging, message, amending, cancel), cancel);
                    changedSincePicked = changed;
                    GitCache.MarkStale([committingIn]);
                    return CodeCommands.GitOutcome([result]);
                });
            if (changedSincePicked != null)
            {
                CommitDrafts.ForgetPicks(committingIn, changedSincePicked.Path);
            }

            if (entry.State == ActionLog.State.Succeeded &&
                continued == null)
            {
                var committed = DraftOf(committingIn);
                committed.Summary = "";
                committed.Description = "";
                committed.Amending = null;
                foreach (var (file, _) in picked)
                {
                    CommitDrafts.ForgetPicks(committingIn, file.Path);
                }

                if (SameRepo(committingIn, repo))
                {
                    loadingDraft = true;
                    summaryFrame.Box.Text = "";
                    descriptionFrame.Box.Text = "";
                    loadingDraft = false;
                    ShowDescription(false);
                    ShowAmending();
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

    /// <summary>
    /// Commits the files ticked whole and the lines picked of the others, each of those read
    /// again first: one whose diff is not what its lines were picked from any more stops the
    /// commit, as the lines picked can't be told, and is given back to be ticked whole again.
    /// Amending, in place of <paramref name="amending"/>, only while that is still the last
    /// commit and not pushed.
    /// </summary>
    internal static (CodeCommands.RepoResult Result, Change? Stale) CommitPicked(
        string repo,
        IReadOnlyList<Change> whole,
        IReadOnlyList<(Change File, LinePicks Picks)> picked,
        bool hasCommits,
        bool merging,
        string message,
        string? amending,
        Cancel cancel)
    {
        if (amending != null &&
            GitHistory.WhyNotRewrite(repo, amending, cancel) is { } why)
        {
            return (new(repo, false, $"Nothing was committed: {why}. Stop amending to make a new commit."), null);
        }

        var partial = new List<PartialChange>();
        foreach (var (file, picks) in picked)
        {
            var diff = GitFileDiff.ReadToCommit(new(repo, file, null, hasCommits), picks.PatchHash, cancel);
            if (diff == null)
            {
                return (new(repo, false, $"{file.Path} changed after its lines were picked, so nothing was committed. It is ticked whole again: pick its lines again, or commit it whole."), file);
            }

            var patch = LinePatch.Format(diff, picks, file.Kind == GitChange.Deleted);
            if (patch != null)
            {
                partial.Add(new(file, patch));
            }
        }

        return (GitChanges.Commit(repo, whole, merging, message, cancel, partial, amending != null), null);
    }

    /// <summary>
    /// For files in conflict, as GitHub Desktop offers them: one file settled with one side's
    /// file whole, as <see cref="SideNames"/> names them, or a deletion where that side deleted
    /// it; then files taken as they are now, once no markers are left in them. None otherwise.
    /// </summary>
    internal List<QuickMenu.Command?> ConflictCommands(IReadOnlyList<Change> selected)
    {
        if (selected.Count == 0 ||
            selected.Any(_ => _.Kind != GitChange.Conflicted))
        {
            return [];
        }

        var commands = new List<QuickMenu.Command?>();
        if (selected is [var one])
        {
            var (ours, theirs) = SideNames(changes);
            commands.Add(new(SideText(one.Conflict?.OursDeleted == true, ours), () => Settle($"Git use {ours}'s {one.Path}", (repo, cancel) => GitChanges.UseSide(repo, one, ours: true, cancel))));
            commands.Add(new(SideText(one.Conflict?.TheirsDeleted == true, theirs), () => Settle($"Git use {theirs}'s {one.Path}", (repo, cancel) => GitChanges.UseSide(repo, one, ours: false, cancel))));
        }

        // Markers left in a file would go into the commit as they are.
        var noMarkers = selected.All(_ => _.Conflict is not { Manual: false, Markers: > 0 });
        commands.Add(new("Mark as resolved", () => Settle("Git mark as resolved", (repo, cancel) => GitChanges.MarkResolved(repo, selected, cancel)), noMarkers));
        commands.Add(null);
        return commands;

        static string SideText(bool deleted, string branch)
        {
            if (deleted)
            {
                return $"Use the deletion from {branch}";
            }

            return $"Use the file from {branch}";
        }
    }

    /// <summary>
    /// What git's two sides of a file in conflict are named, ours then theirs: part way through a
    /// merge, the branch checked out and the branch merged in; part way through a rebase, as
    /// GitHub Desktop names them, what it goes onto, whose commits it is replaying the branch's on
    /// top of, and the branch being rebased, whose commit is being replayed; part way through a
    /// cherry-pick, the branch checked out and the commit picked. With none part way, a stash
    /// restored over what clashed with it, the branch checked out and the stash, as git's markers
    /// name them "Updated upstream" and "Stashed changes".
    /// </summary>
    internal static (string Ours, string Theirs) SideNames(GitChanges changes)
    {
        if (changes.Rebasing is { } rebase)
        {
            return (rebase.Onto ?? "the branch rebased onto", rebase.Branch ?? "the commit being replayed");
        }

        if (changes.CherryPicking is { } cherryPick)
        {
            return (changes.Branch ?? "the branch checked out", cherryPick.Commit);
        }

        if (!changes.Merging)
        {
            return (changes.Branch ?? "the branch checked out", "the stash");
        }

        return (changes.Branch ?? "the branch checked out", changes.MergingFrom ?? "the branch merged in");
    }

    // A file in conflict settled, or the merge given up, in the Actions panel, then read again.
    async void Settle(string title, Func<string, Cancel, CodeCommands.RepoResult> work)
    {
        if (repo is not { } settlingIn)
        {
            return;
        }

        await ActionLog.Run(
            title,
            [settlingIn],
            async (_, cancel) =>
            {
                var result = await Task.Run(() => work(settlingIn, cancel), cancel);
                GitCache.MarkStale([settlingIn]);
                return CodeCommands.GitOutcome([result]);
            });
        if (!IsDisposed)
        {
            Reload();
            Committed?.Invoke();
        }
    }

    // Abort merge, Abort rebase or Abort cherry-pick: having asked, the branch and its files go
    // back to how they were before it began.
    void AbortMerge()
    {
        var what = Continued(changes) ?? "merge";
        if (repo == null ||
            !changes.Merging ||
            AskAbort(this, what) != DialogResult.OK)
        {
            return;
        }

        Settle($"Git abort {what}", GitChanges.Abort);
    }

    /// <summary>
    /// Asks whether to abort what is part way, the merge, the rebase or the cherry-pick, as
    /// named. Tests answer for the user.
    /// </summary>
    internal static Func<Control, string, DialogResult> AskAbort { get; set; } = (owner, what) =>
        MessageBoxes.Show(
            owner,
            $"Abort the {what}?\n\nThe branch and its files go back to how they were before it began, and any conflicts settled are lost.",
            $"Abort {what}",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

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

        var onDisk = GitChanges.OnDisk(discardingIn, discarding);
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
        MessageBoxes.Show(owner, text, "Discard changes", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

    // Whether git can stash, or restore a stash, now: not part way through a merge or rebase,
    // nor with files in conflict, which git refuses to stash or restore over, nor while git
    // can't read the repository, or is stashing already.
    bool CanStash =>
        repo != null &&
        !stashing &&
        failure == null &&
        !changes.Merging &&
        !changes.Conflicted;

    /// <summary>
    /// What a stash's right-click menu offers: Restore, as GitHub Desktop's does, and Discard,
    /// which asks first.
    /// </summary>
    internal List<QuickMenu.Command?> StashCommands(GitStashes.Stash stash) =>
    [
        new("Restore", () => RestoreStash(stash), CanStash)
        {
            Glyph = Icons.Stash
        },
        new("Discard…", () => DiscardStash(stash), repo != null && !stashing)
        {
            Glyph = Icons.Delete
        }
    ];

    // Stash all changes, as GitHub Desktop's: every change, new files included, put aside with
    // the message asked for, the summary typed suggested, and the files left as the last commit
    // has them.
    void StashAll()
    {
        if (repo is not { } stashingIn ||
            !CanStash ||
            changes.Files.Count == 0)
        {
            return;
        }

        var question = $"Stash all changes in {Path.GetFileName(stashingIn.TrimEnd('\\'))}";
        var newFiles = changes.Files.Count(_ => _.Kind == GitChange.Untracked);
        if (newFiles > 0)
        {
            question += $", {GitState.CountOf(newFiles, "new file")} among them";
        }

        question += ", putting them aside and the files back as the last commit has them. Restore them from Stashed changes, under the files.";
        if (AskStashMessage(this, question, summaryFrame.Box.Text.Trim()) is not { Length: > 0 } message)
        {
            return;
        }

        Stashing = RunStash("Git stash", (stashRepo, cancel) => GitStashes.StashAll(stashRepo, message, cancel));
    }

    // Its changes put back, and the stash dropped, or, where they couldn't all be, kept, the
    // clashes left as files in conflict to settle here.
    void RestoreStash(GitStashes.Stash stash)
    {
        if (!CanStash)
        {
            return;
        }

        Stashing = RunStash("Git restore stash", (stashRepo, cancel) => GitStashes.Restore(stashRepo, stash, cancel));
    }

    // Dropped, having asked, as it can't be got back.
    void DiscardStash(GitStashes.Stash stash)
    {
        if (repo == null ||
            stashing ||
            AskDiscardStash(this, $"Discard the stash {stash.Text}?\n\nIts changes are gone for good: this can't be undone.") != DialogResult.OK)
        {
            return;
        }

        Stashing = RunStash("Git discard stash", (stashRepo, cancel) => GitStashes.Discard(stashRepo, stash, cancel));
    }

    // In the Actions panel, then the files and the stashes read again, and the window told, for
    // the status bar's count of changes.
    async Task RunStash(string title, Func<string, Cancel, CodeCommands.RepoResult> work)
    {
        if (repo is not { } stashingIn)
        {
            return;
        }

        stashing = true;
        try
        {
            await ActionLog.Run(
                title,
                [stashingIn],
                async (_, cancel) =>
                {
                    var result = await Task.Run(() => work(stashingIn, cancel), cancel);
                    GitCache.MarkStale([stashingIn]);
                    return CodeCommands.GitOutcome([result]);
                });
        }
        finally
        {
            stashing = false;
            if (!IsDisposed)
            {
                Reload();
                Committed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Asks for the message to stash every change with, the question and the message suggested
    /// given: null when cancelled. Tests answer for the user.
    /// </summary>
    internal static Func<Control, string, string, string?> AskStashMessage { get; set; } = (owner, question, suggested) =>
    {
        using var form = new CodeMenu.GitTextForm("Git stash", question, "Message:", suggested, "Stash", []);
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        return form.Value;
    };

    /// <summary>
    /// Asks whether to discard a stash. Tests answer for the user.
    /// </summary>
    internal static Func<Control, string, DialogResult> AskDiscardStash { get; set; } = (owner, text) =>
        MessageBoxes.Show(owner, text, "Discard stash", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

    static string FullPath(string repo, string path) =>
        Path.Combine(repo, path.Replace('/', '\\'));

    // Double-clicked, or Enter: in the diff window, where that is where changes show, otherwise in
    // the text editor or git's diff tool, as ever.
    void Activate(int row)
    {
        if (row < 0 ||
            row >= shown.Count)
        {
            return;
        }

        if (settings.DiffPlacement == DiffPlacement.Window)
        {
            ShowChanges(shown[row]);
            return;
        }

        OpenDiff(shown[row]);
    }

    void OpenDiff(Change change)
    {
        if (repo != null)
        {
            GitDiff.OfWorkingFile(this, settings, repo, change);
        }
    }

    // Show changes: where Options says changes show.
    void ShowChanges(Change change)
    {
        if (settings.DiffPlacement == DiffPlacement.Editor)
        {
            OpenDiff(change);
            return;
        }

        if (repo == null)
        {
            return;
        }

        lastRequest = null;
        DiffRequested?.Invoke(new(repo, SourceOf(change), shown, null, 1, Open: true));
    }

    DiffSource SourceOf(Change change) =>
        new(repo!, change, null, changes.HasCommits, changes.Merging);

    DiffRequest? lastRequest;
    bool pickedPending;

    // What is picked changed: told once the list has finished changing it, as reading the files
    // again picks each again, one by one.
    void PickedChanged()
    {
        if (pickedPending ||
            !IsHandleCreated)
        {
            return;
        }

        pickedPending = true;
        BeginInvoke(
            () =>
            {
                pickedPending = false;
                if (!IsDisposed)
                {
                    RaisePicked();
                }
            });
    }

    /// <summary>
    /// Tells what is picked now, unless it is what was told last.
    /// </summary>
    void RaisePicked()
    {
        var rows = list.Selected()
            .Where(_ => _ < shown.Count)
            .ToList();
        DiffSource? source = null;
        if (repo != null &&
            rows.Count == 1)
        {
            source = SourceOf(shown[rows[0]]);
        }

        var request = new DiffRequest(repo, source, shown, null, rows.Count, Open: false);
        if (request.Same(lastRequest))
        {
            return;
        }

        lastRequest = request;
        DiffRequested?.Invoke(request);
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

    /// <summary>
    /// What the files' right-click menu offers for <paramref name="selected"/>.
    /// </summary>
    internal List<QuickMenu.Command?> MenuFor(string menuRepo, IReadOnlyList<Change> selected)
    {
        var commands = ConflictCommands(selected);
        commands.Add(
            new("Discard changes…", () => Discard(selected))
            {
                Glyph = Icons.Discard
            });
        commands.Add(
            new("Stash all changes…", StashAll, CanStash)
            {
                Glyph = Icons.Stash
            });
        if (selected is [var one])
        {
            var path = FullPath(menuRepo, one.Path);
            var exists = File.Exists(path);
            commands.Add(null);
            var showChanges = new QuickMenu.Command("Show changes", () => ShowChanges(one));
            if (settings.DiffPlacement is DiffPlacement.Window or DiffPlacement.Editor)
            {
                showChanges = showChanges with
                {
                    Shortcut = "Enter"
                };
            }

            commands.Add(showChanges);
            if (settings.DiffPlacement != DiffPlacement.Editor)
            {
                var inEditor = new QuickMenu.Command("Show changes in editor", () => GitDiff.OfWorkingFile(this, settings, menuRepo, one));
                if (settings.DiffPlacement != DiffPlacement.Window)
                {
                    inEditor = inEditor with
                    {
                        Shortcut = "Enter"
                    };
                }

                commands.Add(inEditor);
            }
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
        Ticks.Draw(graphics, this, box, TickOf(change), !changes.Merging);
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
            CommitDrafts.PicksChanged -= OnPicksChanged;
            DiffView.LinesDiscarded -= OnLinesDiscarded;
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
            if (pane.shown.Count > 0 &&
                pane.failure == null)
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
