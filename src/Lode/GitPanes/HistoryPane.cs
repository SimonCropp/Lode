/// <summary>
/// The History pane, as GitHub Desktop's History tab lists commits: the branch's commits, newest
/// first, each with its subject, its author, how long ago, its tags, and an arrow for one not
/// pushed yet. Beneath, the files the commit picked changed, each shown changed on a
/// double-click. A page of commits at a time, the next as the list nears its end.
/// </summary>
sealed class HistoryPane : UserControl
{
    // Pixels at 100% scale.
    const int inset = DesktopStyle.Inset;

    const string notPushed = "Not pushed yet. Click to push";

    PaneHeader header = new(GitPane.History);
    PaneSplitContainer split = new();
    RowList commits;
    RowList files;
    RowTip commitTip;
    RowTip fileTip;
    CommitHeading heading;
    Settings settings;

    // As Desktop sets them: a commit's summary semibold, what is under it 11 pixels, and the
    // picked commit's summary 14 pixels and semibold.
    Font bold;
    Font small;
    Font title;

    string? repo;
    RemoteSite? site;
    List<GitCommit> loaded = [];
    HashSet<string> unpushed = [];
    bool more;
    bool showing;
    bool stale = true;
    bool reading;
    bool readAgain;
    bool readingMore;
    int version;
    int filesVersion;
    GitCommit? selected;

    // The picked commit was clicked again to pick none, so the newest isn't picked in its place.
    bool unpicked;
    List<Change> changed = [];

    // Compare to branch, as GitHub Desktop's: the branch compared with, null when not comparing,
    // whether its commits ahead are listed rather than those behind, and how far apart they are.
    string? compareTo;
    bool showAhead;
    (int Ahead, int Behind)? apart;
    CompareBar compareBar;

    /// <summary>
    /// Reads a page of the commits only one of HEAD and a branch has, comparing. Tests answer
    /// for git.
    /// </summary>
    internal static Func<string, string, int, int, Cancel, List<GitCommit>?> ReadRange { get; set; } = GitHistory.Read;

    /// <summary>
    /// Reads how far HEAD and a branch are apart, comparing. Tests answer for git.
    /// </summary>
    internal static Func<string, string, Cancel, (int Ahead, int Behind)?> ReadApart { get; set; } = GitHistory.Apart;

    /// <summary>
    /// The branch compared with, null when not comparing. For tests.
    /// </summary>
    internal string? ComparedTo => compareTo;

    /// <summary>
    /// Whether the commits ahead of the branch compared with are listed, rather than those
    /// behind. For tests.
    /// </summary>
    internal bool ShowingAhead => showAhead;

    /// <summary>
    /// What the bar comparing says: the branch, and how many commits are behind and ahead. For
    /// tests.
    /// </summary>
    internal string CompareText => compareBar.Description;

    /// <summary>
    /// Reads a page of commits. Tests answer for git.
    /// </summary>
    internal static Func<string, int, int, Cancel, List<GitCommit>?> ReadCommits { get; set; } = GitHistory.Read;

    /// <summary>
    /// Reads the files a commit changed. Tests answer for git.
    /// </summary>
    internal static Func<string, GitCommit, Cancel, List<Change>?> ReadFiles { get; set; } = GitHistory.FilesOf;

    /// <summary>
    /// Reads which commits aren't pushed. Tests answer for git.
    /// </summary>
    internal static Func<string, Cancel, HashSet<string>> ReadUnpushed { get; set; } = GitHistory.Unpushed;

    /// <summary>
    /// How long ago is measured from. Tests put in a clock of their own.
    /// </summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    public event Action<string>? ShowInFolderRequested;
    public event Action<string>? OpenSiteRequested;
    public event Action? EscapePressed;

    /// <summary>
    /// The arrow of a commit not pushed clicked: for the commits ahead to be pushed, once asked.
    /// </summary>
    public event Action? PushRequested;

    /// <summary>
    /// A file of the picked commit picked, or several, or none, or one asked to be shown: for the
    /// diff to show it.
    /// </summary>
    public event Action<DiffRequest>? DiffRequested;

    public PaneHeader Header => header;

    public bool MenuOpen { get; private set; }

    /// <summary>
    /// The commits listed. For tests.
    /// </summary>
    internal IReadOnlyList<GitCommit> Commits => loaded;

    internal RowList CommitList => commits;
    internal RowList FileList => files;

    /// <summary>
    /// The picked commit's heading, above its files. For tests.
    /// </summary>
    internal Control Heading => heading;

    /// <summary>
    /// The reading under way, for tests to wait on.
    /// </summary>
    internal Task Reading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The reading of the picked commit's files, for tests to wait on.
    /// </summary>
    internal Task ReadingFiles { get; private set; } = Task.CompletedTask;

    public HistoryPane(Settings settings)
    {
        this.settings = settings;
        BackColor = ExplorerStyle.Window;
        bold = DesktopStyle.Semibold(Font, Font.SizeInPoints);
        small = new(Font.FontFamily, Math.Max(7f, Font.SizeInPoints * 11 / 12));
        title = DesktopStyle.Semibold(Font, Font.SizeInPoints * 7 / 6);
        commits = new(CommitRowHeight(Font))
        {
            MultiSelect = false,
            AccessibleName = "Commits",
            ShadesHovered = false,
            DeselectsOnClick = true
        };
        commits.Deselected += Unpick;
        commits.DrawRow += DrawCommit;
        commits.SelectedIndexChanged += (_, _) => OnCommitPicked();
        commits.RetrieveVirtualItem += (_, args) => NearEnd(args.ItemIndex);
        commits.MouseUp += OnCommitsMouseUp;
        commits.ButtonAt = ButtonAt;
        commits.GotFocus += (_, _) => commits.Invalidate();
        commits.LostFocus += (_, _) => commits.Invalidate();
        commitTip = new(commits, CommitTipAt);

        files = new(DesktopStyle.RowHeight(Font))
        {
            AccessibleName = "Files changed"
        };
        files.DrawRow += DrawFile;
        files.ItemActivate += (_, _) => Activate(files.FocusedItem?.Index ?? -1);
        files.SelectedIndexChanged += (_, _) => PickedChanged();
        files.VirtualItemsSelectionRangeChanged += (_, _) => PickedChanged();
        files.MouseUp += OnFilesMouseUp;
        files.GotFocus += (_, _) => files.Invalidate();
        files.LostFocus += (_, _) => files.Invalidate();
        fileTip = new(files, _ => ChangeRows.TipAt(files, _, changed));
        heading = new(this);
        compareBar = new(this)
        {
            Visible = false
        };

        split.Dock = DockStyle.Fill;
        // No line of its own: the commit's heading draws one along its top, which always shows.
        split.Line = SplitterLine.None;
        split.Orientation = Orientation.Horizontal;
        split.Panel1.Controls.Add(commits);
        split.Panel2.Controls.Add(files);
        split.Panel2.Controls.Add(heading);
        split.SizeChanged += (_, _) => PlaceSplitter();

        Controls.Add(split);
        Controls.Add(compareBar);
        Controls.Add(header);
    }

    /// <summary>
    /// Compares the branch checked out with <paramref name="branch"/>, as GitHub Desktop's
    /// Compare to branch does: the commits it has that the branch checked out hasn't, behind,
    /// listed first, then those the branch checked out has, ahead, a click away.
    /// </summary>
    public void Compare(string branch)
    {
        compareTo = branch;
        showAhead = false;
        apart = null;
        ResetCommits();
        compareBar.Visible = true;
        compareBar.ShowFor(branch, showAhead, apart);
        Reload();
    }

    /// <summary>
    /// Back to the commits of the branch checked out.
    /// </summary>
    public void EndCompare()
    {
        if (compareTo == null)
        {
            return;
        }

        compareTo = null;
        apart = null;
        compareBar.Visible = false;
        ResetCommits();
        Reload();
    }

    // The commits behind, or those ahead.
    internal void ShowAhead(bool ahead)
    {
        if (compareTo == null ||
            showAhead == ahead)
        {
            return;
        }

        showAhead = ahead;
        ResetCommits();
        compareBar.ShowFor(compareTo, showAhead, apart);
        Reload();
    }

    // The commits of the branch compared with that the branch checked out hasn't, merged into it.
    void MergeCompared()
    {
        if (repo is { } mergeRepo &&
            compareTo is { } branch)
        {
            CodeMenu.MergeBranch(this, mergeRepo, GitMerging.Kind.Merge, branch);
        }
    }

    // The commits listed are another list: none picked, none kept, until they are read.
    void ResetCommits()
    {
        version++;
        filesVersion++;
        loaded = [];
        more = false;
        selected = null;
        unpicked = false;
        changed = [];
        commits.SetRows(0);
        files.SetRows(0);
        PickedChanged();
        heading.Invalidate();
    }

    // What is listed: HEAD's commits, or, comparing, those behind or ahead of the branch.
    static List<GitCommit>? Read(string reading, string? comparing, bool ahead, int skip, int count)
    {
        if (comparing == null)
        {
            return ReadCommits(reading, skip, count, Cancel.None);
        }

        var range = $"HEAD..{comparing}";
        if (ahead)
        {
            range = $"{comparing}..HEAD";
        }

        return ReadRange(reading, range, skip, count, Cancel.None);
    }

    // Desktop's 50 pixels, taller for larger text.
    static int CommitRowHeight(Font font) =>
        Math.Max(DesktopStyle.CommitRowHeight, font.Height * 2 + 18);

    bool splitterPlaced;

    // The commits taking three fifths, the files the rest, once the pane first has the room.
    void PlaceSplitter()
    {
        var room = split.Height - split.SplitterWidth;
        if (splitterPlaced ||
            room < LogicalToDeviceUnits(200))
        {
            return;
        }

        splitterPlaced = true;
        split.SplitterDistance = room * 3 / 5;
    }

    /// <summary>
    /// The repository the pane is about, or null outside one. A new one is read once the pane
    /// shows.
    /// </summary>
    public void Bind(string? repo)
    {
        if (ChangesPane.SameRepo(this.repo, repo))
        {
            return;
        }

        this.repo = repo;
        site = null;

        // Another repository's branches are others.
        compareTo = null;
        apart = null;
        compareBar.Visible = false;
        version++;
        loaded = [];
        unpushed = [];
        more = false;
        selected = null;
        unpicked = false;
        changed = [];
        commits.SetRows(0);
        files.SetRows(0);
        PickedChanged();
        heading.Invalidate();
        stale = true;
        if (showing)
        {
            Reload();
        }
    }

    /// <summary>
    /// Puts the focus in the list of commits, on the newest when none is picked.
    /// </summary>
    public void FocusContent()
    {
        if (loaded.Count > 0 &&
            commits.SelectedIndices.Count == 0)
        {
            commits.SelectedIndices.Add(0);
            commits.FocusedItem = commits.Items[0];
        }

        commits.Focus();
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
    /// Reads the commits again, as many as were listed, once the pane shows: a commit was made,
    /// or the branch switched.
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
        var count = Math.Max(GitHistory.Page, loaded.Count);
        var comparing = compareTo;
        var ahead = showAhead;
        this.reading = true;
        stale = false;
        try
        {
            var (read, notPushed, readSite, readApart) = await Task.Run(() => (
                Read(reading, comparing, ahead, 0, count),
                ReadUnpushed(reading, Cancel.None),
                RemoteSite.OfRepo(reading),
                comparing == null ? null : ReadApart(reading, comparing, Cancel.None)));
            if (asked != version ||
                IsDisposed)
            {
                return;
            }

            if (comparing != null)
            {
                apart = readApart;
                compareBar.ShowFor(comparing, ahead, readApart);
            }

            site = readSite;
            unpushed = notPushed;
            loaded = read ?? [];
            more = loaded.Count >= count;
            ShowCommits();
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

    // The next page, once the list is drawing a row near the end of those read.
    void NearEnd(int index)
    {
        if (!more ||
            readingMore ||
            reading ||
            repo == null ||
            index < loaded.Count - 20)
        {
            return;
        }

        readingMore = true;
        BeginInvoke(() => _ = ReadMore());
    }

    async Task ReadMore()
    {
        var reading = repo!;
        var asked = version;
        var skip = loaded.Count;
        var comparing = compareTo;
        var ahead = showAhead;
        try
        {
            var read = await Task.Run(() => Read(reading, comparing, ahead, skip, GitHistory.Page));
            if (asked != version ||
                IsDisposed ||
                read == null)
            {
                return;
            }

            loaded.AddRange(read);
            more = read.Count >= GitHistory.Page;
            commits.SetRows(loaded.Count);
            commits.Invalidate();
        }
        finally
        {
            readingMore = false;
        }
    }

    // The commits as read, the one picked before picked again, by its sha, where it still is,
    // and otherwise the newest, unless none was picked by clicking the picked one again.
    void ShowCommits()
    {
        var keep = selected?.Sha;
        var top = 0;
        if (commits is
            {
                VirtualListSize: > 0,
                TopItem: { } topItem
            })
        {
            top = topItem.Index;
        }

        commits.BeginUpdate();
        commits.SelectedIndices.Clear();
        commits.SetRows(loaded.Count);
        var index = -1;
        if (keep != null)
        {
            index = loaded.FindIndex(_ => _.Sha == keep);
        }

        if (index < 0 &&
            !unpicked &&
            loaded.Count > 0)
        {
            index = 0;
        }

        commits.EndUpdate();
        if (index >= 0)
        {
            commits.SelectedIndices.Add(index);
            commits.FocusedItem = commits.Items[index];
        }
        else
        {
            selected = null;
            changed = [];
            files.SetRows(0);
            heading.Invalidate();
            PickedChanged();
        }

        if (top > 0 &&
            top < loaded.Count)
        {
            commits.TopItem = commits.Items[top];
        }

        commits.Invalidate();
    }

    void OnCommitPicked()
    {
        if (commits.SelectedIndices.Count == 0)
        {
            return;
        }

        var index = commits.SelectedIndices[0];
        if (index >= loaded.Count)
        {
            return;
        }

        unpicked = false;
        var commit = loaded[index];
        if (selected?.Sha == commit.Sha &&
            files.VirtualListSize == changed.Count &&
            changed.Count > 0)
        {
            return;
        }

        selected = commit;
        heading.Invalidate();
        ReadingFiles = ReadFilesAsync(commit);
    }

    /// <summary>
    /// The commit picked was clicked again, and so no commit is picked: its files are put away,
    /// and any still being read are not shown.
    /// </summary>
    void Unpick()
    {
        filesVersion++;
        selected = null;
        unpicked = true;
        changed = [];
        files.SetRows(0);
        heading.Invalidate();
        PickedChanged();
    }

    async Task ReadFilesAsync(GitCommit commit)
    {
        if (repo is not { } reading)
        {
            return;
        }

        var asked = ++filesVersion;
        var read = await Task.Run(() => ReadFiles(reading, commit, Cancel.None));
        if (asked != filesVersion ||
            IsDisposed)
        {
            return;
        }

        changed = read ?? [];
        files.SelectedIndices.Clear();
        files.SetRows(changed.Count);
        files.Invalidate();

        // How many files it changed, which said none until they were read.
        heading.Invalidate();
        PickedChanged();
    }

    // Double-clicked, or Enter: in the diff window, where that is where changes show, otherwise in
    // the text editor or git's diff tool, as ever.
    void Activate(int row)
    {
        if (row < 0 ||
            row >= changed.Count)
        {
            return;
        }

        if (settings.DiffPlacement == DiffPlacement.Window)
        {
            ShowChanges(changed[row]);
            return;
        }

        OpenDiff(changed[row]);
    }

    void OpenDiff(Change change)
    {
        if (repo != null &&
            selected != null)
        {
            GitDiff.OfCommit(this, settings, repo, selected, change);
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

        if (repo == null ||
            selected == null)
        {
            return;
        }

        lastRequest = null;
        DiffRequested?.Invoke(new(repo, new(repo, change, selected), changed, selected, 1, Open: true));
    }

    DiffRequest? lastRequest;
    bool pickedPending;

    // What is picked changed: told once the list has finished changing it.
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
        var rows = files.Selected()
            .Where(_ => _ < changed.Count)
            .ToList();
        DiffSource? source = null;
        if (repo != null &&
            selected != null &&
            rows.Count == 1)
        {
            source = new(repo, changed[rows[0]], selected);
        }

        var request = new DiffRequest(repo, source, changed, selected, rows.Count, Open: false);
        if (request.Same(lastRequest))
        {
            return;
        }

        lastRequest = request;
        DiffRequested?.Invoke(request);
    }

    void OnCommitsMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right ||
            repo is not { } menuRepo ||
            commits.HitTest(args.Location).Item is not { } item ||
            item.Index >= loaded.Count)
        {
            return;
        }

        commits.SelectedIndices.Clear();
        commits.SelectedIndices.Add(item.Index);
        PaneMenu.Show(commits, args.Location, CommitCommands(menuRepo, loaded[item.Index]), open => MenuOpen = open);
    }

    /// <summary>
    /// A commit's right-click menu: copy its SHA or message; revert it; tag it, or delete one of its tags,
    /// those it has offered; and view it on the repository's site.
    /// </summary>
    internal List<QuickMenu.Command?> CommitCommands(string menuRepo, GitCommit commit)
    {
        var menu = new List<QuickMenu.Command?>
        {
            new("Copy SHA", () => FolderView.SetClipboardText(commit.Sha))
            {
                Glyph = Icons.Copy
            },
            new("Copy message", () => CopyMessage(menuRepo, commit)),
            null,
            new("Revert commit…", () => CodeMenu.RevertCommit(this, menuRepo, commit))
            {
                Glyph = Icons.Undo
            },
            new("Tag…", () => CodeMenu.TagCommit(this, menuRepo, commit))
            {
                Glyph = Icons.Tag
            }
        };
        if (commit.Tags.Count > 0)
        {
            menu.Add(
                new("Delete tag…", () => CodeMenu.DeleteCommitTag(this, menuRepo, commit))
                {
                    Glyph = Icons.Delete
                });
        }

        if (site is { } onSite)
        {
            menu.Add(null);
            menu.Add(
                new($"View on {onSite.Name}", () => OpenSiteRequested?.Invoke(onSite.CommitUrl(commit.Sha)))
                {
                    Glyph = Icons.Links
                });
        }

        return menu;
    }

    static async void CopyMessage(string repo, GitCommit commit)
    {
        var message = await Task.Run(() => GitHistory.Message(repo, commit.Sha));
        FolderView.SetClipboardText(message ?? commit.Subject);
    }

    void OnFilesMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right ||
            repo is not { } menuRepo ||
            files.HitTest(args.Location).Item is not { } item ||
            item.Index >= changed.Count)
        {
            return;
        }

        if (!files.SelectedIndices.Contains(item.Index))
        {
            files.SelectedIndices.Clear();
            files.SelectedIndices.Add(item.Index);
        }

        var change = changed[item.Index];
        var path = Path.Combine(menuRepo, change.Path.Replace('/', '\\'));
        var menu = new List<QuickMenu.Command?>();
        var showChanges = new QuickMenu.Command("Show changes", () => ShowChanges(change));
        if (settings.DiffPlacement is DiffPlacement.Window or DiffPlacement.Editor)
        {
            showChanges = showChanges with
            {
                Shortcut = "Enter"
            };
        }

        menu.Add(showChanges);
        if (settings.DiffPlacement != DiffPlacement.Editor)
        {
            var inEditor = new QuickMenu.Command("Show changes in editor", () => OpenDiff(change));
            if (settings.DiffPlacement != DiffPlacement.Window)
            {
                inEditor = inEditor with
                {
                    Shortcut = "Enter"
                };
            }

            menu.Add(inEditor);
        }

        menu.Add(
            new("Show in folder", () => ShowInFolderRequested?.Invoke(path), File.Exists(path))
            {
                Glyph = Icons.Folder
            });
        menu.Add(
            new("Copy path", () => FolderView.SetClipboardText(path))
            {
                Glyph = Icons.CopyAsPath
            });
        PaneMenu.Show(files, args.Location, menu, open => MenuOpen = open);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            EscapePressed?.Invoke();
            return true;
        }

        if (keyData == (Keys.Control | Keys.C) &&
            commits.Focused &&
            selected != null)
        {
            FolderView.SetClipboardText(selected.Sha);
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    // As Desktop draws a commit: its summary semibold, and under it the author and how long ago,
    // smaller. At the right, its tags and its arrow for one not pushed: see Indicators.
    void DrawCommit(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= loaded.Count)
        {
            return;
        }

        var commit = loaded[index];
        var (background, text, secondary, onBlue) = commits.Colours(index);
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        RowList.DrawRowLine(graphics, bounds);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var left = bounds.Left + LogicalToDeviceUnits(inset);
        var lineGap = LogicalToDeviceUnits(3);
        var top = bounds.Top + (bounds.Height - bold.Height - lineGap - small.Height) / 2;

        var (pill, pillText) = PillColours(onBlue, commits.SelectedIndices.Contains(index));
        var textRight = bounds.Right - LogicalToDeviceUnits(15);
        foreach (var indicator in Indicators(commit, bounds))
        {
            if (indicator.Tag is { } tag)
            {
                DesktopStyle.DrawPill(graphics, indicator.Bounds, LogicalToDeviceUnits(DesktopStyle.Radius), pill, tag, Font, pillText);
            }
            else
            {
                var badge = indicator.Bounds;
                var arrowSize = LogicalToDeviceUnits(12);
                DesktopStyle.DrawPill(graphics, badge, LogicalToDeviceUnits(8), pill, "", Font, pillText);
                graphics.DrawImage(Icons.Bitmap(Icons.PromotedFiles, arrowSize, pillText), badge.Left + (badge.Width - arrowSize) / 2, badge.Top + (badge.Height - arrowSize) / 2, arrowSize, arrowSize);
            }

            // Desktop's 10 pixels between the text and the pills.
            textRight = indicator.Bounds.Left - LogicalToDeviceUnits(10);
        }

        TextRenderer.DrawText(graphics, commit.Subject, bold, Rectangle.FromLTRB(left, top, textRight, top + bold.Height), text, flags);

        var secondTop = top + bold.Height + lineGap;
        var byline = $"{commit.Author} • {GitBranches.Ago(commit.When, Time.GetUtcNow())}";
        TextRenderer.DrawText(graphics, byline, small, Rectangle.FromLTRB(left, secondTop, textRight, secondTop + small.Height), secondary, flags);
    }

    /// <summary>
    /// What is at the right of a commit's row drawn in <paramref name="bounds"/>, from the right:
    /// its tags in grey pills, as many as fit in half the row, then an arrow in a pill for one not
    /// pushed. Each with the tag it shows, null for the arrow, and its tooltip.
    /// </summary>
    List<(Rectangle Bounds, string? Tag, string Tip)> Indicators(GitCommit commit, Rectangle bounds)
    {
        var indicators = new List<(Rectangle, string?, string)>();
        var left = bounds.Left + LogicalToDeviceUnits(inset);
        var right = bounds.Right - LogicalToDeviceUnits(15);
        var half = left + (right - left) / 2;
        var pillHeight = Math.Max(LogicalToDeviceUnits(16), Font.Height + LogicalToDeviceUnits(1));
        var pillTop = bounds.Top + (bounds.Height - pillHeight) / 2;
        var gap = LogicalToDeviceUnits(5);
        foreach (var tag in commit.Tags.Reverse())
        {
            var width = TextRenderer.MeasureText(tag, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + LogicalToDeviceUnits(10);
            var badge = new Rectangle(right - width, pillTop, width, pillHeight);
            if (badge.Left < half)
            {
                break;
            }

            indicators.Add((badge, tag, $"Tag {tag}"));
            right = badge.Left - gap;
        }

        if (unpushed.Contains(commit.Sha))
        {
            var width = LogicalToDeviceUnits(12) + LogicalToDeviceUnits(10);
            indicators.Add((new(right - width, pillTop, width, pillHeight), null, notPushed));
        }

        return indicators;
    }

    /// <summary>
    /// The tooltip at a point of the list of commits: what a tag or the arrow under it is. Null
    /// elsewhere.
    /// </summary>
    internal string? CommitTipAt(Point point) =>
        IndicatorAt(point)?.Tip;

    // The tag or arrow at a point of the list of commits, null elsewhere.
    (Rectangle Bounds, string? Tag, string Tip)? IndicatorAt(Point point)
    {
        if (commits.HitTest(point).Item is not { } item ||
            item.Index >= loaded.Count)
        {
            return null;
        }

        var row = commits.GetItemRect(item.Index);
        row.Width = commits.Columns[0].Width;
        foreach (var indicator in Indicators(loaded[item.Index], row))
        {
            if (indicator.Bounds.Contains(point))
            {
                return indicator;
            }
        }

        return null;
    }

    // The arrow of a commit not pushed is a button: clicked, it asks to push, as the status
    // bar's commits ahead do. A tag is only a label.
    Action? ButtonAt(Point point)
    {
        if (IndicatorAt(point) is { Tag: null })
        {
            return () => PushRequested?.Invoke();
        }

        return null;
    }

    // A tag's pill: white on the blue of the row picked in the list with the focus, darker on the
    // grey of one picked in the list without, as Desktop has them.
    static (Color Fill, Color Text) PillColours(bool onBlue, bool selected)
    {
        if (onBlue)
        {
            return (DesktopStyle.OnBlue, DesktopStyle.Blue);
        }

        if (selected)
        {
            return (DesktopStyle.SelectedPill, DesktopStyle.Text);
        }

        return (DesktopStyle.Pill, DesktopStyle.PillText);
    }

    void DrawFile(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= changed.Count)
        {
            return;
        }

        var (background, text, secondary, onBlue) = files.Colours(index);
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        RowList.DrawRowLine(graphics, bounds);
        var room = Rectangle.FromLTRB(bounds.Left + LogicalToDeviceUnits(inset), bounds.Top, bounds.Right, bounds.Bottom);
        ChangeRows.Draw(graphics, this, files.Font, room, changed[index], text, secondary, onBlue);
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
            bold.Dispose();
            small.Dispose();
            title.Dispose();
            commitTip.Dispose();
            fileTip.Dispose();
        }

        base.Dispose(disposing);
    }

    // Above the commits while comparing, as Desktop's Compare tab has it: the branch compared
    // with, and a close button back to the branch's own commits; under that, Behind and Ahead,
    // each with how many, the one listed pressed, and, behind, a button to merge them in.
    sealed class CompareBar : TableLayoutPanel
    {
        Label title = new()
        {
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        Button close = new()
        {
            Text = "✕",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            AccessibleName = "Stop comparing"
        };
        RadioButton behind = new()
        {
            Appearance = Appearance.Button,
            AutoSize = true,
            Name = "behind"
        };
        RadioButton ahead = new()
        {
            Appearance = Appearance.Button,
            AutoSize = true,
            Name = "ahead"
        };
        Button merge = new()
        {
            AutoSize = true,
            Name = "merge"
        };

        public string Description { get; private set; } = "";

        public CompareBar(HistoryPane pane)
        {
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 2;
            RowCount = 2;
            BackColor = DesktopStyle.Background;
            Padding = new(DesktopStyle.Inset - 3, 4, 4, 4);
            ColumnStyles.Add(new(SizeType.Percent, 100));
            ColumnStyles.Add(new(SizeType.AutoSize));
            close.FlatAppearance.BorderSize = 0;
            close.Click += (_, _) => pane.EndCompare();
            behind.Click += (_, _) => pane.ShowAhead(false);
            ahead.Click += (_, _) => pane.ShowAhead(true);
            merge.Click += (_, _) => pane.MergeCompared();

            var tabs = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            tabs.Controls.Add(behind);
            tabs.Controls.Add(ahead);
            tabs.Controls.Add(merge);
            Controls.Add(title, 0, 0);
            Controls.Add(close, 1, 0);
            Controls.Add(tabs, 0, 1);
            SetColumnSpan(tabs, 2);
        }

        public void ShowFor(string branch, bool showAhead, (int Ahead, int Behind)? apart)
        {
            title.Text = $"Compared to {branch}";
            var behindText = "Behind";
            var aheadText = "Ahead";
            if (apart is { } counts)
            {
                behindText = $"Behind ({counts.Behind})";
                aheadText = $"Ahead ({counts.Ahead})";
            }

            behind.Text = behindText;
            ahead.Text = aheadText;
            behind.Checked = !showAhead;
            ahead.Checked = showAhead;

            // Merging brings in the commits behind, so is offered there, where there are any.
            var behindCount = apart?.Behind ?? 0;
            merge.Visible = !showAhead &&
                            behindCount > 0;
            merge.Text = $"Merge {GitState.CountOf(behindCount, "commit")}…";
            Description = $"{title.Text}: {behindText}, {aheadText}";
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using var pen = new Pen(DesktopStyle.Border);
            args.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    // Above the files, as Desktop shows a commit picked: its summary, larger and semibold, then
    // its author, its sha and when, smaller, and a grey bar saying how many files it changed. A line along its top, between it and the commits: with the commits cut
    // off part way through a row, above that row's own line, nothing else stands between them.
    sealed class CommitHeading : Control
    {
        // Pixels at 100% scale.
        const int barHeight = 30;

        HistoryPane pane;

        public CommitHeading(HistoryPane pane)
        {
            this.pane = pane;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Dock = DockStyle.Top;
            Height = 10 + pane.title.Height + 5 + pane.small.Height + 10 + barHeight;
            BackColor = DesktopStyle.Background;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(BackColor);
            using (var divider = new Pen(ExplorerStyle.Divider))
            {
                graphics.DrawLine(divider, 0, 0, Width, 0);
            }

            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            var left = LogicalToDeviceUnits(inset);
            if (pane.selected is not { } commit)
            {
                TextRenderer.DrawText(graphics, "Pick a commit to see the files it changed", Font, Rectangle.FromLTRB(left, 0, Width - left, Height), DesktopStyle.SecondaryText, flags | TextFormatFlags.VerticalCenter);
                return;
            }

            var top = LogicalToDeviceUnits(10);
            TextRenderer.DrawText(graphics, commit.Subject, pane.title, Rectangle.FromLTRB(left, top, Width - left, top + pane.title.Height), DesktopStyle.Text, flags);

            var metaTop = top + pane.title.Height + LogicalToDeviceUnits(5);
            var meta = $"{commit.Author} • {commit.ShortSha} • {commit.When.ToLocalTime():g}";
            TextRenderer.DrawText(graphics, meta, pane.small, Rectangle.FromLTRB(left, metaTop, Width - left, metaTop + pane.small.Height), DesktopStyle.SecondaryText, flags);

            var bar = Rectangle.FromLTRB(0, Height - LogicalToDeviceUnits(barHeight), Width, Height);
            using (var fill = new SolidBrush(DesktopStyle.AltBackground))
            {
                graphics.FillRectangle(fill, bar);
            }

            using (var pen = new Pen(DesktopStyle.Border))
            {
                graphics.DrawLine(pen, 0, bar.Top, Width, bar.Top);
                graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            var count = GitState.CountOf(pane.changed.Count, "changed file");
            TextRenderer.DrawText(graphics, count, Font, bar, DesktopStyle.Text, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
