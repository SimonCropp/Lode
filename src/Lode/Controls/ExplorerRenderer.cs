/// <summary>
/// The command bar, status bar and menus drawn as Explorer's are. The bars are flat white, the
/// command bar between two lines and the status bar with one along its top, and a button on the
/// command bar is shaded under the mouse, its corners rounded. The menus are off white, with
/// rounded corners and roomy rows, and the entry under the mouse is shaded the same way, set in
/// from the menu's edges.
/// </summary>
sealed class ExplorerRenderer : ToolStripSystemRenderer
{
    // Pixels at 100% scale.
    const int cornerRadius = 4;
    const int menuPadding = 4;
    const int rowPadding = 4;
    const int highlightInset = 4;
    const int contentIndent = 8;
    const int arrowSize = 12;
    const int checkSize = 14;

    const int windowCornerPreference = 33;
    const int roundCorners = 2;

    /// <summary>
    /// Gives a menu Explorer's look: this renderer, rounded corners, and roomy rows, both for the
    /// entries in it now and for those added later, which most menus fill in as they open. The
    /// same goes for the submenus of its entries.
    /// </summary>
    public static void Style(ToolStripDropDown menu)
    {
        menu.Renderer = new ExplorerRenderer();
        var vertical = menu.LogicalToDeviceUnits(menuPadding);
        menu.Padding = menu.Padding with
        {
            Top = vertical,
            Bottom = vertical
        };
        menu.HandleCreated += (_, _) => RoundCorners(menu);
        if (menu.IsHandleCreated)
        {
            RoundCorners(menu);
        }

        menu.ItemAdded += (_, eventArgs) =>
        {
            if (eventArgs.Item is { } item)
            {
                StyleItem(menu, item);
            }
        };
        foreach (ToolStripItem item in menu.Items)
        {
            StyleItem(menu, item);
        }
    }

    static void StyleItem(ToolStripDropDown menu, ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return;
        }

        // Taller, and wider by the indent its icon and name are drawn at (see Indented).
        var vertical = menu.LogicalToDeviceUnits(rowPadding);
        item.Padding = new(0, vertical, menu.LogicalToDeviceUnits(contentIndent), vertical);
        if (item is ToolStripDropDownItem { HasDropDown: true } parent)
        {
            Style(parent.DropDown);
        }
    }

    // Windows 11 rounds the corners of a window that asks it to. Earlier Windows ignore it.
    static void RoundCorners(ToolStripDropDown menu)
    {
        var preference = roundCorners;
        DwmSetWindowAttribute(menu.Handle, windowCornerPreference, ref preference, sizeof(int));
    }

    static int Scale(ToolStripItem item, int logical) =>
        item.Owner?.LogicalToDeviceUnits(logical) ?? logical;

    // A menu entry's icon, tick and name are drawn further in than Windows places them, which
    // is hard against the edge of the shade under the mouse, now set in from the menu's edge.
    static Rectangle Indented(ToolStripItem item, Rectangle bounds)
    {
        bounds.Offset(Scale(item, contentIndent), 0);
        return bounds;
    }

    // A menu entry's name and shortcut, on the middle of its row, where its icon, tick and
    // chevron are. Windows Forms centres them on the row's height without the padding that makes
    // the rows roomy (see StyleItem), which left the text riding above the icon beside it.
    static Rectangle CentredOnRow(ToolStripItem item, Rectangle bounds)
    {
        bounds.Y = (item.Height - bounds.Height) / 2;
        return bounds;
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs eventArgs)
    {
        if (eventArgs.Item.IsOnDropDown)
        {
            base.OnRenderItemImage(new(eventArgs.Graphics, eventArgs.Item, eventArgs.Image, Indented(eventArgs.Item, eventArgs.ImageRectangle)));
            return;
        }

        if (!eventArgs.Item.Enabled &&
            eventArgs.Image is { } image)
        {
            DrawDisabled(eventArgs.Graphics, image, eventArgs.ImageRectangle);
            return;
        }

        base.OnRenderItemImage(eventArgs);
    }

    // A command bar icon that does nothing now, in Explorer's light gray. Windows' grayed
    // version of the icon is much darker. Each pixel takes the gray, keeping how opaque it was,
    // so the icon's smoothed edges stay smooth.
    static void DrawDisabled(Graphics graphics, Image image, Rectangle bounds)
    {
        var color = ExplorerStyle.DisabledIcon;
        var matrix = new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1]
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(image, bounds, 0, 0, bounds.Width, bounds.Height, GraphicsUnit.Pixel, attributes);
    }

    static void Shade(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var shape = ExplorerStyle.RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, shape);
        graphics.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
    {
        if (eventArgs.ToolStrip is ToolStripDropDown)
        {
            using var brush = new SolidBrush(ExplorerStyle.MenuBackground);
            eventArgs.Graphics.FillRectangle(brush, eventArgs.AffectedBounds);
            return;
        }

        using var window = new SolidBrush(ExplorerStyle.Window);
        eventArgs.Graphics.FillRectangle(window, eventArgs.AffectedBounds);
    }

    // In place of the raised edge Windows draws round a toolbar, and the frame round a menu,
    // whose border Windows 11 draws round its rounded corners. With high contrast on, the
    // frame, which shows where the menu ends.
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
    {
        var strip = eventArgs.ToolStrip;
        if (strip is ToolStripDropDown)
        {
            if (SystemInformation.HighContrast)
            {
                base.OnRenderToolStripBorder(eventArgs);
            }

            return;
        }

        using var pen = new Pen(ExplorerStyle.Divider);

        // Along the top of the status bar, between it and the panes, which are as white as it is.
        if (strip is StatusStrip)
        {
            eventArgs.Graphics.DrawLine(pen, 0, 0, strip.Width, 0);
            return;
        }

        // Above and below the command bar: between it and the address row, and it and the panes.
        eventArgs.Graphics.DrawLine(pen, 0, 0, strip.Width, 0);
        eventArgs.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        if (!item.IsOnDropDown)
        {
            base.OnRenderMenuItemBackground(eventArgs);
            return;
        }

        if (item is
            {
                Selected: false,
                Pressed: false
            })
        {
            return;
        }

        var inset = Scale(item, highlightInset);
        Shade(
            eventArgs.Graphics,
            Rectangle.FromLTRB(inset, 0, item.Width - inset, item.Height),
            ExplorerStyle.Hover(ExplorerStyle.MenuBackground),
            Scale(item, cornerRadius));
    }

    // The menu's own colours whether or not the entry is under the mouse: Windows' toolbar
    // drawing would have the text of the entry under the mouse in the colour for text on a
    // selection, which the light shade is not.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
    {
        if (eventArgs.Item is { IsOnDropDown: true } item)
        {
            var color = SystemColors.MenuText;
            // A shortcut is drawn as a second piece of text, at the right, where it stays: gray,
            // as Explorer shows them.
            var shortcut = item is ToolStripMenuItem { ShortcutKeyDisplayString.Length: > 0 } menuItem &&
                           eventArgs.Text == menuItem.ShortcutKeyDisplayString;
            if (!item.Enabled)
            {
                color = SystemColors.GrayText;
            }
            else if (shortcut)
            {
                color = ExplorerStyle.SecondaryText;
            }

            if (!shortcut)
            {
                eventArgs.TextRectangle = Indented(item, eventArgs.TextRectangle);
            }

            eventArgs.TextRectangle = CentredOnRow(item, eventArgs.TextRectangle);
            eventArgs.TextColor = color;
        }
        else if (!eventArgs.Item.Enabled)
        {
            // New's name while there is nothing to make, as light as Explorer has it. Drawn
            // here, as Windows' toolbar drawing has its own gray for disabled text.
            TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Text, eventArgs.TextFont, eventArgs.TextRectangle, ExplorerStyle.DisabledText, eventArgs.TextFormat);
            return;
        }

        base.OnRenderItemText(eventArgs);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        if (!item.IsOnDropDown)
        {
            // Down the middle, as tall as the buttons beside it, in place of Windows' etched
            // line, which is much darker than Explorer's.
            using var barPen = new Pen(ExplorerStyle.BarSeparator);
            var x = item.Width / 2;
            eventArgs.Graphics.DrawLine(barPen, x, 0, x, item.Height);
            return;
        }

        using var pen = new Pen(ExplorerStyle.MenuSeparator);
        var y = item.Height / 2;
        eventArgs.Graphics.DrawLine(pen, 0, y, item.Width, y);
    }

    // A submenu's chevron, as Explorer has, in place of a small triangle.
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs eventArgs)
    {
        if (eventArgs.Item is not { IsOnDropDown: true } item)
        {
            base.OnRenderArrow(eventArgs);
            return;
        }

        var color = SystemColors.MenuText;
        if (!item.Enabled)
        {
            color = SystemColors.GrayText;
        }

        DrawGlyph(eventArgs.Graphics, Icons.ChevronRight, eventArgs.ArrowRectangle, Scale(item, arrowSize), color);
    }

    // A tick in place of the icon, as Explorer has. An entry with an icon of its own keeps it,
    // shaded behind, as Windows marks a ticked entry with a picture.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        if (!item.IsOnDropDown)
        {
            base.OnRenderItemCheck(eventArgs);
            return;
        }

        var bounds = Indented(item, eventArgs.ImageRectangle);
        if (item.Image != null)
        {
            var margin = Scale(item, 3);
            Shade(eventArgs.Graphics, Rectangle.Inflate(bounds, margin, margin), ExplorerStyle.Hover(ExplorerStyle.MenuBackground), Scale(item, cornerRadius));
            return;
        }

        var color = SystemColors.MenuText;
        if (!item.Enabled)
        {
            color = SystemColors.GrayText;
        }

        DrawGlyph(eventArgs.Graphics, Icons.CheckMark, bounds, Scale(item, checkSize), color);
    }

    static void DrawGlyph(Graphics graphics, char glyph, Rectangle bounds, int size, Color color) =>
        graphics.DrawImage(
            Icons.Bitmap(glyph, size, color),
            bounds.Left + (bounds.Width - size) / 2,
            bounds.Top + (bounds.Height - size) / 2,
            size,
            size);

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        if (eventArgs.Item.IsOnDropDown)
        {
            base.OnRenderButtonBackground(eventArgs);
            return;
        }

        ShadeButton(eventArgs.Graphics, eventArgs.Item);
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        if (eventArgs.Item.IsOnDropDown)
        {
            base.OnRenderDropDownButtonBackground(eventArgs);
            return;
        }

        ShadeButton(eventArgs.Graphics, eventArgs.Item);
    }

    // Nothing at rest; under the mouse a shade darker than the bar, and pressed, or open, a
    // shade lighter, as Windows 11 has it.
    static void ShadeButton(Graphics graphics, ToolStripItem item)
    {
        Color shade;
        if (item.Pressed ||
            item is ToolStripButton { Checked: true })
        {
            shade = ExplorerStyle.Pressed(ExplorerStyle.Window);
        }
        else if (item.Selected)
        {
            shade = ExplorerStyle.Hover(ExplorerStyle.Window);
        }
        else
        {
            return;
        }

        Shade(graphics, new(Point.Empty, item.Size), shade, Scale(item, cornerRadius));
    }
}
