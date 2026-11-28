/// <summary>
/// The address bar as Explorer shows it: each folder up the path, with chevrons between.
/// Clicking a folder goes there; clicking it with the middle button, or with Ctrl held, opens it
/// in a new tab, and right-clicking it offers Copy address and Open in new tab. Clicking a
/// chevron drops down the folders in the folder before it, and clicking the ellipsis, which
/// stands for the folders nearest the top when the bar has no room for them, drops those down;
/// picking one goes there. Clicking anywhere else in the bar, or Ctrl+L, swaps in a text box for
/// typing a path; Enter goes there, Escape or leaving the box swaps the folders back. Drawn
/// rather than built from buttons, so it looks like the text box it turns into, with the
/// folders on its text line.
/// </summary>
sealed class Breadcrumb : Control
{
    const string chevron = "\uE76C";

    // A chevron whose folders are dropped down points down at them, as Explorer's does.
    const string openChevron = "\uE70D";
    const string ellipsis = "…";

    Settings settings;
    Font chevronFont = ChevronFontFor(DefaultFont);
    ToolTip toolTip = new();
    string path = Locations.ThisPC;
    List<(string Path, string Name)> segments = [];

    // Where each shown folder is drawn, and the chevron after it, with the folder whose folders
    // it lists. Hidden folders (the bar too narrow) are left out, and an ellipsis drawn first in
    // their place, with a chevron after it for the nearest of them.
    List<(int Index, Rectangle Bounds)> layout = [];
    List<(int Index, Rectangle Bounds)> chevrons = [];
    Rectangle ellipsisBounds;
    Part hovered = Part.None;
    Part pressed = Part.None;
    Part middlePressed = Part.None;

    // The chevron or the ellipsis whose menu is open, drawn pressed until the menu closes.
    Part open = Part.None;
    ToolStripDropDown? menu;

    // The chevron or the ellipsis whose menu a press closed, which the same press, reaching the
    // bar once the menu has gone, then leaves closed rather than opening it again.
    Part dismissed = Part.None;

    // Whether Ctrl, and not Shift, was held as the left button came up, as the tree takes it.
    bool controlClicked;

    /// <summary>
    /// The box for typing a path, shown while editing. Borderless and centred on the bar's text
    /// line, inside the border the bar draws, so its text sits where the folder names were.
    /// MainForm handles its keys.
    /// </summary>
    public FrameTextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None,
        Visible = false
    };

    /// <summary>
    /// A folder was clicked, or picked from a chevron's or the ellipsis' menu.
    /// </summary>
    public event Action<string>? PathClicked;

    /// <summary>
    /// A folder was clicked with the middle button or with Ctrl held, or Open in new tab picked
    /// from its right-click menu.
    /// </summary>
    public event Action<string>? NewTabRequested;

    /// <param name="settings">Whether hidden folders, and Windows' own, are among those a
    /// chevron lists, as the list shows them.</param>
    public Breadcrumb(Settings settings)
    {
        this.settings = settings;
        ShowMenu = (dropDown, location) => dropDown.Show(this, location);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Box.Leave += (_, _) => EndEdit();
        Controls.Add(Box);
        Height = TextFrame.Height(Font);
        Rebuild();
    }

    public bool Editing => Box.Visible;

    /// <summary>
    /// What the chevrons are drawn in, for tests.
    /// </summary>
    internal Font ChevronFont => chevronFont;

    // The chevrons' glyphs, two thirds the size of the folders' names: 7 points beside the
    // window's 10.5. Made from the bar's font, which the window scales for the monitor it is on,
    // as it does every font in it, rather than a size of their own: Windows draws a font's points
    // at the scale it started at, whatever monitor the window is on.
    static Font ChevronFontFor(Font font) =>
        new("Segoe MDL2 Assets", font.SizeInPoints * 2 / 3);

    /// <summary>
    /// The folder shown. Setting it redraws the folders and, unless editing, the box's text.
    /// Somewhere else, a menu of the folders on the way to where it was closes.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Path
    {
        get => path;
        set
        {
            if (!Locations.Same(path, value))
            {
                menu?.Close();
                open = Part.None;
            }

            path = value;
            if (!Editing)
            {
                Box.Text = value;
            }

            Rebuild();
        }
    }

    /// <summary>
    /// What a drive shows as: its label with its letter, "Windows (C:)", as Windows names it
    /// and Explorer shows it, or its letter alone when Windows has no name for it. Tests pin it,
    /// so captures don't depend on what the drives of the machine running them are called. A
    /// drive is named off the UI thread, and the second argument is called once it has been.
    /// </summary>
    internal static Func<string, Action, string> DriveName { get; set; } = (drive, described) =>
    {
        var name = ShellIcons.DescribeDrive(drive, described).DisplayName;
        if (name == drive)
        {
            return drive.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }

        return name;
    };

    /// <summary>
    /// Reads what a chevron lists, off the UI thread: see <see cref="FoldersIn"/>. Tests wrap
    /// it, to see where it runs, and to hold it while they look at the menu meanwhile.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool, SystemItems?, List<string>> ReadFolders { get; set; } = FoldersIn;

    /// <summary>
    /// Shows a menu of the bar's at a point in it. Tests swap it, to show the menu where it
    /// can't be seen.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<ToolStripDropDown, Point> ShowMenu { get; set; }

    /// <summary>
    /// The folders from the top of <paramref name="location"/> down to it, with the name each
    /// shows. A drive shows as <paramref name="driveName"/> names it, and without one, as its
    /// letter alone. Somewhere on the network starts at the machine it is on, as in Explorer,
    /// rather than at This PC.
    /// </summary>
    internal static List<(string Path, string Name)> Segments(string location, Func<string, string>? driveName = null)
    {
        var segments = new List<(string, string)>();
        for (var current = location; current != null; current = Locations.Parent(current))
        {
            segments.Add((current, SegmentName(current, driveName)));
            if (Locations.IsServer(current))
            {
                break;
            }
        }

        segments.Reverse();
        return segments;
    }

    static string SegmentName(string location, Func<string, string>? driveName)
    {
        // Its machine is the segment before it, so a share needs only its own name.
        if (Locations.IsShare(location))
        {
            return Locations.ShareName(location);
        }

        var name = Locations.DisplayName(location);
        if (name.Length > 1 &&
            name.EndsWith(System.IO.Path.DirectorySeparatorChar) &&
            !Locations.IsNetwork(name))
        {
            if (driveName != null)
            {
                return driveName(location);
            }

            return name.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }

        return name;
    }

    /// <summary>
    /// What the chevron after <paramref name="location"/> lists: This PC's drives, the folders a
    /// machine on the network shares, or the folders in a folder or in a zip. Each by its name as
    /// the list sorts names, numbers as numbers, but the drives, in their letters' order. Hidden
    /// ones only with <paramref name="showHidden"/>, and Windows' own only when
    /// <paramref name="hiddenSystem"/> is null, as the list and the tree show them. Never on the
    /// UI thread: a share or a drive can be slow to answer, and finding the folders in a folder
    /// means going through everything in it. Throws when the folder can't be read.
    /// </summary>
    internal static List<string> FoldersIn(string location, bool showHidden, SystemItems? hiddenSystem)
    {
        if (Locations.IsThisPC(location))
        {
            return DriveInfo.GetDrives().Select(_ => _.Name).ToList();
        }

        List<(string Name, string FullPath)> found;
        if (Locations.IsServer(location))
        {
            found = FolderTree.ListShares(location)
                .Where(_ => showHidden || !_.Hidden)
                .Select(_ => (_.Name, System.IO.Path.Join(location, _.Name)))
                .ToList();
        }
        else if (Zip.Split(location) is { } inArchive)
        {
            found = Zip.List(inArchive, location)
                .Where(_ => _.IsDirectory)
                .Select(_ => (_.Name, _.FullPath))
                .ToList();
        }
        else
        {
            // A folder Lode is denied would read as having none, as the options pass over what
            // can't be read: asked first, it says so instead, as the list does.
            _ = Directory.EnumerateFileSystemEntries(location).Any();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = showHidden ? default : FileAttributes.Hidden
            };
            found = FolderTree.FindSubfolders(location, options, hiddenSystem).ToList();
        }

        found.Sort((first, second) => StrCmpLogicalW(first.Name, second.Name));
        return found.Select(_ => _.FullPath).ToList();
    }

    public void BeginEdit()
    {
        Box.Text = path;
        PlaceBox();
        Box.Visible = true;
        Invalidate();
        Box.Focus();
        Box.SelectAll();
    }

    public void EndEdit()
    {
        if (!Editing)
        {
            return;
        }

        Box.Visible = false;
        Box.Text = path;
        Invalidate();
    }


    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        PlaceBox();
    }

    // On a display above 100% the form scales its controls after they are built, and again when
    // the window moves to a display of another scale. Scaled, the frame would be taller than its
    // font, which is already at the display's scale, needs; it keeps a text box's height instead,
    // and puts the box, which ignores scaling, back on its text line.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        Height = TextFrame.Height(Font);
        PlaceBox();
    }

    // Centred on the text line, starting where the first folder name does.
    void PlaceBox()
    {
        var inner = Inner;
        var left = TextFrame.Inset(this);
        Box.SetBounds(
            left,
            (ClientSize.Height - Box.PreferredHeight) / 2,
            Math.Max(0, inner.Right - left - ItemPadding),
            Box.PreferredHeight);
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        chevronFont.Dispose();
        chevronFont = ChevronFontFor(Font);
        Height = TextFrame.Height(Font);
        Rebuild();
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        PlaceBox();
        Arrange();
    }

    void Rebuild()
    {
        segments = Segments(path, drive => DriveName(drive, DriveDescribed));
        hovered = Part.None;
        pressed = Part.None;
        middlePressed = Part.None;
        Arrange();
    }

    void DriveDescribed()
    {
        if (!IsDisposed)
        {
            Rebuild();
        }
    }

    int ItemPadding => LogicalToDeviceUnits(5);
    int ChevronWidth => LogicalToDeviceUnits(16);

    int TextWidth(string text) =>
        TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;

    Rectangle Inner => Rectangle.Inflate(ClientRectangle, -LogicalToDeviceUnits(2), -LogicalToDeviceUnits(2));

    // Lays the folders out from the left. When they do not fit, the ones nearest the top are
    // dropped, so the current folder always shows.
    void Arrange()
    {
        var inner = Inner;
        var widths = segments.Select(_ => ContentWidth(_.Path, _.Name) + ItemPadding * 2).ToList();
        var first = 0;
        var ellipsisWidth = TextWidth(ellipsis) + ItemPadding * 2 + ChevronWidth;
        while (first < segments.Count - 1 &&
               Total(widths, first) + (first > 0 ? ellipsisWidth : 0) > inner.Width)
        {
            first++;
        }

        layout = [];
        chevrons = [];
        var x = inner.Left;
        ellipsisBounds = Rectangle.Empty;
        if (first > 0)
        {
            ellipsisBounds = new(x, inner.Top, ellipsisWidth - ChevronWidth, inner.Height);
            chevrons.Add((first - 1, ChevronAfter(ellipsisBounds)));
            x += ellipsisWidth;
        }

        for (var index = first; index < segments.Count; index++)
        {
            var bounds = new Rectangle(x, inner.Top, widths[index], inner.Height);
            layout.Add((index, bounds));
            if (index < segments.Count - 1)
            {
                chevrons.Add((index, ChevronAfter(bounds)));
            }

            x += widths[index] + ChevronWidth;
        }

        Invalidate();
    }

    // In the gap after an item.
    Rectangle ChevronAfter(Rectangle item) =>
        new(item.Right, item.Top, ChevronWidth, item.Height);

    int IconSize => LogicalToDeviceUnits(16);

    int IconGap => LogicalToDeviceUnits(6);

    // This PC shows as an icon alone, as in Explorer; a machine on the network as an icon with
    // its name; folders show their names.
    int ContentWidth(string location, string name)
    {
        if (Locations.IsThisPC(location))
        {
            return IconSize;
        }

        if (Locations.IsServer(location))
        {
            return IconSize + IconGap + TextWidth(name);
        }

        return TextWidth(name);
    }

    int Total(List<int> widths, int first) =>
        widths.Skip(first).Sum() + ChevronWidth * (widths.Count - first - 1);

    /// <summary>
    /// Where <paramref name="location"/>'s folder is drawn, or empty when it isn't shown. For
    /// tests, to click it.
    /// </summary>
    internal Rectangle FolderBounds(string location) =>
        layout
            .Where(_ => Locations.Same(segments[_.Index].Path, location))
            .Select(_ => _.Bounds)
            .FirstOrDefault();

    /// <summary>
    /// Where the chevron that lists the folders in <paramref name="location"/> is drawn, or
    /// empty when there is none. For tests, to click it.
    /// </summary>
    internal Rectangle ChevronBounds(string location) =>
        chevrons
            .Where(_ => Locations.Same(segments[_.Index].Path, location))
            .Select(_ => _.Bounds)
            .FirstOrDefault();

    /// <summary>
    /// Where the ellipsis is drawn, or empty when every folder fits. For tests, to click it.
    /// </summary>
    internal Rectangle EllipsisBounds => ellipsisBounds;

    /// <summary>
    /// The chevron, or the ellipsis, whose menu is open, by the folder whose folders it lists,
    /// the ellipsis' as "…"; null with none open. For tests.
    /// </summary>
    internal string? MenuOpenFor =>
        open.Kind switch
        {
            PartKind.Chevron => segments[open.Index].Path,
            PartKind.Ellipsis => ellipsis,
            _ => null
        };

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        TextFrame.Draw(graphics, this, Editing);
        if (Editing)
        {
            return;
        }

        const TextFormatFlags flags = TextFormatFlags.NoPrefix |
                                      TextFormatFlags.SingleLine |
                                      TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.HorizontalCenter |
                                      TextFormatFlags.EndEllipsis;
        if (!ellipsisBounds.IsEmpty)
        {
            Shade(graphics, Part.Ellipsis, ellipsisBounds);
            TextRenderer.DrawText(graphics, ellipsis, Font, ellipsisBounds, SystemColors.GrayText, flags);
        }

        foreach (var (index, bounds) in layout)
        {
            Shade(graphics, new(PartKind.Folder, index), bounds);
            if (Locations.IsThisPC(segments[index].Path))
            {
                var image = Icons.Bitmap(Icons.ThisPC, IconSize, SystemColors.WindowText);
                graphics.DrawImage(
                    image,
                    bounds.Left + (bounds.Width - IconSize) / 2,
                    bounds.Top + (bounds.Height - IconSize) / 2,
                    IconSize,
                    IconSize);
            }
            else if (Locations.IsServer(segments[index].Path))
            {
                var left = bounds.Left + ItemPadding;
                var image = Icons.Bitmap(Icons.Network, IconSize, SystemColors.WindowText);
                graphics.DrawImage(image, left, bounds.Top + (bounds.Height - IconSize) / 2, IconSize, IconSize);
                var text = Rectangle.FromLTRB(left + IconSize + IconGap, bounds.Top, bounds.Right - ItemPadding, bounds.Bottom);
                TextRenderer.DrawText(
                    graphics,
                    segments[index].Name,
                    Font,
                    text,
                    SystemColors.WindowText,
                    (flags & ~TextFormatFlags.HorizontalCenter) | TextFormatFlags.NoPadding);
            }
            else
            {
                TextRenderer.DrawText(graphics, segments[index].Name, Font, bounds, SystemColors.WindowText, flags);
            }
        }

        foreach (var (index, bounds) in chevrons)
        {
            var part = new Part(PartKind.Chevron, index);
            Shade(graphics, part, bounds);
            var glyph = chevron;
            if (part == open)
            {
                glyph = openChevron;
            }

            TextRenderer.DrawText(
                graphics,
                glyph,
                chevronFont,
                bounds,
                SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Windows 11's shades of gray, its corners rounded, in place of Windows 10's blue: a shade
    // under the mouse, and a lighter one while pressed, or while the menu it opened is open.
    void Shade(Graphics graphics, Part part, Rectangle bounds)
    {
        Color shade;
        if (part == pressed ||
            part == open)
        {
            shade = ExplorerStyle.Pressed(ExplorerStyle.Window);
        }
        else if (part == hovered)
        {
            shade = ExplorerStyle.Hover(ExplorerStyle.Window);
        }
        else
        {
            return;
        }

        using var brush = new SolidBrush(shade);
        using var shape = ExplorerStyle.RoundedRectangle(bounds, LogicalToDeviceUnits(4));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(brush, shape);
        graphics.SmoothingMode = SmoothingMode.Default;
    }

    // What is at a point of the bar: a folder, by where it is in the path, the chevron after
    // one, which lists the folders in it, or the ellipsis, which lists those the bar has no room
    // for.
    enum PartKind
    {
        None,
        Folder,
        Chevron,
        Ellipsis
    }

    readonly record struct Part(PartKind Kind, int Index)
    {
        public static Part None { get; } = new(PartKind.None, -1);

        public static Part Ellipsis { get; } = new(PartKind.Ellipsis, -1);
    }

    Part PartAt(Point point)
    {
        foreach (var (index, bounds) in layout)
        {
            if (bounds.Contains(point))
            {
                return new(PartKind.Folder, index);
            }
        }

        foreach (var (index, bounds) in chevrons)
        {
            if (bounds.Contains(point))
            {
                return new(PartKind.Chevron, index);
            }
        }

        if (ellipsisBounds.Contains(point))
        {
            return Part.Ellipsis;
        }

        return Part.None;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var part = PartAt(args.Location);
        if (part != hovered)
        {
            hovered = part;
            // The icon-only This PC gets its name as a tooltip.
            toolTip.SetToolTip(
                this,
                part.Kind == PartKind.Folder &&
                Locations.IsThisPC(segments[part.Index].Path)
                    ? Locations.ThisPC
                    : null);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        hovered = Part.None;
        pressed = Part.None;
        middlePressed = Part.None;
        Invalidate();
    }

    // WM_LBUTTONUP, and the MK_CONTROL and MK_SHIFT its WPARAM has for the keys held.
    const int leftButtonUp = 0x0202;
    const int controlHeld = 0x0008;
    const int shiftHeld = 0x0004;

    /// <summary>
    /// Notes the keys held as the left button came up, as the click's own message has them,
    /// rather than as the keyboard is by the time the click is handled.
    /// </summary>
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == leftButtonUp)
        {
            var keys = (int) message.WParam;
            controlClicked = (keys & controlHeld) != 0 &&
                             (keys & shiftHeld) == 0;
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        var part = PartAt(args.Location);
        if (args.Button == MouseButtons.Middle)
        {
            middlePressed = part;
            return;
        }

        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        pressed = part;
        Invalidate();
        if (dismissed.Kind != PartKind.None &&
            part == dismissed)
        {
            dismissed = Part.None;
            return;
        }

        // As it is pressed, as a menu opens.
        if (part.Kind == PartKind.Chevron)
        {
            ShowFolders(part);
        }
        else if (part.Kind == PartKind.Ellipsis)
        {
            ShowHidden();
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        var part = PartAt(args.Location);
        if (args.Button == MouseButtons.Middle)
        {
            var wasMiddlePressed = middlePressed;
            middlePressed = Part.None;
            if (part.Kind == PartKind.Folder &&
                part == wasMiddlePressed)
            {
                NewTabRequested?.Invoke(segments[part.Index].Path);
            }

            return;
        }

        if (args.Button == MouseButtons.Right)
        {
            if (part.Kind == PartKind.Folder)
            {
                ShowFolderMenu(segments[part.Index].Path, args.Location);
            }

            return;
        }

        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        var wasPressed = pressed;
        pressed = Part.None;
        Invalidate();

        // Anywhere but a folder, a chevron or the ellipsis starts typing a path.
        if (wasPressed.Kind == PartKind.None)
        {
            BeginEdit();
            return;
        }

        if (wasPressed.Kind != PartKind.Folder ||
            part != wasPressed)
        {
            return;
        }

        var location = segments[part.Index].Path;
        if (controlClicked)
        {
            NewTabRequested?.Invoke(location);
            return;
        }

        PathClicked?.Invoke(location);
    }

    /// <summary>
    /// What right-clicking <paramref name="location"/>'s folder in the bar offers: its path
    /// copied, as Explorer's Copy address copies it, and Open in new tab.
    /// </summary>
    internal List<QuickMenu.Command?> FolderCommands(string location) =>
    [
        new("Copy address", () => FolderView.SetClipboardText(location))
        {
            Glyph = Icons.Copy
        },
        LodeActions.Command("openInNewTab", () => NewTabRequested?.Invoke(location))
    ];

    // At the pointer. What is picked runs once the menu has closed.
    void ShowFolderMenu(string location, Point point)
    {
        var strip = QuickMenu.Build(FolderCommands(location), null, _ => BeginInvoke(_));
        strip.Closed += (_, _) => strip.BeginInvoke(strip.Dispose);
        ShowMenu(strip, point);
    }

    // Drops down the folders in the folder before the chevron. They are read off the UI thread,
    // so a share or a drive slow to answer leaves the window free meanwhile: the menu says
    // Loading… until they are read, then lists them, or says what went wrong reading them.
    void ShowFolders(Part part)
    {
        var location = segments[part.Index].Path;
        var (dropDown, images) = NewMenu();
        dropDown.Items.Add(Note("Loading…"));
        Open(dropDown, part, chevrons.Single(_ => _.Index == part.Index).Bounds);
        _ = ListFolders(dropDown, images, location);
    }

    async Task ListFolders(ToolStripDropDown dropDown, MenuImages images, string location)
    {
        // By the options as they are as it opens, as the list reads by them as they are then.
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        var read = ReadFolders;
        List<string>? folders = null;
        string? failure = null;
        try
        {
            folders = await Task.Run(() => read(location, showHidden, hiddenSystem));
        }
        catch (Exception exception)
        {
            // A drive not ready, a share offline, a folder Lode is denied or one gone since:
            // said in the menu, as the list says it in place of the folder's items.
            failure = exception.Message;
        }

        // Closed meanwhile.
        if (dropDown.IsDisposed ||
            IsDisposed)
        {
            return;
        }

        List<ToolStripItem> items;
        if (folders == null)
        {
            items = [Note(failure ?? "")];
        }
        else
        {
            items = [.. folders.Select(_ => Item(_, SegmentName(_, drive => DriveName(drive, () => { })), images))];
            if (items.Count == 0)
            {
                items.Add(Note("No folders"));
            }
        }

        dropDown.SuspendLayout();
        var loading = dropDown.Items.Cast<ToolStripItem>().ToList();
        dropDown.Items.Clear();
        foreach (var item in loading)
        {
            item.Dispose();
        }

        dropDown.Items.AddRange(items.ToArray());
        dropDown.ResumeLayout();
    }

    // Drops down the folders the ellipsis stands for, the nearest first.
    void ShowHidden()
    {
        var (dropDown, images) = NewMenu();
        for (var index = layout[0].Index - 1; index >= 0; index--)
        {
            dropDown.Items.Add(Item(segments[index].Path, segments[index].Name, images));
        }

        Open(dropDown, Part.Ellipsis, ellipsisBounds);
    }

    // Under what it drops down from, left edges level, drawn pressed while it is open.
    void Open(ToolStripDropDown dropDown, Part part, Rectangle under)
    {
        menu?.Close();
        menu = dropDown;
        open = part;
        dropDown.Closed += (_, args) => MenuClosed(dropDown, part, args.CloseReason);
        Invalidate();
        ShowMenu(dropDown, new(under.Left, ClientSize.Height));
    }

    void MenuClosed(ToolStripDropDown dropDown, Part part, ToolStripDropDownCloseReason reason)
    {
        // Closed by a press outside it, which, on what opened it, would open it again as the
        // press reaches the bar. Forgotten once that press has been handled, wherever it was.
        if (reason == ToolStripDropDownCloseReason.AppClicked)
        {
            dismissed = part;
            BeginInvoke(() => dismissed = Part.None);
        }

        if (menu == dropDown)
        {
            menu = null;
            open = Part.None;
            Invalidate();
        }

        dropDown.BeginInvoke(dropDown.Dispose);
    }

    // The icons a menu's folders show, each drawn once for the menu, and let go of with it.
    sealed class MenuImages(int size) : IDisposable
    {
        List<Image> made = [];
        Image? folder;

        public int Size => size;

        public Image For(string location)
        {
            if (Locations.IsThisPC(location))
            {
                return Icons.Bitmap(Icons.ThisPC, size, SystemColors.MenuText);
            }

            if (Locations.IsServer(location))
            {
                return Icons.Bitmap(Icons.Network, size, SystemColors.MenuText);
            }

            if (!Locations.IsNetwork(location) &&
                System.IO.Path.GetPathRoot(location) is { } root &&
                Locations.Same(root, location))
            {
                return Made(ShellIcons.DescribeDrive(location, null).Icon);
            }

            return folder ??= Made(ShellIcons.FolderIcon);
        }

        Image Made(int icon)
        {
            var image = ShellIcons.Render(icon, new(size, size), Point.Empty);
            made.Add(image);
            return image;
        }

        public void Dispose()
        {
            foreach (var image in made)
            {
                image.Dispose();
            }
        }
    }

    (ToolStripDropDown DropDown, MenuImages Images) NewMenu()
    {
        var dropDown = new ExplorerMenu();
        var images = new MenuImages(LogicalToDeviceUnits(16));
        dropDown.ImageScalingSize = new(images.Size, images.Size);
        dropDown.Disposed += (_, _) => images.Dispose();
        return (dropDown, images);
    }

    // An entry for going to a folder. What is picked runs once the menu has closed.
    ToolStripMenuItem Item(string location, string name, MenuImages images)
    {
        // An ampersand would mark the next letter as the item's access key and not show: a
        // folder named "R&D".
        var item = new ToolStripMenuItem(name.Replace("&", "&&"), images.For(location));
        item.Click += (_, _) => BeginInvoke(() => PathClicked?.Invoke(location));
        return item;
    }

    // An entry that only says something: that the folders are being read, that there are none,
    // or what went wrong reading them.
    static ToolStripMenuItem Note(string text) =>
        new(text.Replace("&", "&&"))
        {
            Enabled = false
        };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            menu?.Close();
            chevronFont.Dispose();
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
