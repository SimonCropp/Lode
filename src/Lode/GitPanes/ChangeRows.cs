/// <summary>
/// How a changed file is drawn in a row, in the Changes pane and under a commit in History, as
/// GitHub Desktop draws it: its folder dimmed, then its name, a renamed file's old name before it
/// with an arrow, and at the right Desktop's icon for what happened to it, in its colour.
/// </summary>
static class ChangeRows
{
    const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    // Pixels at 100% scale.
    const int iconSize = 16;

    /// <summary>
    /// What happened to a file, in words, for a screen reader and the row's tooltip.
    /// </summary>
    public static string Describe(Change change) =>
        change.Kind switch
        {
            GitChange.Added => $"{change.Path}, added",
            GitChange.Deleted => $"{change.Path}, deleted",
            GitChange.Renamed => $"{change.Path}, renamed from {change.OldPath}",
            GitChange.Untracked => $"{change.Path}, not tracked",
            GitChange.Conflicted => $"{change.Path}, conflicted",
            _ => $"{change.Path}, modified"
        };

    /// <summary>
    /// What the status icon stands for, for its tooltip.
    /// </summary>
    public static string Meaning(Change change) =>
        change.Kind switch
        {
            GitChange.Added => "Added",
            GitChange.Deleted => "Deleted",
            GitChange.Renamed => $"Renamed from {change.OldPath}",
            GitChange.Untracked => "Untracked: new to git",
            GitChange.Conflicted => "Conflicted: left to resolve",
            _ => "Modified"
        };

    /// <summary>
    /// Where the status icon is in a row drawn in <paramref name="bounds"/>: as far in from the
    /// right as Desktop's rows are padded, the height of the row, for its tooltip to show
    /// anywhere across it.
    /// </summary>
    public static Rectangle StatusBounds(Control control, Rectangle bounds)
    {
        var size = control.LogicalToDeviceUnits(iconSize);
        var inset = control.LogicalToDeviceUnits(DesktopStyle.Inset);
        return new(bounds.Right - inset - size, bounds.Top, size, bounds.Height);
    }

    /// <summary>
    /// The tooltip at a point of a list of changed files: what the status icon stands for over
    /// it, and the file and what happened to it elsewhere on its row, where the name may be cut
    /// short. Null off the rows.
    /// </summary>
    public static string? TipAt(RowList list, Point point, IReadOnlyList<Change> changes)
    {
        if (list.HitTest(point).Item is not { } item ||
            item.Index >= changes.Count)
        {
            return null;
        }

        var change = changes[item.Index];
        var row = list.GetItemRect(item.Index);
        row.Width = list.Columns[0].Width;
        if (StatusBounds(list, row).Contains(point))
        {
            return Meaning(change);
        }

        return Describe(change);
    }

    /// <param name="onBlue">The row is picked in a list with the focus, so its icon is drawn in the
    /// text's colour, white on the blue, as Desktop draws it.</param>
    public static void Draw(Graphics graphics, Control control, Font font, Rectangle bounds, Change change, Color text, Color secondary, bool onBlue)
    {
        var gap = control.LogicalToDeviceUnits(5);
        var status = StatusBounds(control, bounds);
        var size = control.LogicalToDeviceUnits(iconSize);
        var icon = new Rectangle(status.Left, status.Top + (status.Height - size) / 2, size, size);
        var iconColour = DesktopStyle.StatusColor(change.Kind);
        if (onBlue)
        {
            iconColour = text;
        }

        StatusIcons.Draw(graphics, icon, change.Kind, iconColour);

        var room = Rectangle.FromLTRB(bounds.Left, bounds.Top, status.Left - gap, bounds.Bottom);

        // A renamed file: where it was, an arrow, then where it is, as Desktop shows one.
        if (change is
            {
                Kind: GitChange.Renamed,
                OldPath: { } old
            })
        {
            var oldWidth = Math.Min(room.Width / 2, TextRenderer.MeasureText(graphics, old, font, Size.Empty, flags).Width);
            DrawPath(graphics, font, new(room.Left, room.Top, oldWidth, room.Height), old, text, secondary);
            var arrowSize = control.LogicalToDeviceUnits(12);
            var arrowLeft = room.Left + oldWidth + gap;
            graphics.DrawImage(Icons.Bitmap(Icons.ArrowRight, arrowSize, secondary), arrowLeft, room.Top + (room.Height - arrowSize) / 2, arrowSize, arrowSize);
            room = Rectangle.FromLTRB(arrowLeft + arrowSize + gap, room.Top, room.Right, room.Bottom);
        }

        var colour = text;
        if (change.Kind == GitChange.Deleted)
        {
            colour = secondary;
        }

        DrawPath(graphics, font, room, change.Path, colour, secondary);
    }

    // The folder dimmed, then the name, the name whole where it can be, the folder giving way
    // first.
    static void DrawPath(Graphics graphics, Font font, Rectangle room, string path, Color text, Color secondary)
    {
        if (room.Width <= 0)
        {
            return;
        }

        var slash = path.LastIndexOf('/');
        var folder = path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var nameWidth = TextRenderer.MeasureText(graphics, name, font, Size.Empty, flags).Width;
        var folderWidth = Math.Max(0, Math.Min(room.Width - nameWidth, TextRenderer.MeasureText(graphics, folder, font, Size.Empty, flags).Width));
        if (folder.Length > 0 &&
            folderWidth > 0)
        {
            TextRenderer.DrawText(graphics, folder, font, new Rectangle(room.Left, room.Top, folderWidth, room.Height), secondary, flags | TextFormatFlags.PathEllipsis);
        }

        var nameBounds = Rectangle.FromLTRB(room.Left + folderWidth, room.Top, room.Right, room.Bottom);
        TextRenderer.DrawText(graphics, name, font, nameBounds, text, flags | TextFormatFlags.EndEllipsis);
    }
}
