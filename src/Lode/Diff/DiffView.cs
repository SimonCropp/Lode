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

    /// <summary>
    /// Reads a file's diff. Tests answer for git.
    /// </summary>
    internal static Func<DiffSource, bool, bool, Cancel, FileDiff> Read { get; set; } = GitFileDiff.Read;

    /// <summary>
    /// Reads the lines of the file a diff is of, to show more of it. Tests answer for git.
    /// </summary>
    internal static Func<DiffSource, IReadOnlyList<string>?> ReadNewLines { get; set; } = GitFileDiff.NewLines;

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
        header.Visible = false;
        ShowOnly(message);
        message.Show(text, null);
        Reading = Task.CompletedTask;
    }

    async Task ReadAsync(DiffSource file, bool keepPlace, bool evenIfLarge)
    {
        var asked = ++version;
        var hideWhitespace = settings.DiffHideWhitespace;
        var (diff, lines) = await Task.Run(() =>
        {
            var read = Read(file, hideWhitespace, evenIfLarge, Cancel.None);
            IReadOnlyList<string>? newLines = null;
            if (read is TextDiff)
            {
                newLines = ReadNewLines(file);
            }

            return (read, newLines);
        });
        if (asked != version ||
            IsDisposed)
        {
            return;
        }

        Present(file, diff, lines, keepPlace, hideWhitespace);
    }

    void Present(DiffSource file, FileDiff diff, IReadOnlyList<string>? lines, bool keepPlace, bool hideWhitespace)
    {
        shown = diff;
        header.Show(file.File, diff as TextDiff);
        header.Visible = true;
        switch (diff)
        {
            case TextDiff text:
                var (picks, hint) = PicksFor(file, text, hideWhitespace);
                canvas.Show(text, lines, settings.DiffSplit, picks, hint, keepPlace);
                ShowOnly(canvas);
                return;
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

        if (file.File.Kind == GitChange.Renamed)
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
            var parsed = DiffParser.Parse(large.Patch, evenIfLarge: true);
            var file = source;
            var hideWhitespace = settings.DiffHideWhitespace;
            var asked = ++version;
            Reading = Task.Run(() => ReadNewLines(file))
                .ContinueWith(
                    _ =>
                    {
                        if (asked == version &&
                            !IsDisposed)
                        {
                            Present(file, parsed, _.Result, keepPlace: false, hideWhitespace);
                        }
                    },
                    TaskScheduler.FromCurrentSynchronizationContext());
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
    }

    // A tick clicked in the Changes pane: the file's lines all picked, or none.
    void OnDraftPicked(string repo, string path)
    {
        if (source is not { IsUncommitted: true } file ||
            shown is not TextDiff diff ||
            canvas.Picks == null ||
            !Locations.Same(repo, file.Repo) ||
            path != file.File.Path)
        {
            return;
        }

        canvas.SetPicks(CommitDrafts.PicksOf(repo, path, diff));
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

            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
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
            if (counts)
            {
                var removed = $"−{deleted:N0}";
                var plus = $"+{added:N0}";
                var gap = LogicalToDeviceUnits(6);
                var removedWidth = TextRenderer.MeasureText(graphics, removed, Font, Size.Empty, flags).Width;
                var plusWidth = TextRenderer.MeasureText(graphics, plus, Font, Size.Empty, flags).Width;
                var removedLeft = room.Right - removedWidth;
                var plusLeft = removedLeft - gap - plusWidth;
                TextRenderer.DrawText(graphics, removed, Font, new Rectangle(removedLeft, room.Top, removedWidth, room.Height), DiffStyle.DeletedCount, flags);
                TextRenderer.DrawText(graphics, plus, Font, new Rectangle(plusLeft, room.Top, plusWidth, room.Height), DiffStyle.AddedCount, flags);
                room = Rectangle.FromLTRB(room.Left, room.Top, plusLeft - gap * 2, room.Bottom);
            }

            ChangeRows.Draw(graphics, this, Font, room, file, DesktopStyle.Text, DesktopStyle.SecondaryText, false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// What is said instead of a diff, in the middle, with a button beneath when there is one to
    /// press.
    /// </summary>
    sealed class MessagePanel : Panel
    {
        AccentButton button = new()
        {
            Visible = false,
            Height = 28
        };

        public event Action? ButtonClicked;

        public MessagePanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DiffStyle.Background;
            button.Click += (_, _) => ButtonClicked?.Invoke();
            Controls.Add(button);
        }

        public void Show(string text, string? buttonText)
        {
            Text = text;
            button.Visible = buttonText != null;
            button.Text = buttonText ?? "";
            PlaceButton();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            PlaceButton();
        }

        void PlaceButton()
        {
            var width = Math.Max(LogicalToDeviceUnits(160), TextRenderer.MeasureText(button.Text, button.Font).Width + LogicalToDeviceUnits(32));
            button.SetBounds((Width - width) / 2, Height / 2 + LogicalToDeviceUnits(20), width, LogicalToDeviceUnits(28));
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom;
            var inset = LogicalToDeviceUnits(24);
            var bounds = Rectangle.FromLTRB(inset, inset, Width - inset, Height / 2 + LogicalToDeviceUnits(8));
            TextRenderer.DrawText(args.Graphics, Text, Font, bounds, DesktopStyle.SecondaryText, flags);
        }
    }
}
