/// <summary>
/// A window's Changes and History panes, for the repository its tab is in, and the strip down
/// its right edge with a tab for each that isn't pinned. Hovering a tab slides its pane out over
/// the list, and over a pane pinned beside it; it slides back once the pointer has left it and
/// the focus isn't in it. A pinned pane stays beside the list, the list narrower for it: both
/// pinned share the column, Changes above History. Which are pinned is kept for each repository.
/// </summary>
sealed class GitPanes : IDisposable
{
    // Pixels at 100% scale.
    const int defaultWidth = 380;
    const int leastWidth = 240;
    const int leastList = 200;

    Form window;
    Settings settings;
    ChangesPane changes;
    HistoryPane history;
    PaneSplitContainer paneSplit = new();
    PaneSplitContainer columnSplit = new();
    Control? host;

    string? repo;
    GitState? state;
    bool changesPinned;
    bool historyPinned;
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
    /// A pane was pinned or unpinned for a repository, for other windows in it to follow.
    /// </summary>
    public event Action<string>? PinnedChanged;

    public EdgeStrip Strip { get; } = new()
    {
        Visible = false
    };

    internal ChangesPane ChangesPane => changes;
    internal HistoryPane HistoryPane => history;
    internal PaneSplitContainer PaneSplit => paneSplit;
    internal GitPane? Floating => floating;

    /// <summary>
    /// The repository the panes are about, null outside one.
    /// </summary>
    public string? Repo => repo;

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

        changes.Header.PinClicked += () => SetPinned(GitPane.Changes, !changesPinned);
        history.Header.PinClicked += () => SetPinned(GitPane.History, !historyPinned);
        changes.EscapePressed += () => Escape(GitPane.Changes);
        history.EscapePressed += () => Escape(GitPane.History);
        changes.ShowInFolderRequested += _ => ShowInFolderRequested?.Invoke(_);
        history.ShowInFolderRequested += _ => ShowInFolderRequested?.Invoke(_);
        history.OpenSiteRequested += _ => OpenSiteRequested?.Invoke(_);

        // A commit is in History straight away, not once the watch hears of it.
        changes.Committed += history.Reload;

        Strip.TabEntered += OnTabEntered;
        Strip.TabLeft += OnTabLeft;
        Strip.TabClicked += pane => Float(pane, focus: true);
        hoverTimer.Tick += (_, _) => OnHovered();
        retractTimer.Tick += (_, _) => CheckRetract();
        slideTimer.Tick += (_, _) => Slide();

        paneSplit.Dock = DockStyle.Fill;
        paneSplit.FixedPanel = FixedPanel.Panel2;
        paneSplit.Panel2Collapsed = true;
        paneSplit.Dragged += KeepWidth;
        paneSplit.DpiChangedAfterParent += (_, _) => Arrange();
        columnSplit.Dock = DockStyle.Fill;
        columnSplit.Orientation = Orientation.Horizontal;
        columnSplit.Dragged += KeepSplit;
        paneSplit.Panel2.Controls.Add(columnSplit);

        GitCache.Stale += OnStale;
        Avatars.Arrived += history.AvatarsArrived;
    }

    /// <summary>
    /// Puts <paramref name="tabs"/> in <paramref name="host"/>, with the column the panes pin to
    /// beside them. A pane slides out over all of <paramref name="host"/>, which ends at the strip.
    /// </summary>
    public void Attach(Control host, Control tabs)
    {
        this.host = host;
        paneSplit.Panel1.Controls.Add(tabs);
        host.Controls.Add(paneSplit);
        host.Controls.Add(changes);
        host.Controls.Add(history);
    }

    Control ControlOf(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return changes;
        }

        return history;
    }

    public bool IsPinned(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return changesPinned;
        }

        return historyPinned;
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
    /// many files have changed. A new repository brings its own pinned panes. Called at every
    /// change of selection, so does nothing when nothing has changed.
    /// </summary>
    public void ShowRepo(string? repo, GitState? state)
    {
        if (!ChangesPane.SameRepo(this.repo, repo))
        {
            Retract(animate: false);
            this.repo = repo;
            this.state = state;
            PinnedPanes? pinned = null;
            if (repo != null)
            {
                pinned = settings.PinnedPanesOf(repo);
            }

            changesPinned = pinned?.Changes == true;
            historyPinned = pinned?.History == true;
            changes.Bind(repo);
            history.Bind(repo);
            Strip.Changes = state?.Changes ?? 0;
            Arrange();
            return;
        }

        if (Equals(this.state, state))
        {
            return;
        }

        var old = this.state;
        this.state = state;
        Strip.Changes = state?.Changes ?? 0;

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
    /// Ctrl+Shift+G or Ctrl+Shift+H: a pinned pane takes the focus, or gives it back to the list;
    /// one slid out slides back; otherwise it slides out with the focus.
    /// </summary>
    public void Toggle(GitPane pane)
    {
        if (repo == null)
        {
            return;
        }

        var control = ControlOf(pane);
        if (IsPinned(pane))
        {
            if (control.ContainsFocus)
            {
                FocusList();
                return;
            }

            FocusContent(pane);
            return;
        }

        if (floating == pane)
        {
            Retract(animate: true);
            return;
        }

        Float(pane, focus: true);
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
    /// Pins a pane beside the list for the repository, or unpins it, noting which are pinned for
    /// it. One unpinned under the pointer stays slid out until the pointer leaves it.
    /// </summary>
    public void SetPinned(GitPane pane, bool pinned)
    {
        if (repo is not { } pinning ||
            IsPinned(pane) == pinned)
        {
            return;
        }

        if (pinned &&
            floating == pane)
        {
            StopSlide();
            floating = null;
            Strip.Open = null;
            retractTimer.Stop();
        }

        var focused = ControlOf(pane).ContainsFocus;
        if (pane == GitPane.Changes)
        {
            changesPinned = pinned;
        }
        else
        {
            historyPinned = pinned;
        }

        settings.SetPinnedPanes(pinning, changesPinned, historyPinned);
        settings.Save();
        Arrange();
        if (!pinned &&
            host != null &&
            host.RectangleToScreen(host.ClientRectangle).Contains(Cursor.Position))
        {
            Float(pane, focus: focused, animate: false);
        }

        PinnedChanged?.Invoke(pinning);
    }

    /// <summary>
    /// Takes up the panes pinned for <paramref name="repo"/> again, pinned or unpinned in another
    /// window.
    /// </summary>
    public void ApplyPinned(string repo)
    {
        if (!ChangesPane.SameRepo(this.repo, repo))
        {
            return;
        }

        var pinned = settings.PinnedPanesOf(repo);
        var changesNow = pinned?.Changes == true;
        var historyNow = pinned?.History == true;
        if (changesNow == changesPinned &&
            historyNow == historyPinned)
        {
            return;
        }

        Retract(animate: false);
        changesPinned = changesNow;
        historyPinned = historyNow;
        Arrange();
    }

    /// <summary>
    /// Puts each pane where it belongs: in the column when pinned, hidden over the list when not,
    /// and shows the strip's tabs for those not pinned.
    /// </summary>
    internal void Arrange()
    {
        if (host == null)
        {
            return;
        }

        var inRepo = repo != null;
        var pinChanges = inRepo && changesPinned;
        var pinHistory = inRepo && historyPinned;
        Place(changes, GitPane.Changes, pinChanges, columnSplit.Panel1);
        Place(history, GitPane.History, pinHistory, columnSplit.Panel2);
        changes.Header.Pinned = pinChanges;
        history.Header.Pinned = pinHistory;

        if (pinChanges ||
            pinHistory)
        {
            // Never both collapsed: SplitContainer uncollapses the other.
            if (pinChanges)
            {
                columnSplit.Panel1Collapsed = false;
                columnSplit.Panel2Collapsed = !pinHistory;
            }
            else
            {
                columnSplit.Panel2Collapsed = false;
                columnSplit.Panel1Collapsed = true;
            }

            paneSplit.Panel2Collapsed = false;
            SetLeastSizes();
            paneSplit.SetPanel2Size(Width());
            if (pinChanges &&
                pinHistory)
            {
                PlaceColumnSplit();
            }
        }
        else
        {
            paneSplit.Panel2Collapsed = true;
        }

        Strip.ShowTabs(!pinChanges, !pinHistory);
        Strip.Visible = inRepo && !(pinChanges && pinHistory);
        changes.Showing = pinChanges || (inRepo && floating == GitPane.Changes);
        history.Showing = pinHistory || (inRepo && floating == GitPane.History);
    }

    // In the column, filling its half, or over the list, hidden until slid out.
    void Place(Control control, GitPane pane, bool pinned, Control column)
    {
        if (pinned)
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
    void SetLeastSizes()
    {
        var list = paneSplit.LogicalToDeviceUnits(leastList);
        var pane = paneSplit.LogicalToDeviceUnits(leastWidth);
        if (paneSplit.Width <= list + pane + paneSplit.SplitterWidth)
        {
            return;
        }

        paneSplit.Panel1MinSize = list;
        paneSplit.Panel2MinSize = pane;
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

    void KeepWidth() =>
        settings.PaneWidth = (int) Math.Round(paneSplit.Panel2.Width * 96.0 / paneSplit.DeviceDpi);

    void PlaceColumnSplit()
    {
        var room = columnSplit.Height - columnSplit.SplitterWidth;
        if (room <= 0)
        {
            return;
        }

        // Changes more than half, as it has the commit's summary and description too.
        var share = 0.6;
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
        var room = columnSplit.Height - columnSplit.SplitterWidth;
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
    /// Slides a pane that isn't pinned out over the list, from the window's edge, the one slid
    /// out before going back at once. With <paramref name="focus"/>, the focus goes into it, and
    /// it stays out until the focus leaves it.
    /// </summary>
    internal void Float(GitPane pane, bool focus, bool animate = true)
    {
        if (repo == null ||
            host == null)
        {
            return;
        }

        if (IsPinned(pane))
        {
            if (focus)
            {
                FocusContent(pane);
            }

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

    // Where a slide was going is forgotten: the pane is pinned meanwhile.
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

        return $"{Path.GetFileName(repo)}: Changes {Where(GitPane.Changes)}, History {Where(GitPane.History)}, strip {StripText()}";
    }

    string Where(GitPane pane)
    {
        if (IsPinned(pane))
        {
            return "pinned";
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
        Avatars.Arrived -= history.AvatarsArrived;
        hoverTimer.Dispose();
        retractTimer.Dispose();
        slideTimer.Dispose();

        // Whichever isn't in the window's controls just now, slid away, is disposed here.
        changes.Dispose();
        history.Dispose();
    }
}
