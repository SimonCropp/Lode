using System.Runtime.InteropServices;

/// <summary>
/// The padded icons every list draws its rows' icons from (ShellListView.PaddedIcons), each
/// icon its own, as a program's, an icon file's or a shortcut's is. SwitchScale is a list that
/// has shown 5,000 of them, moved to a monitor at twice the scale and drawn there.
/// ShowDistinctIcons shows 20,000 at 200%, one after another, as scrolling through a folder of
/// them does, and says how much the images then held take. The icons are the system image
/// list's, those past its end blank, which costs as much to hold.
/// </summary>
[MemoryDiagnoser]
public class PaddedIconsBenchmarks
{
    const int seen = 5_000;
    const int distinct = 20_000;

    ShellListView list = null!;
    Bitmap bitmap = null!;
    ShellListView.PaddedIcons icons = null!;
    ShellListView.PaddedIcons.Set shown = null!;
    int dpi;

    [GlobalSetup]
    public void Setup()
    {
        list = new()
        {
            VirtualMode = true,
            View = View.Details,
            Size = new(600, 1200)
        };
        list.Columns.Add("Name", 400);
        list.RetrieveVirtualItem += (_, args) =>
            args.Item = new($"Item {args.ItemIndex}", icons.Index(args.ItemIndex, 0));
        list.CreateControl();
        dpi = list.DeviceDpi;
        bitmap = new(list.Width, list.Height);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        bitmap.Dispose();
        list.Dispose();
    }

    [IterationSetup(Target = nameof(SwitchScale))]
    public void SetupSwitch()
    {
        icons = new(ShellListView.PaddedIcons.MostIcons);
        list.Icons = icons;
        var set = icons.For(dpi);
        for (var icon = 0; icon < seen; icon++)
        {
            set.Show(icons.Index(icon, 0));
        }

        list.VirtualListSize = seen;
        list.DrawToBitmap(bitmap, new(0, 0, list.Width, list.Height));
    }

    [IterationCleanup(Target = nameof(SwitchScale))]
    public void CleanupSwitch() =>
        icons.Dispose();

    // Moved to a monitor at twice the scale, and drawn there.
    [Benchmark]
    [InvocationCount(1)]
    public void SwitchScale()
    {
        list.UseImagesFor(dpi * 2);
        list.DrawToBitmap(bitmap, new(0, 0, list.Width, list.Height));
    }

    [IterationSetup(Target = nameof(ShowDistinctIcons))]
    public void SetupShow()
    {
        icons = new(ShellListView.PaddedIcons.MostIcons);
        shown = icons.For(dpi * 2);
    }

    [Benchmark]
    [InvocationCount(1)]
    public void ShowDistinctIcons()
    {
        for (var icon = 0; icon < distinct; icon++)
        {
            shown.Show(icons.Index(icon, 0));
        }
    }

    [IterationCleanup(Target = nameof(ShowDistinctIcons))]
    public void Held()
    {
        ImageList_GetIconSize(shown.Handle, out var width, out var height);
        var count = ImageList_GetImageCount(shown.Handle);
        Console.WriteLine($"// Held at {dpi * 2} dpi: {count} images, {(long) count * width * height * 4 / (1 << 20)} MB");
        icons.Dispose();
    }

    [DllImport("comctl32.dll")]
    static extern int ImageList_GetImageCount(IntPtr imageList);

    [DllImport("comctl32.dll")]
    static extern bool ImageList_GetIconSize(IntPtr imageList, out int width, out int height);
}
