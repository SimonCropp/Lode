#if DEBUG
/// <summary>
/// The address bar, drawn. Pixels depend on the machine's fonts, so, as with
/// <see cref="MenuTests"/>, these only run in Debug builds.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class BreadcrumbCaptureTests
{
    // Somewhere on the network starts at the machine it is on, with the network's icon.
    [Test]
    public Task Share() =>
        Capture(@"\\192.168.1.110\video\tv");

    static async Task Capture(string location)
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            FormBorderStyle = FormBorderStyle.None
        };
        var breadcrumb = new Breadcrumb
        {
            Width = 400,
            Path = location
        };
        form.ClientSize = breadcrumb.Size;
        form.Controls.Add(breadcrumb);
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(breadcrumb.Width, breadcrumb.Height);
        breadcrumb.DrawToBitmap(bitmap, new(Point.Empty, breadcrumb.Size));
        var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        await Verify(stream, "png");
    }
}
#endif