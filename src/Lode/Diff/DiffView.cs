/// <summary>
/// A file's diff, wherever it shows: in the Diff tab, over the folder list, or in the diff
/// window. Along its top, the file, a renamed one's old name with an arrow, how many lines were
/// added and deleted, what happened to it, and the button for GitHub Desktop's diff options:
/// hiding whitespace changes, and the old file beside the new or one above the other. Beneath,
/// the lines, the images, or what there is to say instead: a binary file, an empty one, one only
/// renamed, or a diff too big to show straight away, with the button that shows it anyway.
/// </summary>
sealed class DiffView : UserControl
{
    Settings settings;
    DiffHeader header;
    DiffCanvas canvas = new()
    {
        Dock = DockStyle.Fill,
        Visible = false
    };

    ImageDiffView images = new()
    {
        Dock = DockStyle.Fill,
        Visible = false
    };

    MessagePanel message = new()
    {
        Dock = DockStyle.Fill
    };

    DiffSource? source;
    FileDiff? shown;
    int version;

    // The file read again while lines of it were being picked by dragging down their numbers,
    // to show once the drag is let go.
    (DiffSource File, Prepared Read, bool HideWhitespace)? held;

    /// <summary>
    /// Reads a file's diff. Tests answer for git.
    /// </summary>
    internal static Func<DiffSource, bool, bool, Cancel, FileDiff> Read { get; set; } = GitFileDiff.Read;

    /// <summary>
    /// Reads the lines of the file a diff is of, to show more of it. Tests answer for git.
    /// </summary>
    internal static Func<DiffSource, IReadOnlyList<string>?> ReadNewLines { get; set; } = GitFileDiff.NewLines;

    /// <summary>
    /// What sends a file to the Recycle Bin, with the window asking as its owner, before lines of
    /// it are discarded. Tests stand in for the Recycle Bin, so a run leaves nothing there.
    /// </summary>
    internal static Func<IntPtr, CodeCommands.Recycler> Recycler { get; set; } = owner => paths => FileOperations.Delete(owner, paths, permanent: false);

    /// <summary>
    /// Discards lines of a file's uncommitted change. Tests answer for git.
    /// </summary>
    internal static Func<DiffSource, string, IReadOnlyCollection<int>, CodeCommands.Recycler, Cancel, Task<CodeCommands.RepoResult>> DiscardLines { get; set; } = GitChanges.DiscardLines;

    /// <summary>
    /// Lines of a file in the repository were discarded from a diff, wherever it shows, for the
    /// Changes pane to read what is uncommitted in it again, as after Discard changes there.
    /// </summary>
    internal static event Action<string>? LinesDiscarded;

    /// <summary>
    /// The discarding under way, from the question to the diff read again, for tests to wait on.
    /// </summary>
    internal Task Discarding { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Lays a text diff out in rows, one above the other or side by side, off the UI thread.
    /// Tests watch where, and hold it up.
    /// </summary>
    internal static Func<TextDiff, IReadOnlyList<string>?, bool, DiffLayout> LayOut { get; set; } = DiffLayout.Of;

    /// <summary>
    /// A diff read, ready to show: a text diff laid out.
    /// </summary>
    readonly record struct Prepared(FileDiff Diff, DiffLayout? Layout);

    /// <summary>
    /// The × along the top was clicked, or Escape pressed: the diff over the folder list goes.
    /// </summary>
    public event Action? CloseRequested;

    public DiffView(Settings settings)
    {
        this.settings = settings;
        BackColor = DiffStyle.Background;
        header = new(this);
        header.OptionsClicked += ShowOptions;
        header.CloseClicked += () => CloseRequested?.Invoke();
        message.ButtonClicked += OnMessageButton;
        canvas.PicksChanged += OnCanvasPicked;
        canvas.DiscardRequested += OnDiscardRequested;
        CommitDrafts.PicksChanged += OnDraftPicked;
        Controls.Add(canvas);
        Controls.Add(images);
        Controls.Add(message);
        Controls.Add(header);
        header.Visible = false;
        message.Show("", null);
    }

    /// <summary>
    /// What the diff is of. Null when it says something else instead.
    /// </summary>
    public DiffSource? Source => source;

    /// <summary>
    /// The reading under way, for tests to wait on.
    /// </summary>
    internal Task Reading { get; private set; } = Task.CompletedTask;

    internal DiffCanvas Canvas => canvas;

    /// <summary>
    /// The header's tooltip at a point of it, and how wide it is. For tests.
    /// </summary>
    internal string? HeaderTipAt(Point point) => header.TipAt(point);

    internal Size HeaderSize => header.Size;

    /// <summary>
    /// What it says instead of a diff, when it does. For tests.
    /// </summary>
    internal string? MessageText
    {
        get
        {
            if (message.Visible)
            {
                return message.Text;
            }

            return null;
        }
    }

    /// <summary>
    /// Whether the × shows along the top, for the diff over the folder list, which it puts away.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowsClose
    {
        get => header.ShowsClose;
        set => header.ShowsClose = value;
    }

    /// <summary>
    /// Shows the diff of <paramref name="file"/>, read from git, keeping the place when it is the
    /// file already shown, read again.
    /// </summary>
    public void Show(DiffSource file)
    {
        var keepPlace = file.SameFile(source);
        source = file;
        Reading = ReadAsync(file, keepPlace, evenIfLarge: false);
    }

    /// <summary>
    /// Reads the file shown again: it changed, or how it is shown did.
    /// </summary>
    public void Reload()
    {
        if (source != null)
        {
            Reading = ReadAsync(source, keepPlace: true, evenIfLarge: shown is TextDiff);
        }
    }

    /// <summary>
    /// Says <paramref name="text"/> instead of a diff: that several files are picked, say.
    /// </summary>
    public void ShowMessage(string text)
    {
        version++;
        source = null;
        shown = null;
        held = null;
        header.Visible = false;
        ShowOnly(message);
        message.Show(text, null);
        Reading = Task.CompletedTask;
    }

    // Reads the diff, or, given largePatch, the patch of a diff too big to show straight away,
    // asked to be shown anyway, parses it, rather than reading it again. Everything that takes
    // as long as the diff is big is done off the UI thread: reading it, parsing it, laying it
    // out, and, for lines to be picked, hashing it. What a later read asked for shows, whichever
    // ends first.
    async Task ReadAsync(DiffSource file, bool keepPlace, bool evenIfLarge, string? largePatch = null)
    {
        var asked = ++version;
        var hideWhitespace = settings.DiffHideWhitespace;
        var split = settings.DiffSplit;
        var read = await Task.Run(() =>
        {
            FileDiff diff;
            if (largePatch == null)
            {
                diff = Read(file, hideWhitespace, evenIfLarge, Cancel.None);
            }
            else
            {
                diff = DiffParser.Parse(largePatch, evenIfLarge: true);
            }

            if (diff is not TextDiff text)
            {
                return new Prepared(diff, null);
            }

            if (file.IsUncommitted)
            {
                // Worked out here, once, rather than as the lines picked are matched to it.
                _ = text.PatchHash;
            }

            return new Prepared(diff, LayOut(text, ReadNewLines(file), split));
        });
        if (asked != version ||
            IsDisposed)
        {
            return;
        }

        Present(file, read, keepPlace, hideWhitespace);
    }

    void Present(DiffSource file, Prepared read, bool keepPlace, bool hideWhitespace)
    {
        // The drag picks lines of the diff it started on: what was read waits for it to end,
        // rather than taking the lines from under it, the drag lost.
        if (keepPlace &&
            canvas.Dragging)
        {
            held = (file, read, hideWhitespace);
            return;
        }

        held = null;
        var diff = read.Diff;
        shown = diff;
        header.Show(file.File, diff as TextDiff);
        header.Visible = true;
        if (read.Layout is { } layout)
        {
            var (picks, hint) = PicksFor(file, layout.Diff, hideWhitespace);
            canvas.Discardable = CanDiscard(file, layout.Diff, hideWhitespace);
            canvas.Show(layout, settings.DiffSplit, picks, hint, keepPlace);
            ShowOnly(canvas);
            return;
        }

        switch (diff)
        {
            case ImageDiff image when images.Show(image):
                ShowOnly(images);
                return;
            case ImageDiff or BinaryDiff:
                ShowMessageOnly("This binary file has changed.", null);
                return;
            case LargeDiff:
                ShowMessageOnly("This diff is too big to show straight away.", "Show diff");
                return;
            case UnrenderableDiff:
                ShowMessageOnly("This diff is too big to show.", null);
                return;
            case FailedDiff failed:
                ShowMessageOnly($"Git couldn't show the changes.\n\n{failed.Error}", null);
                return;
            default:
                var (said, button) = EmptyMessage(file.File, hideWhitespace);
                ShowMessageOnly(said, button);
                return;
        }
    }

    /// <summary>
    /// What a diff with nothing in it says, as GitHub Desktop says it, with the button that shows
    /// the whitespace changes hidden, when they are.
    /// </summary>
    internal static (string Text, string? Button) EmptyMessage(Change file, bool hideWhitespace)
    {
        if (file.Kind is GitChange.Added or GitChange.Untracked)
        {
            return ("The file is empty.", null);
        }

        if (file.Kind == GitChange.Renamed)
        {
            return ("The file was renamed but not changed.", null);
        }

        if (hideWhitespace)
        {
            return ("Only whitespace changes found.", "Show whitespace changes");
        }

        return ("No content changes found.", null);
    }

    /// <summary>
    /// The lines picked of an uncommitted change, and, where they can't be picked though the
    /// change is uncommitted, why, for a click on the numbers to say.
    /// </summary>
    internal static (LinePicks? Picks, string? Hint) PicksFor(DiffSource file, TextDiff diff, bool hideWhitespace)
    {
        if (!file.IsUncommitted)
        {
            return (null, null);
        }

        if (file.Merging)
        {
            return (null, "Part way through a merge, everything is committed.");
        }

        // Edited since, a renamed file is Modified, and still renamed.
        if (file.File.Kind == GitChange.Renamed ||
            file.File.OldPath != null)
        {
            return (null, "A renamed file is committed whole: tick or untick it in Changes.");
        }

        if (file.File.Kind == GitChange.Conflicted)
        {
            return (null, null);
        }

        if (hideWhitespace)
        {
            return (null, "Lines can't be picked with whitespace changes hidden. Show them from the diff options to pick lines.");
        }

        return (CommitDrafts.PicksOf(file.Repo, file.File.Path, diff), null);
    }

    /// <summary>
    /// Whether lines of <paramref name="diff"/> can be discarded from the menu: an uncommitted
    /// change to a file git tracks, changed in place, whose diff, against the last commit, is
    /// of the file on disk as it is, staged or not, so the lines can be taken out of it there.
    /// Not a file git doesn't track, nor one added, deleted or renamed, which Discard changes in
    /// Changes puts back whole, nor one in conflict; nor part way through a merge, when git
    /// commits everything, nor with whitespace hidden, when the diff isn't what the file has.
    /// A binary file has no lines.
    /// </summary>
    internal static bool CanDiscard(DiffSource file, TextDiff diff, bool hideWhitespace) =>
        file is { IsUncommitted: true, HasCommits: true, Merging: false } &&
        file.File is { OldPath: null, Kind: GitChange.Modified or GitChange.Staged } &&
        !hideWhitespace &&
        LinePatch.CanDiscard(diff);

    void ShowMessageOnly(string text, string? button)
    {
        message.Show(text, button);
        ShowOnly(message);
    }

    void ShowOnly(Control control)
    {
        canvas.Visible = control == canvas;
        images.Visible = control == images;
        message.Visible = control == message;
    }

    void OnMessageButton()
    {
        if (source == null)
        {
            return;
        }

        if (shown is LargeDiff large)
        {
            Reading = ReadAsync(source, keepPlace: false, evenIfLarge: true, large.Patch);
            return;
        }

        // Show whitespace changes.
        SetHideWhitespace(false);
    }

    void OnCanvasPicked(LinePicks picks)
    {
        if (source is { IsUncommitted: true } file)
        {
            CommitDrafts.SetPicks(file.Repo, file.File.Path, picks);
        }

        // A drag let go, the lines it picked noted: what was read during it shows now, as long
        // as the diff is still of that file.
        if (held is { } waiting &&
            !canvas.Dragging)
        {
            held = null;
            if (waiting.File.SameFile(source))
            {
                Present(waiting.File, waiting.Read, keepPlace: true, waiting.HideWhitespace);
            }
        }
    }

    // A tick clicked in the Changes pane, of this file among others perhaps: the file's lines
    // all picked, or none.
    void OnDraftPicked(string repo, IReadOnlySet<string> paths)
    {
        if (source is not { IsUncommitted: true } file ||
            shown is not TextDiff diff ||
            canvas.Picks == null ||
            !Locations.Same(repo, file.Repo) ||
            !paths.Contains(file.File.Path))
        {
            return;
        }

        canvas.SetPicks(CommitDrafts.PicksOf(repo, file.File.Path, diff));
    }

    // One at a time: another, of the diff before the first's lines went, would find it changed.
    void OnDiscardRequested(IReadOnlyList<int> lines, string what)
    {
        if (Discarding.IsCompleted)
        {
            Discarding = DiscardAsync(lines, what);
        }
    }

    // Discard line or Discard hunk: having asked, as Discard changes in Changes does, the file
    // as it is goes to the Recycle Bin first, the lines are taken out of it on disk, in the
    // Actions panel, and then what is uncommitted is read again, by the Changes pane and here,
    // whether they were or not, as what is there now is what to show. Lines of the file left
    // out of the commit stay left out, which the file changed would otherwise forget, ticking it
    // whole again.
    async Task DiscardAsync(IReadOnlyList<int> lines, string what)
    {
        if (source is not { } file ||
            shown is not TextDiff diff ||
            !canvas.Discardable ||
            lines.Count == 0)
        {
            return;
        }

        if (ChangesPane.AskDiscard(this, $"Discard {what} in {file.File.Path}?\n\nThe file as it is now goes to the Recycle Bin.") != DialogResult.OK)
        {
            return;
        }

        var patchHash = diff.PatchHash;
        var picked = canvas.Picks;
        var recycle = Recycler(Handle);
        CodeCommands.RepoResult? discarded = null;
        await ActionLog.Run(
            "Git discard",
            [file.Repo],
            async (_, cancel) =>
            {
                discarded = await DiscardLines(file, patchHash, lines, recycle, cancel);
                GitCache.MarkStale([file.Repo]);
                return CodeCommands.GitOutcome([discarded]);
            });
        if (discarded is { Succeeded: true } &&
            picked is { State: null } &&
            picked.PatchHash == patchHash)
        {
            // As it is about to be shown: whitespace and all, as lines aren't discarded with it
            // hidden, and whole, as it was shown.
            var after = await Task.Run(() => Read(file, false, true, Cancel.None));
            if (after is TextDiff afterDiff &&
                picked.AfterDiscarding(diff, lines.ToHashSet(), afterDiff) is { } kept)
            {
                CommitDrafts.KeepPicks(file.Repo, file.File.Path, kept);
            }
        }

        LinesDiscarded?.Invoke(file.Repo);
        if (!IsDisposed &&
            file.SameFile(source))
        {
            Reload();
        }
    }

    void ShowOptions(Control button)
    {
        var split = settings.DiffSplit;
        var commands = new List<QuickMenu.Command?>
        {
            new("Hide whitespace changes", () => SetHideWhitespace(!settings.DiffHideWhitespace))
            {
                Checked = settings.DiffHideWhitespace
            },
            null,
            new("Unified", () => SetSplit(false))
            {
                Checked = !split
            },
            new("Split", () => SetSplit(true))
            {
                Checked = split
            }
        };
        PaneMenu.Show(button, new(0, button.Height), commands, _ => { });
    }

    /// <summary>
    /// Hides whitespace changes, or shows them, for every diff from then on, reading this one
    /// again.
    /// </summary>
    void SetHideWhitespace(bool hide)
    {
        settings.DiffHideWhitespace = hide;
        settings.Save();
        Reload();
    }

    /// <summary>
    /// Shows the old file beside the new, or one above the other, for every diff from then on.
    /// </summary>
    internal void SetSplit(bool split)
    {
        settings.DiffSplit = split;
        settings.Save();
        canvas.Split = split;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape &&
            ShowsClose)
        {
            CloseRequested?.Invoke();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CommitDrafts.PicksChanged -= OnDraftPicked;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Along the top: the file, how many lines were added and deleted, what happened to it, and
    /// the buttons for the options and, over the folder list, to put it away.
    /// </summary>
    sealed class DiffHeader : Panel
    {
        GlyphButton options = new()
        {
            Dock = DockStyle.Right,
            Width = 32,
            TabStop = false,
            Text = Icons.Options.ToString(),
            AccessibleName = "Diff options"
        };

        GlyphButton close = new()
        {
            Dock = DockStyle.Right,
            Width = 32,
            TabStop = false,
            Text = Icons.Close.ToString(),
            AccessibleName = "Close the diff",
            Visible = false
        };

        ToolTip tip = new();
        RowTip headerTip;
        Change? file;
        int added;
        int deleted;
        bool counts;

        public event Action<Control>? OptionsClicked;
        public event Action? CloseClicked;

        public DiffHeader(Control owner)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = owner.LogicalToDeviceUnits(DesktopStyle.BoxHeight + 10);
            BackColor = DesktopStyle.AltBackground;
            Padding = new(0, 2, 4, 3);
            options.Click += (_, _) => OptionsClicked?.Invoke(options);
            close.Click += (_, _) => CloseClicked?.Invoke();
            tip.SetToolTip(options, "Diff options: whitespace, unified or split");
            tip.SetToolTip(close, "Close the diff (Esc)");
            Controls.Add(options);
            Controls.Add(close);
            headerTip = new(this, TipAt);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowsClose
        {
            get => close.Visible;
            set => close.Visible = value;
        }

        public void Show(Change shownFile, TextDiff? diff)
        {
            file = shownFile;
            counts = diff != null;
            added = diff?.Added ?? 0;
            deleted = diff?.Deleted ?? 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var graphics = args.Graphics;
            using (var pen = new Pen(DesktopStyle.Border))
            {
                graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            if (file == null)
            {
                return;
            }

            var parts = Measure(graphics);
            if (counts)
            {
                TextRenderer.DrawText(graphics, DeletedText, Font, parts.Deleted, DiffStyle.DeletedCount, flags);
                TextRenderer.DrawText(graphics, AddedText, Font, parts.Added, DiffStyle.AddedCount, flags);
            }

            ChangeRows.Draw(graphics, this, Font, parts.File, file, DesktopStyle.Text, DesktopStyle.SecondaryText, false);
        }

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        string AddedText => $"+{added:N0}";

        string DeletedText => $"−{deleted:N0}";

        /// <summary>
        /// Where the counts are, right of the file, and the room left for the file, its status
        /// icon at the room's right. The counts are empty when there are none.
        /// </summary>
        HeaderParts Measure(IDeviceContext graphics)
        {
            var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
            var right = Width - Padding.Right;
            foreach (Control control in Controls)
            {
                if (control.Visible)
                {
                    right = Math.Min(right, control.Left);
                }
            }

            var room = Rectangle.FromLTRB(inset, 0, right - inset, Height - 1);
            if (!counts)
            {
                return new(room, Rectangle.Empty, Rectangle.Empty);
            }

            var gap = LogicalToDeviceUnits(6);
            var removedWidth = TextRenderer.MeasureText(graphics, DeletedText, Font, Size.Empty, flags).Width;
            var plusWidth = TextRenderer.MeasureText(graphics, AddedText, Font, Size.Empty, flags).Width;
            var removedLeft = room.Right - removedWidth;
            var plusLeft = removedLeft - gap - plusWidth;
            return new(
                Rectangle.FromLTRB(room.Left, room.Top, plusLeft - gap * 2, room.Bottom),
                new(plusLeft, room.Top, plusWidth, room.Height),
                new(removedLeft, room.Top, removedWidth, room.Height));
        }

        /// <summary>
        /// The tooltip at a point of the header, as GitHub Desktop has one: what the status icon
        /// stands for over it, how many lines were added or deleted over each count, and the file
        /// and what happened to it over its name, which may be cut short. Null elsewhere.
        /// </summary>
        internal string? TipAt(Point point)
        {
            if (file == null)
            {
                return null;
            }

            using var graphics = CreateGraphics();
            var parts = Measure(graphics);
            if (parts.Added.Contains(point))
            {
                return Lines(added, "added");
            }

            if (parts.Deleted.Contains(point))
            {
                return Lines(deleted, "deleted");
            }

            if (!parts.File.Contains(point))
            {
                return null;
            }

            // The icon, and the gap between it and the counts.
            if (point.X >= ChangeRows.StatusBounds(this, parts.File).Left)
            {
                return ChangeRows.Meaning(file);
            }

            return ChangeRows.Describe(file);
        }

        static string Lines(int count, string what)
        {
            if (count == 1)
            {
                return $"1 line {what}";
            }

            return $"{count:N0} lines {what}";
        }

        record struct HeaderParts(Rectangle File, Rectangle Added, Rectangle Deleted);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
                headerTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// What is said instead of a diff, in the middle, wrapping to fit, with a button beneath when
    /// there is one to press.
    /// </summary>
    sealed class MessagePanel : Panel
    {
        // Its last line just above the middle, the button just below it.
        Label label = new()
        {
            AutoSize = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.BottomCenter,
            ForeColor = DesktopStyle.SecondaryText
        };

        AccentButton button = new()
        {
            Visible = false,
            Height = 28
        };

        public event Action? ButtonClicked;

        public MessagePanel()
        {
            BackColor = DiffStyle.Background;
            button.Click += (_, _) => ButtonClicked?.Invoke();
            Controls.Add(label);
            Controls.Add(button);
        }

        public void Show(string text, string? buttonText)
        {
            Text = text;
            label.Text = text;
            button.Visible = buttonText != null;
            button.Text = buttonText ?? "";
            Place();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Place();
        }

        void Place()
        {
            var inset = LogicalToDeviceUnits(24);
            label.Bounds = Rectangle.FromLTRB(inset, inset, Math.Max(inset, Width - inset), Math.Max(inset, Height / 2 + LogicalToDeviceUnits(8)));
            var width = Math.Max(LogicalToDeviceUnits(160), TextRenderer.MeasureText(button.Text, button.Font).Width + LogicalToDeviceUnits(32));
            button.SetBounds((Width - width) / 2, Height / 2 + LogicalToDeviceUnits(20), width, LogicalToDeviceUnits(28));
        }
    }
}
