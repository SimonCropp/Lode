using System.Runtime.InteropServices;

/// <summary>
/// A window's row of 30 tabs, each with its folder's icon, the pointer moving along it from the
/// first tab to the last: at each tab it crosses, the strip is painted as Windows would paint it
/// on screen, what the move left to paint and no more, and what the strip queued meanwhile is
/// run, as the window's messages are. The window is off screen, where Windows paints nothing,
/// so the strip paints into a bitmap of its own, made once, clipped to what was left to paint.
/// </summary>
[MemoryDiagnoser]
public class TabStripBenchmarks
{
    const int tabCount = 30;
    const int mouseMove = 0x0200;
    const int printClient = 0x0318;
    const int printClientArea = 0x0004;

    QuietForm form = null!;
    ClosableTabControl tabs = null!;
    List<Point> crossing = [];

    // What the strip paints into.
    IntPtr context;
    IntPtr bitmap;
    IntPtr previousBitmap;

    [GlobalSetup]
    public void Setup()
    {
        string[] names = ["Lode", "src", "Documents", "Downloads", "Directory.Build.props", "node_modules", "Release 10", "Pictures"];
        tabs = new()
        {
            Size = new(9000, 300),
            // A drop target is registered with OLE, which needs the thread to be STA, as a
            // benchmark's isn't. Nothing is dropped here.
            AllowDrop = false
        };
        var icon = ShellIcons.GetPlaceIcon(Path.GetTempPath());
        for (var index = 0; index < tabCount; index++)
        {
            var page = new TabPage($"{names[index % names.Length]} {index}");
            tabs.TabPages.Add(page);
            tabs.SetIcon(page, icon);
        }

        form = new()
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            FormBorderStyle = FormBorderStyle.None,
            // Wider than the screen, which WinForms allows only a window with a maximum size.
            MaximumSize = new(10_000, 10_000),
            ClientSize = tabs.Size
        };
        form.Controls.Add(tabs);
        form.Show();
        Application.DoEvents();
        for (var index = 0; index < tabCount; index++)
        {
            var tab = tabs.GetTabRect(index);
            if (tab.Right > tabs.Width)
            {
                throw new("The row is too narrow for every tab, so the pointer can't cross them all.");
            }

            crossing.Add(new(tab.Left + 10, tab.Top + tab.Height / 2));
        }

        var screen = GetDC(IntPtr.Zero);
        context = CreateCompatibleDC(screen);
        bitmap = CreateCompatibleBitmap(screen, tabs.Width, tabs.Height);
        previousBitmap = SelectObject(context, bitmap);
        ReleaseDC(IntPtr.Zero, screen);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        SelectObject(context, previousBitmap);
        DeleteObject(bitmap);
        DeleteDC(context);
        form.Dispose();
    }

    [Benchmark]
    public void MoveAlongTheTabs()
    {
        foreach (var point in crossing)
        {
            NativeMethods.SendMessage(tabs.Handle, mouseMove, 0, (point.Y << 16) | (point.X & 0xFFFF));
            PaintWhatWasLeft();
            Application.DoEvents();
        }
    }

    void PaintWhatWasLeft()
    {
        if (!GetUpdateRect(tabs.Handle, out var left, false))
        {
            return;
        }

        ValidateRect(tabs.Handle, IntPtr.Zero);
        SelectClipRgn(context, IntPtr.Zero);
        IntersectClipRect(context, left.Left, left.Top, left.Right, left.Bottom);
        NativeMethods.SendMessage(tabs.Handle, printClient, context, printClientArea);
    }

    [DllImport("user32.dll")]
    static extern bool GetUpdateRect(IntPtr window, out NativeMethods.NativeRect rect, bool erase);

    [DllImport("user32.dll")]
    static extern bool ValidateRect(IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr window, IntPtr context);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr context);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleBitmap(IntPtr context, int width, int height);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr context, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr context);

    [DllImport("gdi32.dll")]
    static extern int SelectClipRgn(IntPtr context, IntPtr region);

    [DllImport("gdi32.dll")]
    static extern int IntersectClipRect(IntPtr context, int left, int top, int right, int bottom);

    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
