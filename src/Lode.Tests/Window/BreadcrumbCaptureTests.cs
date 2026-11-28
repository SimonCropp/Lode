#if DEBUG
/// <summary>
/// The address bar, drawn. Pixels depend on the machine's fonts, so, as with
/// <see cref="MenuTests"/>, these only run in Debug builds.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class BreadcrumbCaptureTests
{
    // Somewhere on the network starts at the machine it is on, with the network's icon.
    [Test]
    public Task Share() =>
        Capture(@"\\192.168.1.110\video\tv");

    // The chevron whose folders are dropped down is drawn pressed, pointing down at them, until
    // they close.
    [Test]
    public Task ChevronOpen() =>
        Capture(
            @"D:\Projects\Lode\src",
            breadcrumb =>
            {
                const int leftButtonDown = 0x0201;
                const int leftButtonUp = 0x0202;
                const int leftButton = 0x0001;
                var chevron = breadcrumb.ChevronBounds(@"D:\Projects\Lode");
                var point = ((chevron.Top + chevron.Height / 2) << 16) | (chevron.Left + chevron.Width / 2);
                NativeMethods.SendMessage(breadcrumb.Handle, leftButtonDown, leftButton, point);
                NativeMethods.SendMessage(breadcrumb.Handle, leftButtonUp, 0, point);
            });

    // A tab with no folder, Find Results here: the bar says what the tab is, in place of the
    // folder it showed last, grey, as clicking it does nothing.
    [Test]
    public Task Caption() =>
        Capture(
            @"D:\Projects\Lode\src",
            breadcrumb =>
            {
                breadcrumb.Enabled = false;
                breadcrumb.Caption = (Icons.Search, "Find Results");
            });

    static async Task Capture(string location, Action<Breadcrumb>? before = null)
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            FormBorderStyle = FormBorderStyle.None
        };
        var menus = new List<ToolStripDropDown>();
        var breadcrumb = new Breadcrumb(new())
        {
            Width = 400,
            Path = location,
            // Opened, rather than shown, and the folders found at once: what is captured is the
            // bar, not the menu.
            ShowMenu = (menu, _) => menus.Add(menu),
            ReadFolders = (_, _, _) => []
        };
        form.ClientSize = breadcrumb.Size;
        form.Controls.Add(breadcrumb);
        form.Show();
        Application.DoEvents();
        before?.Invoke(breadcrumb);
        Application.DoEvents();
        using var bitmap = new Bitmap(breadcrumb.Width, breadcrumb.Height);
        breadcrumb.DrawToBitmap(bitmap, new(Point.Empty, breadcrumb.Size));
        foreach (var menu in menus)
        {
            menu.Dispose();
        }

        var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        await Verify(stream, "png");
    }
}
#endif
