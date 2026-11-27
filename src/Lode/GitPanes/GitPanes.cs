/// <summary>
/// A window's Changes and History panes, for the repository its tab is in, placed for the room
/// there is beside the tree. With room for both beside the list, they are side by side, Changes
/// then History. With room for one, they share it as tabs along its top, the one showing kept for
/// each repository. With room for neither, a strip down the window's right edge has a tab for
/// each: hovering one slides its pane out over the list, and it slides back once the pointer has
/// left it and the focus isn't in it.
/// </summary>
sealed class GitPanes : IDisposable
{
    // Pixels at 100% scale.
    const int defaultWidth = 380;
    const int leastWidth = 240;
    const int leastList = 200;

    // How wide the list stays with the panes beside it, for there to be room for them there.
    const int roomyList = 500;

    Form window;
    Settings settings;
    ChangesPane changes;
    HistoryPane history;
    PaneSplitContainer paneSplit = new();
    PaneSplitContainer columnSplit = new();
    Control? host;

    string? repo;
    GitState? state;
    PaneLayout layout;

    // The pane showing where the two share the room beside the list.
    GitPane tab;
    bool stripShown;
    bool arranging;
    GitPane? floating;
    GitPane? hovering;
    bool disposed;

    Timer hoverTimer = new();
    Timer retractTimer = new()
    {
        Interval = 100
    };

    // Since when the pane has been ready to slide back, as a Stopwatch timestamp. 0 while not.
    long retractingSince;

    // The slide under way.
    Timer slideTimer = new()
    {
        Interval = 15
    };

    Control? sliding;
    int slideFrom;
    int slideTo;
    long slideStarted;
    Action? slideDone;
    SynchronizationContext? madeOn;

    /// <summary>
    /// How long a pane takes to slide out or back. Tests make it nothing.
    /// </summary>
    internal static TimeSpan SlideDuration { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the pointer is away from a pane before it slides back.
    /// </summary>
    internal static TimeSpan RetractDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Gives the focus back to the list, as a pane slides away with it.
    /// </summary>
    public Action FocusList { get; set; } = () => { };

    public event Action<string>? ShowInFolderRequested;
    public event Action<string>? OpenSiteRequested;

    /// <summary>
    /// A file picked in either pane, or asked to be shown: for the diff to show it.
    /// </summary>
    public event Action<DiffRequest>? DiffRequested;

    /// <summary>
    /// What is uncommitted in a repository was read again, for its diff to be read again too.
    /// </summary>
    public event Action<string>? ChangesRead;

    /// <summary>
    /// How the strip's tabs are labelled was changed, for every window to follow.
    /// </summary>
    public event Action? StripLabelsChanged;

    public EdgeStrip Strip { get; } = new()
    {
        Visible = false
    };

    internal ChangesPane ChangesPane => changes;
    internal HistoryPane HistoryPane => history;
    internal PaneSplitContainer PaneSplit => paneSplit;
    internal GitPane? Floating => floating;

    /// <summary>
    /// Where the panes are, for the room beside the tree.
    /// </summary>
    internal PaneLayout Layout => layout;

    /// <summary>
    /// The repository the panes are about, null outside one.
    /// </summary>
    public string? Repo => repo;

    /// <summary>
    /// The repository's state as last read, which the strip says how many files have changed of.
    /// </summary>
    public GitState? State => state;

    public GitPanes(Form window, Settings settings)
    {
        this.window = window;
        this.settings = settings;
        madeOn = SynchronizationContext.Current;
        changes = new(settings)
        {
            Visible = false
        };
        history = new(settings)
        {
            Visible = false
        };

        changes.Header.TabClicked += ChooseTab;
        history.Header.TabClicked += ChooseTab;
        changes.Enter += (_, _) => OnEntered(GitPane.Changes);
        history.Enter += (_, _) => OnEntered(GitPane.History);
        changes.EscapePressed += () => Escape(GitPane.Changes);
        history.EscapePressed += () => Escape(GitPane.History);
        changes.ShowInFolderRequested += _ => ShowInFolderRequested?.Invoke(_);
        history.ShowInFolderRequested += _ => ShowInFolderRequested?.Invoke(_);
        history.OpenSiteRequested += _ => OpenSiteRequested?.Invoke(_);
        history.PushRequested += () => Act(GitState.PartKind.Ahead);
        history.AmendRequested += (amendRepo, commit, message) => TakeMessage(amendRepo, message, commit.Sha);
        history.CommitUndone += (undoRepo, message) => TakeMessage(undoRepo, message, null);
        changes.ActRequested += Act;

        // A commit is in History straight away, not once the watch hears of it.
        changes.Committed += history.Reload;
        changes.DiffRequested += _ => DiffRequested?.Invoke(_);
        history.DiffRequested += _ => DiffRequested?.Invoke(_);
        changes.ChangesRead += _ => ChangesRead?.Invoke(_);

        Strip.TabEntered += OnTabEntered;
        Strip.TabLeft += OnTabLeft;
        Strip.TabClicked += pane => Float(pane, focus: true);
        Strip.Labels = settings.StripLabels;
        Strip.LabelsChosen += SetStripLabels;
        hoverTimer.Tick += (_, _) => OnHovered();
        retractTimer.Tick += (_, _) => CheckRetract();
        slideTimer.Tick += (_, _) => Slide();

        // Plain lines between the list and the panes, and between the panes, rather than the
        // faint one between the tree and the list: the panes are white as the list is.
        paneSplit.Line = SplitterLine.Plain;
        columnSplit.Line = SplitterLine.Plain;
        paneSplit.Dock = DockStyle.Fill;
        paneSplit.FixedPanel = FixedPanel.Panel2;
        paneSplit.Panel2Collapsed = true;
        paneSplit.Dragged += KeepWidth;
        paneSplit.DpiChangedAfterParent += (_, _) => Arrange();

        // Changes and History side by side, where there is room for both.
        columnSplit.Dock = DockStyle.Fill;
        columnSplit.Orientation = Orientation.Vertical;
        columnSplit.Dragged += KeepSplit;
        paneSplit.Panel2.Controls.Add(columnSplit);

        GitCache.Stale += OnStale;
    }

    /// <summary>
    /// Puts <paramref name="tabs"/> in <paramref name="host"/>, with the column the panes go in
    /// beside them. A pane slides out over all of <paramref name="host"/>, which ends at the strip.
    /// The panes are placed again as <paramref name="host"/> is made wider or narrower.
    /// </summary>
    public void Attach(Control host, Control tabs)
    {
        this.host = host;
        paneSplit.Panel1.Controls.Add(tabs);
        host.Controls.Add(paneSplit);
        host.Controls.Add(changes);
        host.Controls.Add(history);
        host.Resize += (_, _) => OnHostResized();
    }

    Control ControlOf(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return changes;
        }

        return history;
    }

    /// <summary>
    /// Whether a pane is beside the list: both where there is room for both, the tab chosen where
    /// there is room for one.
    /// </summary>
    internal bool IsBeside(GitPane pane)
    {
        if (repo == null)
        {
            return false;
        }

        if (layout == PaneLayout.Wide)
        {
            return true;
        }

        return layout == PaneLayout.Medium &&
               tab == pane;
    }

    bool MenuOpen(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return changes.MenuOpen;
        }

        return history.MenuOpen;
    }

    /// <summary>
    /// The repository the tab showing is in, and its state as last read, the strip saying how
    /// many files have changed. A new repository brings the tab chosen in it. Called at every
    /// change of selection, so does nothing when nothing has changed.
    /// </summary>
    public void ShowRepo(string? repo, GitState? state)
    {
        if (!ChangesPane.SameRepo(this.repo, repo))
        {
            Retract(animate: false);
            this.repo = repo;
            this.state = state;
            tab = GitPane.Changes;
            if (repo != null)
            {
                tab = settings.PaneTabOf(repo);
            }

            changes.Bind(repo);
            history.Bind(repo);
            ShowState(state);
            Arrange();
            return;
        }

        if (Equals(this.state, state))
        {
            return;
        }

        var old = this.state;
        this.state = state;
        ShowState(state);

        // Watched, the watch says when the repository changes, for each pane to read it again.
        // Otherwise a state read again that differs, after F5 say, is what says so.
        if (old == null ||
            state == null ||
            GitCache.IsWatching(settings.CodeDirectory))
        {
            return;
        }

        changes.Reload();
        if (old.Commit != state.Commit ||
            old.Branch != state.Branch ||
            old.Ahead != state.Ahead ||
            old.Behind != state.Behind)
        {
            history.Reload();
        }
    }

    // How many files have changed, on the strip's Changes tab and on History's, where it has one,
    // Changes' own saying how many it read; and, for Changes with nothing uncommitted, what to do
    // next.
    void ShowState(GitState? state)
    {
        var count = state?.Changes ?? 0;
        Strip.Changes = count;
        history.Header.ChangesCount = count;
        changes.ShowState(state);
    }

    // History's arrow clicked, or what to do next in Changes with nothing uncommitted: asks what
    // to do about that part of the state, as the status bar's part does, pull first say where
    // the branch has diverged. With the state read again, which the panes may not have, and the
    // arrows may be newer than.
    async void Act(GitState.PartKind part)
    {
        if (repo is not { } asked)
        {
            return;
        }

        var read = await Task.Run(() => FolderView.ReadGit(asked, Cancel.None));
        if (disposed ||
            read == null ||
            !ChangesPane.SameRepo(repo, asked))
        {
            return;
        }

        GitStatusActions.Act(window, part, asked, read, settings.CodeDirectory);
    }

    // Amend commit, or a commit undone, carries on in Changes, the commit's message in the summary
    // and description: to amend the commit, the focus in the summary, or to commit it again.
    void TakeMessage(string messageRepo, string message, string? amending)
    {
        changes.TakeMessage(messageRepo, message, amending);
        if (!ChangesPane.SameRepo(repo, messageRepo))
        {
            return;
        }

        Show(GitPane.Changes, focus: true);
        if (amending != null)
        {
            changes.FocusSummary();
        }
    }

    /// <summary>
    /// Reads both panes again, for Refresh.
    /// </summary>
    public void Reload()
    {
        changes.Reload();
        history.Reload();
    }

    // A repository changed, heard on a background thread.
    void OnStale(IReadOnlyCollection<string> stale)
    {
        var current = repo;
        if (current == null ||
            !stale.Any(_ => Locations.Same(_, current)))
        {
            return;
        }

        madeOn?.Post(
            _ =>
            {
                if (disposed ||
                    !ChangesPane.SameRepo(repo, current))
                {
                    return;
                }

                Reload();
            },
            null);
    }

    /// <summary>
    /// Compare to branch: History lists how the branch checked out and <paramref name="branch"/>
    /// differ, and shows, with the focus. False where <paramref name="compareRepo"/> isn't the
    /// repository the panes are about, which History can't show.
    /// </summary>
    public bool Compare(string compareRepo, string branch)
    {
        if (!ChangesPane.SameRepo(repo, compareRepo))
        {
            return false;
        }

        history.Compare(branch);
        Show(GitPane.History, focus: true);
        return true;
    }

    /// <summary>
    /// Ctrl+Shift+G or Ctrl+Shift+H: a pane beside the list with the focus gives it back to the
    /// list; one beside it without, or a tab not chosen, takes it; one slid out slides back;
    /// otherwise it slides out with the focus.
    /// </summary>
    public void Toggle(GitPane pane)
    {
        if (repo == null)
        {
            return;
        }

        if (layout != PaneLayout.Narrow)
        {
            if (IsBeside(pane) &&
                ControlOf(pane).ContainsFocus)
            {
                FocusList();
                return;
            }

            Show(pane, focus: true);
            return;
        }

        if (floating == pane)
        {
            Retract(animate: true);
            return;
        }

        Float(pane, focus: true);
    }

    /// <summary>
    /// Shows a pane: slid out over the list where there is no room for it beside it, its tab
    /// chosen where it shares the room with the other. With <paramref name="focus"/>, the focus
    /// goes into it.
    /// </summary>
    public void Show(GitPane pane, bool focus)
    {
        if (repo == null ||
            host == null)
        {
            return;
        }

        if (layout == PaneLayout.Narrow)
        {
            Float(pane, focus);
            return;
        }

        if (!IsBeside(pane))
        {
            Choose(pane, focus);
            return;
        }

        if (focus)
        {
            FocusContent(pane);
        }
    }

    /// <summary>
    /// A tab clicked along a pane's top: its pane shows in place of the other, noted for the
    /// repository, with the focus if the other had it.
    /// </summary>
    internal void ChooseTab(GitPane pane) =>
        Choose(pane, focus: false);

    void Choose(GitPane pane, bool focus)
    {
        if (repo == null ||
            layout != PaneLayout.Medium ||
            tab == pane)
        {
            return;
        }

        var focused = changes.ContainsFocus || history.ContainsFocus;
        Note(pane);
        Arrange();
        if (focus ||
            focused)
        {
            FocusContent(pane);
        }
    }

    // The focus went into a pane beside the list: the one to show for the repository where only
    // one has room, so the one in use stays as the window narrows.
    void OnEntered(GitPane pane)
    {
        if (IsBeside(pane))
        {
            Note(pane);
        }
    }

    // The pane to show for the repository, kept in the settings.
    void Note(GitPane pane)
    {
        tab = pane;
        if (repo is not { } noting ||
            settings.PaneTabOf(noting) == pane)
        {
            return;
        }

        settings.SetPaneTab(noting, pane);
        settings.Save();
    }

    void FocusContent(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            changes.FocusContent();
            return;
        }

        history.FocusContent();
    }

    void Escape(GitPane pane)
    {
        if (floating == pane)
        {
            Retract(animate: true);
            return;
        }

        FocusList();
    }

    /// <summary>
    /// Labels the strip's tabs as chosen from its right-click menu, in every window.
    /// </summary>
    internal void SetStripLabels(StripLabels labels)
    {
        if (settings.StripLabels == labels)
        {
            return;
        }

        settings.StripLabels = labels;
        settings.Save();
        ApplyStripLabels();
        StripLabelsChanged?.Invoke();
    }

    /// <summary>
    /// Takes up how the strip's tabs are labelled, changed in this window or another.
    /// </summary>
    public void ApplyStripLabels() =>
        Strip.Labels = settings.StripLabels;

    /// <summary>
    /// Where the panes go for <paramref name="room"/>, the width beside the tree, the strip's
    /// included: beside the list where it stays <paramref name="list"/> wide with both there, or
    /// with one, each <paramref name="pane"/> wide; otherwise over it.
    /// </summary>
    internal static PaneLayout LayoutFor(int room, int pane, int list, int splitter)
    {
        if (room >= list + (pane + splitter) * 2)
        {
            return PaneLayout.Wide;
        }

        if (room >= list + pane + splitter)
        {
            return PaneLayout.Medium;
        }

        return PaneLayout.Narrow;
    }

    // From the panes' width to start at, not the one dragged to, so a pane dragged wider doesn't
    // take itself away under the pointer. The strip's width counted too, so its coming and going
    // doesn't change where they go.
    PaneLayout LayoutNow()
    {
        var room = host!.Width;
        if (stripShown)
        {
            room += Strip.Width;
        }

        return LayoutFor(room, host.LogicalToDeviceUnits(defaultWidth), host.LogicalToDeviceUnits(roomyList), paneSplit.SplitterWidth);
    }

    // Not minimized, which leaves the host no room, so the panes are where they were once the
    // window is back.
    bool Measurable() =>
        host is { Width: > 0 } &&
        window.WindowState != FormWindowState.Minimized;

    void OnHostResized()
    {
        if (arranging ||
            repo == null ||
            !Measurable() ||
            LayoutNow() == layout)
        {
            return;
        }

        Arrange();
    }

    // Where the panes go now differs: one slid out stays where it is, beside the list; the focus
    // in one that goes from beside the list goes back to the list.
    void ChangeLayout(PaneLayout now)
    {
        var was = layout;
        layout = now;
        if (now != PaneLayout.Narrow &&
            floating is { } pane)
        {
            StopSlide();
            floating = null;
            Strip.Open = null;
            retractTimer.Stop();
            tab = pane;
        }

        if (now == PaneLayout.Narrow &&
            was != PaneLayout.Narrow &&
            (changes.ContainsFocus || history.ContainsFocus))
        {
            FocusList();
        }
    }

    /// <summary>
    /// Puts each pane where it belongs, for the room beside the tree: beside the list, both or
    /// the tab chosen, or hidden over it, and shows the strip where both are hidden.
    /// </summary>
    internal void Arrange()
    {
        if (host == null ||
            arranging)
        {
            return;
        }

        arranging = true;
        try
        {
            if (Measurable())
            {
                var now = LayoutNow();
                if (now != layout)
                {
                    ChangeLayout(now);
                }
            }

            Place();
        }
        finally
        {
            arranging = false;
        }
    }

    void Place()
    {
        var inRepo = repo != null;
        var changesBeside = IsBeside(GitPane.Changes);
        var historyBeside = IsBeside(GitPane.History);
        Place(changes, GitPane.Changes, changesBeside, columnSplit.Panel1);
        Place(history, GitPane.History, historyBeside, columnSplit.Panel2);
        var tabbed = inRepo && layout == PaneLayout.Medium;
        changes.Header.Tabbed = tabbed;
        history.Header.Tabbed = tabbed;

        if (changesBeside ||
            historyBeside)
        {
            // Never both collapsed: SplitContainer uncollapses the other.
            if (changesBeside)
            {
                columnSplit.Panel1Collapsed = false;
                columnSplit.Panel2Collapsed = !historyBeside;
            }
            else
            {
                columnSplit.Panel2Collapsed = false;
                columnSplit.Panel1Collapsed = true;
            }

            paneSplit.Panel2Collapsed = false;
            var both = changesBeside && historyBeside;
            SetLeastSizes(both);
            paneSplit.SetPanel2Size(ColumnWidth(both));
            if (both)
            {
                PlaceColumnSplit();
            }
        }
        else
        {
            paneSplit.Panel2Collapsed = true;
        }

        stripShown = inRepo && layout == PaneLayout.Narrow;
        Strip.Visible = stripShown;
        changes.Showing = changesBeside || (inRepo && floating == GitPane.Changes);
        history.Showing = historyBeside || (inRepo && floating == GitPane.History);
    }

    // In the column, filling its part, or over the list, hidden until slid out.
    void Place(Control control, GitPane pane, bool beside, Control column)
    {
        if (beside)
        {
            if (control.Parent != column)
            {
                column.Controls.Add(control);
            }

            control.Dock = DockStyle.Fill;
            control.Padding = Padding.Empty;
            control.Visible = true;
            return;
        }

        // A line down its left edge, between it and the list it is over.
        control.Padding = new(1, 0, 0, 0);

        if (control.Parent != host)
        {
            host!.Controls.Add(control);
        }

        control.Dock = DockStyle.None;
        control.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
        if (floating != pane)
        {
            control.Visible = false;
        }
    }

    // SplitContainer throws for least sizes that don't fit the room it has.
    void SetLeastSizes(bool both)
    {
        var list = paneSplit.LogicalToDeviceUnits(leastList);
        var panes = paneSplit.LogicalToDeviceUnits(leastWidth);
        if (both)
        {
            panes = panes * 2 + columnSplit.SplitterWidth;
        }

        if (paneSplit.Width <= list + panes + paneSplit.SplitterWidth)
        {
            return;
        }

        paneSplit.Panel1MinSize = list;
        paneSplit.Panel2MinSize = panes;
    }

    // How wide a pane is, from the width kept, at the window's scale.
    int Width()
    {
        var width = defaultWidth;
        if (settings.PaneWidth > 0)
        {
            width = settings.PaneWidth;
        }

        return paneSplit.LogicalToDeviceUnits(width);
    }

    // How wide the column beside the list is: a pane, or two side by side.
    int ColumnWidth(bool both)
    {
        if (both)
        {
            return Width() * 2 + columnSplit.SplitterWidth;
        }

        return Width();
    }

    // The width dragged to kept for each pane, half of it with two side by side.
    void KeepWidth()
    {
        var width = paneSplit.Panel2.Width;
        if (repo != null &&
            layout == PaneLayout.Wide)
        {
            width = (width - columnSplit.SplitterWidth) / 2;
        }

        settings.PaneWidth = (int) Math.Round(width * 96.0 / paneSplit.DeviceDpi);
    }

    void PlaceColumnSplit()
    {
        var room = columnSplit.Width - columnSplit.SplitterWidth;
        if (room <= 0)
        {
            return;
        }

        var share = 0.5;
        if (settings.PaneSplit is > 0 and < 1)
        {
            share = settings.PaneSplit;
        }

        var distance = (int) (room * share);
        if (distance > columnSplit.Panel1MinSize &&
            distance < room - columnSplit.Panel2MinSize)
        {
            columnSplit.SplitterDistance = distance;
        }
    }

    void KeepSplit()
    {
        var room = columnSplit.Width - columnSplit.SplitterWidth;
        if (room > 0)
        {
            settings.PaneSplit = Math.Round((double) columnSplit.SplitterDistance / room, 3);
        }
    }

    void OnTabEntered(GitPane pane)
    {
        if (floating == pane)
        {
            return;
        }

        // A moment on the tab first, so the pointer passing by on its way to the edge of a
        // maximized window, the list's scrollbar say, doesn't slide it out.
        hovering = pane;
        hoverTimer.Stop();
        hoverTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
        hoverTimer.Start();
    }

    void OnTabLeft()
    {
        hovering = null;
        hoverTimer.Stop();
    }

    void OnHovered()
    {
        hoverTimer.Stop();
        if (hovering is { } pane &&
            Strip.ScreenBoundsOf(pane).Contains(Cursor.Position))
        {
            Float(pane, focus: false);
        }
    }

    /// <summary>
    /// Slides a pane out over the list, from the window's edge, the one slid out before going
    /// back at once. With <paramref name="focus"/>, the focus goes into it, and it stays out until
    /// the focus leaves it. Where there is room for it beside the list, it shows there instead.
    /// </summary>
    internal void Float(GitPane pane, bool focus, bool animate = true)
    {
        if (repo == null ||
            host == null)
        {
            return;
        }

        if (layout != PaneLayout.Narrow)
        {
            Show(pane, focus);
            return;
        }

        if (floating is { } other &&
            other != pane)
        {
            Retract(animate: false);
        }

        var control = ControlOf(pane);
        var width = Math.Min(Width(), Math.Max(host.Width - host.LogicalToDeviceUnits(leastList), host.LogicalToDeviceUnits(leastWidth)));
        if (floating != pane ||
            !control.Visible)
        {
            control.SetBounds(host.Width, 0, width, host.Height);
        }
        else
        {
            control.SetBounds(control.Left, 0, width, host.Height);
        }

        floating = pane;
        Strip.Open = pane;
        control.Visible = true;
        control.BringToFront();
        Arrange();
        StartSlide(control, host.Width - width, animate, null);
        retractingSince = 0;
        retractTimer.Start();
        if (focus)
        {
            FocusContent(pane);
        }
    }

    /// <summary>
    /// Slides the pane slid out back to the window's edge, the focus back to the list if it was
    /// in the pane.
    /// </summary>
    internal void Retract(bool animate)
    {
        if (floating is not { } pane ||
            host == null)
        {
            return;
        }

        var control = ControlOf(pane);
        if (control.ContainsFocus)
        {
            FocusList();
        }

        floating = null;
        Strip.Open = null;
        retractTimer.Stop();
        StartSlide(
            control,
            host.Width,
            animate,
            () =>
            {
                if (floating != pane)
                {
                    control.Visible = false;
                    Arrange();
                }
            });
    }

    /// <summary>
    /// Whether a pane slid out goes back: the pointer has left it and its tab, the focus isn't in
    /// it, and nothing of its own is in the way, a menu it opened, a mouse button held down for a
    /// drag, or a window in front of this one, its Discard question say.
    /// </summary>
    internal static bool ShouldRetract(bool pointerOver, bool focusInside, bool windowActive, bool menuOpen, bool buttonDown) =>
        !pointerOver &&
        !focusInside &&
        windowActive &&
        !menuOpen &&
        !buttonDown;

    void CheckRetract()
    {
        if (floating is not { } pane)
        {
            retractTimer.Stop();
            return;
        }

        var control = ControlOf(pane);
        var cursor = Cursor.Position;
        var over = control.RectangleToScreen(control.ClientRectangle).Contains(cursor) ||
                   Strip.ScreenBoundsOf(pane).Contains(cursor);
        if (!ShouldRetract(over, control.ContainsFocus, Form.ActiveForm == window, MenuOpen(pane), Control.MouseButtons != MouseButtons.None))
        {
            retractingSince = 0;
            return;
        }

        if (retractingSince == 0)
        {
            retractingSince = Stopwatch.GetTimestamp();
            return;
        }

        if (Stopwatch.GetElapsedTime(retractingSince) >= RetractDelay)
        {
            Retract(animate: true);
        }
    }

    void StartSlide(Control control, int to, bool animate, Action? done)
    {
        // One slide at a time: the one under way ends where it was going.
        if (sliding != null &&
            sliding != control)
        {
            FinishSlide();
        }

        sliding = control;
        slideFrom = control.Left;
        slideTo = to;
        slideDone = done;
        slideStarted = Stopwatch.GetTimestamp();
        if (!animate ||
            SlideDuration <= TimeSpan.Zero ||
            !SystemInformation.UIEffectsEnabled)
        {
            FinishSlide();
            return;
        }

        slideTimer.Start();
    }

    void Slide()
    {
        if (sliding is not { } control)
        {
            slideTimer.Stop();
            return;
        }

        var progress = Stopwatch.GetElapsedTime(slideStarted) / SlideDuration;
        if (progress >= 1)
        {
            FinishSlide();
            return;
        }

        // Fast at first, slowing to a stop, as Windows' own panes slide.
        var eased = 1 - Math.Pow(1 - progress, 3);
        control.Left = slideFrom + (int) Math.Round((slideTo - slideFrom) * eased);
        control.Update();
    }

    void FinishSlide()
    {
        slideTimer.Stop();
        if (sliding is { } control)
        {
            control.Left = slideTo;
        }

        sliding = null;
        var done = slideDone;
        slideDone = null;
        done?.Invoke();
    }

    // Where a slide was going is forgotten: the pane is beside the list meanwhile.
    void StopSlide()
    {
        slideTimer.Stop();
        sliding = null;
        slideDone = null;
    }

    /// <summary>
    /// Where each pane is, for tests.
    /// </summary>
    internal string Describe()
    {
        if (repo == null)
        {
            return "No repository";
        }

        return $"{Path.GetFileName(repo)}: {layout.ToString().ToLowerInvariant()}, Changes {Where(GitPane.Changes)}, History {Where(GitPane.History)}, strip {StripText()}";
    }

    string Where(GitPane pane)
    {
        if (IsBeside(pane))
        {
            if (layout == PaneLayout.Wide)
            {
                return "beside";
            }

            return "showing";
        }

        if (floating == pane)
        {
            return "slid out";
        }

        return "hidden";
    }

    string StripText()
    {
        if (!Strip.Visible)
        {
            return "hidden";
        }

        return string.Join(" and ", Strip.Tabs().Select(_ => Strip.TextOf(_.Pane)));
    }

    public void Dispose()
    {
        disposed = true;
        GitCache.Stale -= OnStale;
        hoverTimer.Dispose();
        retractTimer.Dispose();
        slideTimer.Dispose();

        // Whichever isn't in the window's controls just now, slid away, is disposed here.
        changes.Dispose();
        history.Dispose();
    }
}
