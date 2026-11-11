using System.Drawing.Drawing2D;

static class AppIcon
{
    /// <summary>
    /// File Explorer's own icon, read from explorer.exe on this machine, so Meridian looks like
    /// the app it stands in for. A drawn globe if that cannot be read.
    /// </summary>
    public static Icon Create()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            if (Icon.ExtractIcon(explorer, 0) is { } icon)
            {
                return icon;
            }
        }
        catch (Exception)
        {
            // Fall back to the globe.
        }

        return CreateGlobe();
    }

    static Icon CreateGlobe()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0x1E, 0x6F, 0xD9));
            graphics.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 1.6f);
            graphics.DrawEllipse(pen, 10, 1, 12, 30);
            graphics.DrawLine(pen, 16, 1, 16, 31);
            graphics.DrawLine(pen, 1, 16, 31, 16);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
