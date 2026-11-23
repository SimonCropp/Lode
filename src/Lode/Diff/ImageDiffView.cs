/// <summary>
/// An image as it was beside the image as it is, as GitHub Desktop's two-up image diff shows
/// them: each under its label, red for the one deleted, green for the one added, on a checked
/// ground where it is see-through, no bigger than it is, and smaller to fit, with its size in
/// pixels and bytes beneath. One added or deleted shows alone.
/// </summary>
sealed class ImageDiffView : Control
{
    // Pixels at 100% scale.
    const int margin = 16;
    const int gap = 24;
    const int border = 1;
    const int check = 8;

    Image? before;
    Image? after;
    long beforeLength;
    long afterLength;
    Font bold;

    public ImageDiffView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DiffStyle.Background;
        bold = DesktopStyle.Semibold(Font, Font.SizeInPoints);
        AccessibleName = "Image diff";
        AccessibleRole = AccessibleRole.Graphic;
    }

    /// <summary>
    /// Shows <paramref name="diff"/>. False when neither image could be read as one, which is
    /// then said to be binary.
    /// </summary>
    public bool Show(ImageDiff diff)
    {
        Clear();
        before = Load(diff.Old);
        after = Load(diff.New);
        beforeLength = diff.Old?.LongLength ?? 0;
        afterLength = diff.New?.LongLength ?? 0;
        Invalidate();
        return before != null || after != null;
    }

    // A copy of the image, as GDI+ needs the stream an image is read from kept open for as long
    // as the image is.
    static Bitmap? Load(byte[]? bytes)
    {
        if (bytes == null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new(image);
        }
        catch (Exception exception)
            when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    void Clear()
    {
        before?.Dispose();
        after?.Dispose();
        before = null;
        after = null;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        using (var background = new SolidBrush(DiffStyle.Background))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        var outer = LogicalToDeviceUnits(margin);
        var room = Rectangle.FromLTRB(outer, outer, ClientSize.Width - outer, ClientSize.Height - outer);
        if (room.Width <= 0 ||
            room.Height <= 0)
        {
            return;
        }

        if (before != null &&
            after != null)
        {
            var between = LogicalToDeviceUnits(gap);
            var half = (room.Width - between) / 2;
            DrawImage(graphics, new(room.Left, room.Top, half, room.Height), before, beforeLength, "Deleted", DiffStyle.DeletedCount);
            DrawImage(graphics, new(room.Left + half + between, room.Top, half, room.Height), after, afterLength, "Added", DiffStyle.AddedCount);
            return;
        }

        if (before != null)
        {
            DrawImage(graphics, room, before, beforeLength, "Deleted", DiffStyle.DeletedCount);
            return;
        }

        if (after != null)
        {
            DrawImage(graphics, room, after, afterLength, "Added", DiffStyle.AddedCount);
        }
    }

    void DrawImage(Graphics graphics, Rectangle room, Image image, long length, string label, Color colour)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis;
        var labelHeight = bold.Height + LogicalToDeviceUnits(8);
        var detailsHeight = Font.Height + LogicalToDeviceUnits(8);
        TextRenderer.DrawText(graphics, label, bold, new Rectangle(room.Left, room.Top, room.Width, labelHeight), colour, flags);

        var thickness = LogicalToDeviceUnits(border);
        var space = Rectangle.FromLTRB(room.Left, room.Top + labelHeight, room.Right, room.Bottom - detailsHeight);
        var fit = Math.Min((space.Width - thickness * 2) / (float) Math.Max(1, image.Width), (space.Height - thickness * 2) / (float) Math.Max(1, image.Height));
        var scale = Math.Min(1f, fit);
        if (scale <= 0)
        {
            return;
        }

        var width = Math.Max(1, (int) (image.Width * scale));
        var height = Math.Max(1, (int) (image.Height * scale));
        var shown = new Rectangle(space.Left + (space.Width - width) / 2, space.Top, width, height);
        DrawChecks(graphics, shown);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (scale >= 1f)
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        }

        graphics.DrawImage(image, shown);
        using (var pen = new Pen(colour, thickness))
        {
            var frame = Rectangle.Inflate(shown, thickness, thickness);
            graphics.DrawRectangle(pen, frame.Left, frame.Top, frame.Width - 1, frame.Height - 1);
        }

        var details = $"{image.Width:N0} × {image.Height:N0} px · {Formatting.Size(length)}";
        TextRenderer.DrawText(graphics, details, Font, new Rectangle(room.Left, shown.Bottom + LogicalToDeviceUnits(6), room.Width, detailsHeight), DesktopStyle.SecondaryText, flags);
    }

    // The ground a see-through image is drawn on: light and lighter squares.
    void DrawChecks(Graphics graphics, Rectangle bounds)
    {
        var size = Math.Max(2, LogicalToDeviceUnits(check));
        var light = ExplorerStyle.Blend(DiffStyle.Background, DiffStyle.Border, 0.5f);
        using var brush = new SolidBrush(light);
        var state = graphics.Save();
        graphics.SetClip(bounds);
        for (var y = bounds.Top; y < bounds.Bottom; y += size)
        {
            for (var x = bounds.Left + (y - bounds.Top) / size % 2 * size; x < bounds.Right; x += size * 2)
            {
                graphics.FillRectangle(brush, x, y, size, size);
            }
        }

        graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Clear();
            bold.Dispose();
        }

        base.Dispose(disposing);
    }
}
