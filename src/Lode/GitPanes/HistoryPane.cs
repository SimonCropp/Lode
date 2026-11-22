/// <summary>
/// The History pane, as GitHub Desktop's History tab lists commits: the branch's commits, newest
/// first, each with its subject, its author's avatar and name, how long ago, its tags, and an
/// arrow for one not pushed yet. Beneath, the files the commit picked changed, each shown changed
/// on a double-click. A page of commits at a time, the next as the list nears its end.
/// </summary>
sealed class HistoryPane : UserControl
{
    // Pixels at 100% scale.
    const int inset = 12;
    const int avatarSize = 18;

    PaneHeader header = new("History");
    PaneSplitContainer split = new();
    RowList commits;
    RowList files;
    CommitHeading heading;
    Settings settings;
    Font bold;

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
    List<Change> changed = [];

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

    public PaneHeader Header => header;

    public bool MenuOpen { get; private set; }

    /// <summary>
    /// The commits listed. For tests.
    /// </summary>
    internal IReadOnlyList<GitCommit> Commits => loaded;

    internal RowList CommitList => commits;
    internal RowList FileList => files;

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
        bold = new(Font, FontStyle.Bold);
        commits = new(CommitRowHeight(Font))
        {
            MultiSelect = false,
            AccessibleName = "Commits"
        };
        commits.DrawRow += DrawCommit;
        commits.SelectedIndexChanged += (_, _) => OnCommitPicked();
        commits.RetrieveVirtualItem += (_, args) => NearEnd(args.ItemIndex);
        commits.MouseUp += OnCommitsMouseUp;
        commits.GotFocus += (_, _) => commits.Invalidate();
        commits.LostFocus += (_, _) => commits.Invalidate();

        files = new(Font.Height + 10)
        {
            AccessibleName = "Files changed"
        };
        files.DrawRow += DrawFile;
        files.ItemActivate += (_, _) => OpenDiff(files.FocusedItem?.Index ?? -1);
        files.MouseUp += OnFilesMouseUp;
        files.GotFocus += (_, _) => files.Invalidate();
        files.LostFocus += (_, _) => files.Invalidate();
        heading = new(this);

        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Horizontal;
        split.Panel1.Controls.Add(commits);
        split.Panel2.Controls.Add(files);
        split.Panel2.Controls.Add(heading);
        split.SizeChanged += (_, _) => PlaceSplitter();

        Controls.Add(split);
        Controls.Add(header);
    }

    static int CommitRowHeight(Font font) =>
        font.Height * 2 + 12;

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
        version++;
        loaded = [];
        unpushed = [];
        more = false;
        selected = null;
        changed = [];
        commits.SetRows(0);
        files.SetRows(0);
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
        this.reading = true;
        stale = false;
        try
        {
            var (read, notPushed, readSite) = await Task.Run(() => (
                ReadCommits(reading, 0, count, Cancel.None),
                ReadUnpushed(reading, Cancel.None),
                RemoteSite.OfRepo(reading)));
            if (asked != version ||
                IsDisposed)
            {
                return;
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
        try
        {
            var read = await Task.Run(() => ReadCommits(reading, skip, GitHistory.Page, Cancel.None));
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

    // The commits as read, the one picked before picked again, by its sha, where it still is.
    void ShowCommits()
    {
        var keep = selected?.Sha;
        var top = 0;
        if (commits.VirtualListSize > 0 &&
            commits.TopItem is { } topItem)
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
    }

    void OpenDiff(int row)
    {
        if (repo == null ||
            selected == null ||
            row < 0 ||
            row >= changed.Count)
        {
            return;
        }

        GitDiff.OfCommit(this, settings, repo, selected, changed[row]);
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
        var commit = loaded[item.Index];
        var menu = new List<QuickMenu.Command?>
        {
            new("Copy SHA", () => FolderView.SetClipboardText(commit.Sha))
            {
                Glyph = Icons.Copy
            },
            new("Copy message", () => CopyMessage(menuRepo, commit))
        };
        if (site is { } onSite)
        {
            menu.Add(null);
            menu.Add(
                new($"View on {onSite.Name}", () => OpenSiteRequested?.Invoke(onSite.CommitUrl(commit.Sha)))
                {
                    Glyph = Icons.Links
                });
        }

        PaneMenu.Show(commits, args.Location, menu, open => MenuOpen = open);
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
        var menu = new List<QuickMenu.Command?>
        {
            new("Show changes", () => OpenDiff(item.Index))
            {
                Shortcut = "Enter"
            },
            new("Show in folder", () => ShowInFolderRequested?.Invoke(path), File.Exists(path))
            {
                Glyph = Icons.Folder
            },
            new("Copy path", () => FolderView.SetClipboardText(path))
            {
                Glyph = Icons.CopyAsPath
            }
        };
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

    // Two lines: the subject, then the avatar, the author and how long ago. The tags at the right,
    // and an arrow for a commit not pushed.
    void DrawCommit(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= loaded.Count)
        {
            return;
        }

        var commit = loaded[index];
        var (background, text, secondary) = commits.Colours(commits.SelectedIndices.Contains(index));
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        using (var pen = new Pen(ExplorerStyle.PaneDivider))
        {
            graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var left = bounds.Left + LogicalToDeviceUnits(inset);
        var right = bounds.Right - LogicalToDeviceUnits(inset);
        var lineHeight = Font.Height;
        var top = bounds.Top + (bounds.Height - lineHeight * 2 - LogicalToDeviceUnits(2)) / 2;

        // The tags, from the right, each in a rounded badge.
        var badgeRight = right;
        foreach (var tag in commit.Tags.Reverse())
        {
            var size = TextRenderer.MeasureText(graphics, tag, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var padding = LogicalToDeviceUnits(6);
            var badge = new Rectangle(badgeRight - size.Width - padding * 2, bounds.Top + (bounds.Height - lineHeight - LogicalToDeviceUnits(4)) / 2, size.Width + padding * 2, lineHeight + LogicalToDeviceUnits(4));
            if (badge.Left < left + LogicalToDeviceUnits(80))
            {
                break;
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = ExplorerStyle.RoundedRectangle(badge, badge.Height / 2))
            using (var brush = new SolidBrush(ExplorerStyle.Hover(ExplorerStyle.Hover(background))))
            {
                graphics.FillPath(brush, shape);
            }

            graphics.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(graphics, tag, Font, badge, secondary, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            badgeRight = badge.Left - LogicalToDeviceUnits(4);
        }

        if (unpushed.Contains(commit.Sha))
        {
            var arrowSize = LogicalToDeviceUnits(14);
            var arrow = Icons.Bitmap(Icons.PromotedFiles, arrowSize, secondary);
            graphics.DrawImage(arrow, badgeRight - arrowSize, bounds.Top + (bounds.Height - arrowSize) / 2, arrowSize, arrowSize);
            badgeRight -= arrowSize + LogicalToDeviceUnits(6);
        }

        var subject = Rectangle.FromLTRB(left, top, badgeRight, top + lineHeight);
        TextRenderer.DrawText(graphics, commit.Subject, Font, subject, text, flags);

        var secondTop = top + lineHeight + LogicalToDeviceUnits(2);
        var avatar = LogicalToDeviceUnits(avatarSize);
        var avatarBounds = new Rectangle(left, secondTop + (lineHeight - avatar) / 2, avatar, avatar);
        Avatars.Draw(graphics, avatarBounds, commit.Author, commit.Email, site?.Name == "GitHub");
        var byline = $"{commit.Author} • {GitBranches.Ago(commit.When, Time.GetUtcNow())}";
        var bylineBounds = Rectangle.FromLTRB(avatarBounds.Right + LogicalToDeviceUnits(6), secondTop, badgeRight, secondTop + lineHeight);
        TextRenderer.DrawText(graphics, byline, Font, bylineBounds, secondary, flags);
    }

    void DrawFile(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= changed.Count)
        {
            return;
        }

        var (background, text, secondary) = files.Colours(files.SelectedIndices.Contains(index));
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        var room = Rectangle.FromLTRB(bounds.Left + LogicalToDeviceUnits(inset), bounds.Top, bounds.Right, bounds.Bottom);
        ChangeRows.Draw(graphics, this, files.Font, room, changed[index], text, secondary);
    }

    /// <summary>
    /// Draws the avatars that arrived since the list was last drawn.
    /// </summary>
    public void AvatarsArrived()
    {
        if (showing)
        {
            commits.Invalidate();
        }
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
        }

        base.Dispose(disposing);
    }

    // Above the files: the picked commit's subject, then its sha, author and when.
    sealed class CommitHeading : Control
    {
        HistoryPane pane;

        public CommitHeading(HistoryPane pane)
        {
            this.pane = pane;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Dock = DockStyle.Top;
            Height = pane.Font.Height * 2 + 18;
            BackColor = ExplorerStyle.Window;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(BackColor);
            using (var pen = new Pen(ExplorerStyle.Divider))
            {
                graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            var left = LogicalToDeviceUnits(inset);
            var top = LogicalToDeviceUnits(7);
            var lineHeight = Font.Height;
            if (pane.selected is not { } commit)
            {
                TextRenderer.DrawText(graphics, "Pick a commit to see the files it changed", Font, Rectangle.FromLTRB(left, 0, Width - left, Height), ExplorerStyle.SecondaryText, flags | TextFormatFlags.VerticalCenter);
                return;
            }

            TextRenderer.DrawText(graphics, commit.Subject, pane.bold, Rectangle.FromLTRB(left, top, Width - left, top + lineHeight), ForeColor, flags);
            var count = GitState.CountOf(pane.changed.Count, "file");
            var line = $"{commit.ShortSha} • {commit.Author} • {commit.When.ToLocalTime():g} • {count}";
            TextRenderer.DrawText(graphics, line, Font, Rectangle.FromLTRB(left, top + lineHeight + LogicalToDeviceUnits(2), Width - left, top + lineHeight * 2 + LogicalToDeviceUnits(2)), ExplorerStyle.SecondaryText, flags);
        }
    }
}
