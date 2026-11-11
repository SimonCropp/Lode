using System.Drawing.Drawing2D;

static class AppIcon
{
    public static Icon Create()
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
