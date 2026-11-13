using System.Windows.Forms.VisualStyles;

/// <summary>
/// The address bar as Explorer shows it: each folder up the path, with chevrons between.
/// Clicking a folder goes there. Clicking anywhere else in the bar, or Ctrl+L, swaps in a text
/// box for typing a path; Enter goes there, Escape or leaving the box swaps the folders back.
/// The chevrons are only separators, with no menus. Drawn rather than built from buttons, so
/// it looks like the text box it turns into, with the folders on its text line.
/// </summary>
sealed class Breadcrumb : Control
{
    const string chevron = "";
    const string ellipsis = "…";

    // Explorer's hover and pressed colours for its address bar items.
    static readonly Color hoverColor = Color.FromArgb(229, 243, 255);
    static readonly Color pressedColor = Color.FromArgb(204, 232, 255);

    readonly Font chevronFont = new("Segoe MDL2 Assets", 7f);
    readonly ToolTip toolTip = new();
    string path = Locations.ThisPC;
    List<(string Path, string Name)> segments = [];

    // Where each shown folder is drawn, and the chevron after it. Hidden folders (the bar too
    // narrow) are left out, and an ellipsis drawn first in their place.
    List<(int Index, Rectangle Bounds)> layout = [];
    Rectangle ellipsisBounds;
    int hovered = -1;
    int pressed = -1;

    /// <summary>
    /// The box for typing a path, shown while editing. Borderless and centred on the bar's text
    /// line, inside the border the bar draws, so its text sits where the folder names were.
    /// MainForm handles its keys.
    /// </summary>
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None,
        Visible = false
    };

    /// <summary>
    /// A folder was clicked.
    /// </summary>
    public event Action<string>? PathClicked;

    public Breadcrumb()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Box.Leave += (_, _) => EndEdit();
        Controls.Add(Box);
        FitHeight();
        Rebuild();
    }

    public bool Editing => Box.Visible;

    /// <summary>
    /// The folder shown. Setting it redraws the folders and, unless editing, the box's text.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Path
    {
        get => path;
        set
        {
            path = value;
            if (!Editing)
            {
                Box.Text = value;
            }

            Rebuild();
        }
    }

    /// <summary>
    /// The folders from the top of <paramref name="location"/> down to it, with the name each
    /// shows. Drives show without their trailing backslash.
    /// </summary>
    internal static List<(string Path, string Name)> Segments(string location)
    {
        var segments = new List<(string, string)>();
        for (var current = location; current != null; current = Locations.Parent(current))
        {
            segments.Add((current, SegmentName(current)));
        }

        segments.Reverse();
        return segments;
    }

    static string SegmentName(string location)
    {
        var name = Locations.DisplayName(location);
        if (name.Length > 1 && name.EndsWith(System.IO.Path.DirectorySeparatorChar) && !Locations.IsNetwork(name))
        {
            return name.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        }

        return name;
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

    // As tall as a text box with a border, so the bar matches the filter box beside it.
    void FitHeight()
    {
        using var probe = new TextBox
        {
            Font = Font
        };
        Height = probe.PreferredHeight;
    }

    // Centred on the text line, starting where the first folder name does.
    void PlaceBox()
    {
        var inner = Inner;
        var left = inner.Left + ItemPadding - LogicalToDeviceUnits(1);
        Box.SetBounds(
            left,
            (ClientSize.Height - Box.PreferredHeight) / 2,
            Math.Max(0, inner.Right - left - ItemPadding),
            Box.PreferredHeight);
    }

    protected override void OnFontChanged(EventArgs eventArgs)
    {
        base.OnFontChanged(eventArgs);
        FitHeight();
        Rebuild();
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        PlaceBox();
        Arrange();
    }

    void Rebuild()
    {
        segments = Segments(path);
        hovered = -1;
        pressed = -1;
        Arrange();
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
        var x = inner.Left;
        ellipsisBounds = Rectangle.Empty;
        if (first > 0)
        {
            ellipsisBounds = new(x, inner.Top, ellipsisWidth - ChevronWidth, inner.Height);
            x += ellipsisWidth;
        }

        for (var index = first; index < segments.Count; index++)
        {
            layout.Add((index, new(x, inner.Top, widths[index], inner.Height)));
            x += widths[index] + ChevronWidth;
        }

        Invalidate();
    }

    int IconSize => LogicalToDeviceUnits(16);

    // This PC shows as an icon alone, as in Explorer; folders show their names.
    int ContentWidth(string location, string name)
    {
        if (Locations.IsThisPC(location))
        {
            return IconSize;
        }

        return TextWidth(name);
    }

    int Total(List<int> widths, int first) =>
        widths.Skip(first).Sum() + ChevronWidth * (widths.Count - first - 1);

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.Clear(SystemColors.Window);
        DrawBorder(graphics);
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
            TextRenderer.DrawText(graphics, ellipsis, Font, ellipsisBounds, SystemColors.GrayText, flags);
            DrawChevron(graphics, ellipsisBounds);
        }

        for (var position = 0; position < layout.Count; position++)
        {
            var (index, bounds) = layout[position];
            if (index == pressed || index == hovered)
            {
                using var brush = new SolidBrush(index == pressed ? pressedColor : hoverColor);
                graphics.FillRectangle(brush, bounds);
            }

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
            else
            {
                TextRenderer.DrawText(graphics, segments[index].Name, Font, bounds, SystemColors.WindowText, flags);
            }

            if (position < layout.Count - 1)
            {
                DrawChevron(graphics, bounds);
            }
        }
    }

    // In the gap after an item.
    void DrawChevron(Graphics graphics, Rectangle after)
    {
        var bounds = new Rectangle(after.Right, after.Top, ChevronWidth, after.Height);
        TextRenderer.DrawText(
            graphics,
            chevron,
            chevronFont,
            bounds,
            SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // The same border the filter box beside it has.
    void DrawBorder(Graphics graphics)
    {
        // Focused while typing, which Windows 11 shows with an accent line along the bottom, as
        // the filter box has when it has focus.
        var element = Editing ? VisualStyleElement.TextBox.TextEdit.Focused : VisualStyleElement.TextBox.TextEdit.Normal;
        if (Application.RenderWithVisualStyles &&
            VisualStyleRenderer.IsElementDefined(element))
        {
            new VisualStyleRenderer(element).DrawBackground(graphics, ClientRectangle);
            return;
        }

        ControlPaint.DrawBorder3D(graphics, ClientRectangle, Border3DStyle.Sunken);
    }

    int IndexAt(Point point)
    {
        foreach (var (index, bounds) in layout)
        {
            if (bounds.Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        var index = IndexAt(eventArgs.Location);
        if (index != hovered)
        {
            hovered = index;
            // The icon-only This PC gets its name as a tooltip.
            toolTip.SetToolTip(this, index >= 0 && Locations.IsThisPC(segments[index].Path) ? Locations.ThisPC : null);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        hovered = -1;
        pressed = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        pressed = IndexAt(eventArgs.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        var index = IndexAt(eventArgs.Location);
        var wasPressed = pressed;
        pressed = -1;
        Invalidate();
        if (index >= 0 &&
            index == wasPressed)
        {
            PathClicked?.Invoke(segments[index].Path);
            return;
        }

        // Anywhere but a folder, the ellipsis included, starts typing a path.
        if (wasPressed < 0)
        {
            BeginEdit();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            chevronFont.Dispose();
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
