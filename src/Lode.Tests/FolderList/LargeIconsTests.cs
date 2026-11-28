using System.Collections.Concurrent;

/// <summary>
/// The list in Large icons: each item's thumbnail, or its icon, big, with its name under it.
/// What the shell would make of an item is stood in for, so these hold on any machine, whatever
/// its thumbnail cache has.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class LargeIconsTests
{
    const int keyDown = 0x0100;
    const int mouseWheel = 0x020A;
    const int wheelControl = 0x0008;
    const int listViewScroll = 0x1014;
    const int editGetSelection = 0x00B0;
    const int setText = 0x000C;
    const int virtualKeyReturn = 0x0D;
    const int virtualKeyEnd = 0x23;
    const int virtualKeyRight = 0x27;
    const int virtualKeyDown = 0x28;

    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    // Details until Large icons is chosen, and then for that folder only: in any tab, and the
    // next time Lode starts, until Details is chosen again.
    [Test]
    public async Task EachFolderShowsAsItWasLastShown()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var form = Host();
        var view = await OpenIn(form, fixture.Root, settings);
        var before = (view.LargeIcons, view.List.View);

        view.ShowLargeIcons(true);
        var chosen = (view.LargeIcons, view.List.View);
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        var elsewhere = view.List.View;
        view.GoBack();
        await Pump.Loaded(view);
        var back = view.List.View;
        using var otherForm = Host();
        var other = await OpenIn(otherForm, fixture.Root, settings);
        var inAnotherTab = other.List.View;
        view.ShowLargeIcons(false);

        await Assert.That(before).IsEqualTo((false, View.Details));
        await Assert.That(chosen).IsEqualTo((true, View.LargeIcon));
        await Assert.That(elsewhere).IsEqualTo(View.Details);
        await Assert.That(back).IsEqualTo(View.LargeIcon);
        await Assert.That(inAnotherTab).IsEqualTo(View.LargeIcon);
        await Assert.That(view.List.View).IsEqualTo(View.Details);
        await Assert.That(settings.LargeIconFolders).IsEmpty();
    }

    // As in Explorer: Ctrl and the wheel turned away makes the icons bigger, toward smaller. A
    // fine wheel's small turns add up to one notch. Without Ctrl the wheel only scrolls.
    [Test]
    public async Task CtrlAndTheWheelSwitchTheView()
    {
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;

        Wheel(list, 120, control: true);
        var away = list.View;
        Wheel(list, 120, control: true);
        var furtherAway = list.View;
        Wheel(list, -60, control: true);
        var halfANotch = list.View;
        Wheel(list, -60, control: true);
        var toward = list.View;
        Wheel(list, 120, control: false);
        var withoutControl = list.View;

        await Assert.That(away).IsEqualTo(View.LargeIcon);
        await Assert.That(furtherAway).IsEqualTo(View.LargeIcon);
        await Assert.That(halfANotch).IsEqualTo(View.LargeIcon);
        await Assert.That(toward).IsEqualTo(View.Details);
        await Assert.That(withoutControl).IsEqualTo(View.Details);
    }

    // Each is read off the UI thread, on one set up for the shell's COM objects, at Large icons'
    // size, and only those on screen are: not the rest of a folder of hundreds.
    [Test]
    public async Task ThumbnailsAreReadOffTheUIThreadForTheItemsShown()
    {
        using var fixture = Fixture.Create();
        var folder = ManyFiles(fixture, 300);
        using var form = Host();
        var view = await OpenIn(form, folder);
        var reads = new ConcurrentQueue<(string Path, int Thread, ApartmentState Apartment, int Size)>();
        view.ReadThumbnail = (path, size) =>
        {
            reads.Enqueue((path, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), size));
            return Picture(size);
        };

        view.ShowLargeIcons(true);
        await Thumbnailed(view);
        var shown = Shown(view);

        await Assert.That(shown.Count).IsGreaterThan(0).And.IsLessThan(300);
        await Assert.That(reads.Select(_ => _.Path)).IsEquivalentTo(shown);
        await Assert.That(reads.All(_ => _.Thread != Environment.CurrentManagedThreadId)).IsTrue();
        await Assert.That(reads.All(_ => _.Apartment == ApartmentState.STA)).IsTrue();
        await Assert.That(reads.All(_ => _.Size == 96)).IsTrue();
        await Assert.That(view.ThumbnailsRead).IsEquivalentTo(shown);
    }

    // Kept while the folder is open: drawn again, scrolled past and back, shown in Details and
    // in Large icons again, the folder read again, each is read once. Written since, it is read
    // again.
    [Test]
    public async Task EachIsReadOnceWhileTheFolderIsOpen()
    {
        using var fixture = Fixture.Create();
        var folder = ManyFiles(fixture, 300);
        using var form = Host();
        var view = await OpenIn(form, folder);
        var reads = new ConcurrentQueue<string>();
        view.ReadThumbnail = (path, size) =>
        {
            reads.Enqueue(path);
            return Picture(size);
        };
        var list = view.List;

        view.ShowLargeIcons(true);
        await Thumbnailed(view);
        var firstScreen = Shown(view);
        list.Invalidate();
        list.Update();
        await Pump.For(TimeSpan.FromMilliseconds(100));
        NativeMethods.SendMessage(list.Handle, listViewScroll, IntPtr.Zero, list.ClientSize.Height);
        await Thumbnailed(view);
        var secondScreen = Shown(view);
        NativeMethods.SendMessage(list.Handle, listViewScroll, IntPtr.Zero, -list.ClientSize.Height);
        await Pump.For(TimeSpan.FromMilliseconds(100));
        view.ShowLargeIcons(false);
        view.ShowLargeIcons(true);
        await Pump.For(TimeSpan.FromMilliseconds(100));
        view.Reload();
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(100));
        var readOnce = reads.ToList();

        File.WriteAllBytes(firstScreen[0], [1, 2, 3]);
        await Pump.Until(() => reads.Count(_ => _ == firstScreen[0]) == 2);

        await Assert.That(secondScreen.Except(firstScreen)).IsNotEmpty();
        await Assert.That(readOnce).IsEquivalentTo(firstScreen.Union(secondScreen));
        await Assert.That(readOnce.Distinct().Count()).IsEqualTo(readOnce.Count);
    }

    // What was still to be read of the folder left is not read, and what was read of it is let
    // go: only the folder gone to's are kept.
    [Test]
    public async Task GoingToAnotherFolderCancelsAndDropsThem()
    {
        using var fixture = Fixture.Create();
        var folder = ManyFiles(fixture, 300);
        var settings = new Settings {ShowHidden = false};
        settings.SetLargeIcons(fixture.At("Docs"), true);
        using var form = Host();
        var view = await OpenIn(form, folder, settings);
        using var gate = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        view.ReadThumbnail = (path, size) =>
        {
            started.Enqueue(path);
            gate.Wait(TimeSpan.FromSeconds(20));
            return Picture(size);
        };
        view.ShowLargeIcons(true);
        await Pump.Until(() => !started.IsEmpty);
        await Pump.For(TimeSpan.FromMilliseconds(300));
        var inFlight = started.ToList();
        var shownThen = Shown(view);

        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        gate.Set();
        await Thumbnailed(view);
        var readOfTheFolderLeft = started.Where(_ => Locations.Same(Path.GetDirectoryName(_), folder)).ToList();

        await Assert.That(shownThen.Count).IsGreaterThan(inFlight.Count);
        await Assert.That(readOfTheFolderLeft).IsEquivalentTo(inFlight);
        await Assert.That(view.ThumbnailsRead).IsEquivalentTo([fixture.At("Docs", "guide.aleph")]);
    }

    // Scrolled on before they are read, those no longer on screen are not read: those that are
    // now are.
    [Test]
    public async Task ScrollingOnCancelsThoseNoLongerShown()
    {
        using var fixture = Fixture.Create();
        var folder = ManyFiles(fixture, 300);
        using var form = Host();
        var view = await OpenIn(form, folder);
        using var gate = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        view.ReadThumbnail = (path, size) =>
        {
            started.Enqueue(path);
            gate.Wait(TimeSpan.FromSeconds(20));
            return Picture(size);
        };
        view.ShowLargeIcons(true);
        await Pump.Until(() => !started.IsEmpty);
        await Pump.For(TimeSpan.FromMilliseconds(300));
        var inFlight = started.ToList();
        var shownFirst = Shown(view);

        NativeMethods.SendMessage(view.List.Handle, listViewScroll, IntPtr.Zero, 100_000);
        await Pump.For(TimeSpan.FromMilliseconds(200));
        var shownLast = Shown(view);
        gate.Set();
        await Thumbnailed(view);

        await Assert.That(shownLast.Intersect(shownFirst)).IsEmpty();
        await Assert.That(started.Intersect(shownFirst)).IsEquivalentTo(inFlight);
        await Assert.That(shownLast.Except(started)).IsEmpty();
    }

    // The keys move about the icons as they lie: Right to the next, Down to the one below, End
    // to the last, scrolled to.
    [Test]
    public async Task TheKeysMoveAboutTheIcons()
    {
        using var fixture = Fixture.Create();
        var folder = ManyFiles(fixture, 300);
        using var form = Host();
        var view = await OpenIn(form, folder);
        view.ReadThumbnail = (_, size) => Picture(size);
        var list = view.List;
        view.ShowLargeIcons(true);
        await Pump.NoModifiersHeld();
        list.SelectedIndices.Add(0);
        list.FocusedItem = list.Items[0];
        var top = list.GetItemRect(0, ItemBoundsPortion.Icon).Top;
        var perRow = Enumerable.Range(0, 300).TakeWhile(_ => list.GetItemRect(_, ItemBoundsPortion.Icon).Top == top).Count();

        Key(list, virtualKeyRight);
        var right = Selected(view);
        Key(list, virtualKeyDown);
        var down = Selected(view);
        Key(list, virtualKeyEnd);
        var end = Selected(view);

        await Assert.That(perRow).IsGreaterThan(1).And.IsLessThan(300);
        await Assert.That(right).IsEquivalentTo([1]);
        await Assert.That(down).IsEquivalentTo([1 + perRow]);
        await Assert.That(end).IsEquivalentTo([299]);
        await Assert.That(Shown(view)).Contains(view.Entries[299].FullPath);
    }

    // F2 edits the name under the icon, the name but not the extension selected, as in Details,
    // and Enter renames the file.
    [Test]
    public async Task RenamesInLargeIcons()
    {
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        view.ReadThumbnail = (_, size) => Picture(size);
        var list = view.List;
        view.ShowLargeIcons(true);
        var index = view.Entries.Select(_ => _.Name).ToList().IndexOf("notes.aleph");
        list.SelectedIndices.Add(index);
        list.FocusedItem = list.Items[index];

        view.HandleKey(Keys.F2);
        var edit = IntPtr.Zero;
        await Pump.Until(() => (edit = NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetEditControl, IntPtr.Zero, IntPtr.Zero)) != IntPtr.Zero);
        await Pump.Until(() => NativeMethods.SendMessage(edit, editGetSelection, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero);
        var selection = (int) NativeMethods.SendMessage(edit, editGetSelection, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SendMessage(edit, setText, IntPtr.Zero, "renamed.aleph");
        NativeMethods.SendMessage(edit, keyDown, virtualKeyReturn, IntPtr.Zero);
        await Pump.Until(() => File.Exists(fixture.At("renamed.aleph")));
        await Pump.Loaded(view);

        await Assert.That((selection & 0xFFFF, selection >> 16)).IsEqualTo((0, 5));
        await Assert.That(File.Exists(fixture.At("notes.aleph"))).IsFalse();
        await Assert.That(view.Entries.Select(_ => _.Name)).Contains("renamed.aleph");
        await Assert.That(NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetEditControl, IntPtr.Zero, IntPtr.Zero)).IsEqualTo(IntPtr.Zero);
    }

    // Back in Details, the list is as it was: its columns, its rows' padded icons from the
    // images every list shares, and their height.
    [Test]
    public async Task DetailsAgainIsAsBefore()
    {
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        view.ReadThumbnail = (_, size) => Picture(size);
        var columns = view.ColumnTitles.ToList();
        var icons = Enumerable.Range(0, view.Entries.Count).Select(view.RowIcon).ToList();
        var images = view.List.RowImages;
        var row = view.RowBounds(0);

        view.ShowLargeIcons(true);
        await Thumbnailed(view);
        view.ShowLargeIcons(false);

        await Assert.That(view.List.View).IsEqualTo(View.Details);
        await Assert.That(view.ColumnTitles).IsEquivalentTo(columns, CollectionOrdering.Matching);
        await Assert.That(Enumerable.Range(0, view.Entries.Count).Select(view.RowIcon)).IsEquivalentTo(icons, CollectionOrdering.Matching);
        await Assert.That(view.List.RowImages).IsEqualTo(images);
        await Assert.That(view.RowBounds(0)).IsEqualTo(row);
    }

    // What the shell makes of a picture, as a thumbnail no bigger than asked, the right way up
    // and opaque; and of a file with no thumbnail, its icon, as big, with what is around it
    // transparent.
    [Test]
    public async Task TheShellsThumbnailOrIcon()
    {
        using var fixture = Fixture.Create();
        var jpeg = fixture.At("picture.jpg");
        using (var drawn = new Bitmap(400, 200))
        {
            using (var graphics = Graphics.FromImage(drawn))
            {
                graphics.Clear(Color.Red);
                graphics.FillRectangle(Brushes.Blue, 0, 100, 400, 100);
            }

            drawn.Save(jpeg, ImageFormat.Jpeg);
        }

        using var picture = Thumbnails.FromShell(jpeg, 96)!;
        using var icon = Thumbnails.FromShell(fixture.At("notes.aleph"), 96)!;
        var top = picture.GetPixel(48, 4);
        var bottom = picture.GetPixel(48, picture.Height - 4);

        await Assert.That(picture.Size).IsEqualTo(new Size(96, 48));
        await Assert.That((top.R > 200, top.B < 60, top.A)).IsEqualTo((true, true, (byte) 255));
        await Assert.That((bottom.B > 200, bottom.R < 60, bottom.A)).IsEqualTo((true, true, (byte) 255));
        await Assert.That(icon.Size).IsEqualTo(new Size(96, 96));
        await Assert.That(icon.GetPixel(0, 0).A).IsEqualTo((byte) 0);
    }

    // A folder of many empty files, named in order.
    static string ManyFiles(Fixture fixture, int count)
    {
        var folder = fixture.At("Archive");
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"picture{index:000}.aleph"), []);
        }

        return folder;
    }

    // What stands in for a thumbnail: wider than it is tall, as a photograph is.
    static Bitmap Picture(int size)
    {
        var bitmap = new Bitmap(size, size / 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.SteelBlue);
        return bitmap;
    }

    // Once the items on screen have had their thumbnails read, and nothing more is being read.
    static Task Thumbnailed(FolderView view) =>
        Pump.Until(() => !view.ReadingThumbnails &&
                         Shown(view) is {Count: > 0} shown &&
                         shown.All(view.ThumbnailsRead.Contains));

    // The items whose icon is within the list, by their paths.
    static List<string> Shown(FolderView view)
    {
        var list = view.List;
        var client = list.ClientRectangle;
        return Enumerable.Range(0, view.Entries.Count)
            .Where(_ => list.GetItemRect(_, ItemBoundsPortion.Icon).IntersectsWith(client))
            .Select(_ => view.Entries[_].FullPath)
            .ToList();
    }

    static List<int> Selected(FolderView view) =>
        view.List.SelectedIndices.Cast<int>().ToList();

    static void Key(ListView list, int key) =>
        NativeMethods.SendMessage(list.Handle, keyDown, key, IntPtr.Zero);

    // WM_MOUSEWHEEL: the keys held in the low word, how far the wheel turned in the high.
    static void Wheel(ListView list, int delta, bool control)
    {
        var keys = control ? wheelControl : 0;
        NativeMethods.SendMessage(list.Handle, mouseWheel, (delta << 16) | keys, IntPtr.Zero);
    }

    // A window for a view, off screen. Only in a window does the list draw, and so ask for what
    // it shows.
    static Form Host() =>
        new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(900, 400)
        };

    static async Task<FolderView> OpenIn(Form form, string path, Settings? settings = null)
    {
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces)
        {
            Dock = DockStyle.Fill
        };
        form.Controls.Add(view);
        form.Show();
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    // Showing a window normally takes the focus from whatever the developer is doing.
    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
