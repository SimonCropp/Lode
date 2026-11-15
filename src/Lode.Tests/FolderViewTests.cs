using System.Runtime.InteropServices;

/// <summary>
/// What the file list shows and selects, as text. These don't depend on how anything is
/// drawn, so they hold on any machine.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class FolderViewTests
{
    const int dateColumn = 1;
    const int typeColumn = 2;
    const int sizeColumn = 3;

    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    [Test]
    public Task DisplayNameHidesShortcutExtensions()
    {
        (string Name, bool IsDirectory)[] entries =
        [
            ("DbToMermaid.slnx.lnk", false),
            ("Site.url", false),
            ("UPPER.LNK", false),
            (".lnk", false),
            ("notes.txt", false),
            ("Folder.lnk", true)
        ];
        return Verify(entries.ToDictionary(
            _ => $"{_.Name} (directory: {_.IsDirectory})",
            _ => FolderView.DisplayName(
                new(_.Name, $@"C:\{_.Name}", _.IsDirectory, 0, default, _.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal))));
    }

    [Test]
    public async Task ListsFoldersFirstInNaturalOrder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortBySizeDescending()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortByType()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(typeColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortByDate()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(dateColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task FilterByPartOfName()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "REPORT";

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task FilterByWildcard()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task FilterIsClearedByNavigating()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Filter = "report";

        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task HiddenItemsWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() { ShowHidden = true });

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task History()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var steps = new List<object>();

        async Task Step(string name, Action action)
        {
            action();
            await Pump.Loaded(view);
            steps.Add(
                new
                {
                    step = name,
                    path = fixture.Relative(view.CurrentPath),
                    view.CanGoBack,
                    view.CanGoForward
                });
        }

        await Step("start", () => { });
        await Step("open Source", () => view.Navigate(fixture.At("Source")));
        await Step("open Deep", () => view.Navigate(fixture.At("Source", "Deep")));
        await Step("back", view.GoBack);
        await Step("back", view.GoBack);
        await Step("back at the start does nothing", view.GoBack);
        await Step("forward", view.GoForward);
        await Step("open Docs drops the forward history", () => view.Navigate(fixture.At("Docs")));
        await Step("forward at the end does nothing", view.GoForward);
        await Step("up", view.GoUp);

        await Verify(steps);
    }

    [Test]
    public async Task UpSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));

        view.GoUp();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task RevealSelectsTheFile()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces);

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task PicksUpChangesOnDisk()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);
        File.Delete(fixture.At("summary.gamma"));
        await Pump.Until(() => view.Entries.Any(_ => _.Name == "arrived.aleph") && view.Entries.All(_ => _.Name != "summary.gamma"));

        await Verify(Describe(view, fixture));
    }

    // A view nobody can see, such as a tab in the background, leaves changes on disk unread
    // until it shows again.
    [Test]
    public async Task LeavesChangesUnreadWhileNotShowing()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Showing = false;

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Several times as long as a change takes to be read by a view that is showing.
        await Pump.For(TimeSpan.FromSeconds(2));
        var readWhileHidden = Names(view).Contains("arrived.aleph");

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(readWhileHidden).IsFalse();
        await Assert.That(Names(view)).Contains("arrived.aleph");
    }

    // A folder opened in a view nobody can see, as a tab opened in the background is, is read
    // when the view shows, and the item it was opened for selected then.
    [Test]
    public async Task FolderOpenedWhileNotShowingIsReadWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false
        };

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);
        var path = view.CurrentPath;
        var readWhileHidden = Names(view);

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(path).IsEqualTo(fixture.Root);
        await Assert.That(readWhileHidden).IsEmpty();
        await Assert.That(Names(view)).Contains("report10.beta");
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
    }

    static List<string> Names(FolderView view) =>
        view.Entries.Select(_ => _.Name).ToList();

    // The list holds no rows: it asks for each as it draws it. However many items a folder has,
    // rows are only made for those.
    [Test]
    public async Task MakesOnlyTheRowsItDraws()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 600);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new(Point.Empty, form.ClientSize));

        await Assert.That(view.Entries.Count).IsEqualTo(600);
        await Assert.That(view.RowsMade).IsGreaterThan(0);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // Size column to fit goes by every row in the folder, though the list only ever asks for,
    // and so only measures, those on screen.
    [Test]
    public async Task SizeColumnToFitGoesByTheWholeFolder()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);

        // Sorts last, far below the rows on screen.
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var menu = view.HeaderMenu(clickedColumn: 0);
        menu.Items[0].PerformClick();

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);

        // The text, its icon and the margins around them, and no more.
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // A double-click on the divider at the right of a column's header sizes it to fit too, and
    // by the whole folder too.
    [Test]
    public async Task DoubleClickingAHeaderDividerFitsByTheWholeFolder()
    {
        const int notifyMessage = 0x004E;
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);
        var list = view.Controls.OfType<ListView>().Single();
        var header = NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);

        // NMHEADER, as the header sends it: itself, an id, the code, then the column.
        var notification = Marshal.AllocHGlobal(6 * IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(notification, header);
            Marshal.WriteIntPtr(notification, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(notification, 2 * IntPtr.Size, NativeMethods.HeaderDividerDoubleClick);
            Marshal.WriteInt32(notification, 3 * IntPtr.Size, 0);
            NativeMethods.SendMessage(list.Handle, notifyMessage, IntPtr.Zero, notification);
        }
        finally
        {
            Marshal.FreeHGlobal(notification);
        }

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
    }

    // Typing the start of a name moves to the next item that has it, going round to the top.
    [Test]
    public async Task TypingTheStartOfANameFindsTheNextWithIt()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var names = Names(view);

        string? Find(string text, int start)
        {
            var index = view.FindRow(text, start);
            if (index < 0)
            {
                return null;
            }

            return names[index];
        }

        var first = view.FindRow("rep", 0);

        await Assert.That(Find("rep", 0)).IsEqualTo("Report1.beta");
        await Assert.That(Find("REP", first + 1)).IsEqualTo("report2.beta");
        await Assert.That(Find("a", names.Count - 1)).IsEqualTo("Archive");
        await Assert.That(Find("zz", 0)).IsNull();
    }

    // Typed at the list itself, which holds no rows to look through and has the view do it.
    [Test]
    public async Task TypingAtTheListSelectsTheItemFound()
    {
        const int characterMessage = 0x0102;
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.Controls.OfType<ListView>().Single();

        foreach (var character in "rep")
        {
            NativeMethods.SendMessage(list.Handle, characterMessage, character, IntPtr.Zero);
        }

        await Assert.That(Selected(view)).IsEquivalentTo(["Report1.beta"]);
    }

    // The list asks for a renamed row again the moment the edit ends. It has the new name by
    // then, not only once the folder has been read again.
    [Test]
    public async Task RenamingShowsTheNewNameAtOnce()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var index = Names(view).IndexOf("notes.aleph");

        view.OnAfterLabelEdit(null, new(index, "renamed.aleph"));
        var atOnce = view.Entries[index];

        await Assert.That(atOnce.Name).IsEqualTo("renamed.aleph");
        await Assert.That(atOnce.FullPath).IsEqualTo(fixture.At("renamed.aleph"));
        await Assert.That(File.Exists(fixture.At("renamed.aleph"))).IsTrue();
    }

    // The list goes by where a row is, which moves when the folder changes. What was selected
    // stays selected.
    [Test]
    public async Task KeepsTheSelectionWhenTheFolderChanges()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces);
        view.Reveal(fixture.At("report2.beta"));
        await Pump.Loaded(view);

        // Sorts above it, which moves it down a row.
        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[10]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(Selected(view)).IsEquivalentTo(["report2.beta"]);
    }

    [Test]
    public async Task SortingKeepsEverythingSelected()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(view.Entries.Count - 1).Top + 2));
        marquee.End();
        var selectedBefore = Selected(view).Count;

        view.SortBy(sizeColumn);

        await Assert.That(selectedBefore).IsEqualTo(view.Entries.Count);
        await Assert.That(Selected(view).Count).IsEqualTo(view.Entries.Count);
    }

    // A folder of many files, each with nothing in it.
    static string Many(Fixture fixture, int count)
    {
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"file{index}.aleph"), []);
        }

        return folder;
    }

    // A window for a view, off screen. Only in a window does the list draw rows, and measure them.
    static Form Host() =>
        new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(900, 400)
        };

    static async Task<FolderView> OpenIn(Form form, string path)
    {
        var view = new FolderView(new() { ShowHidden = false }, noPlaces)
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

    [Test]
    public async Task CreateFolder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));

        view.CreateFolder();
        await Pump.Loaded(view);
        view.CreateFolder();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    // A drag from the margin left of the list selects the rows it spans.
    [Test]
    public async Task MarqueeFromTheMargin()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;

        // Straight down the margin, from the second row to the fourth.
        marquee.Begin(new(-5, view.RowBounds(1).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(3).Top + 2));
        marquee.End();
        var dragged = Selected(view);

        // With Ctrl, the rows covered flip: the fourth is dropped, the fifth added.
        marquee.Begin(new(-5, view.RowBounds(3).Top + 2), add: true);
        marquee.Update(new(-5, view.RowBounds(4).Top + 2));
        marquee.End();
        var toggled = Selected(view);

        // A click in the margin, without Ctrl, clears the selection.
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.End();

        await Verify(
            new
            {
                dragged,
                toggled,
                clicked = Selected(view)
            });
    }

    // Hiding a column takes it and its cells out of the list, and sorting by it falls back to
    // the name. Showing it again puts it back in its place.
    [Test]
    public async Task HideAndShowColumns()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false };
        using var view = await Open(fixture.Root, settings);
        view.SortBy(typeColumn);

        view.SetColumnShown(typeColumn, false);
        var hidden = new
        {
            columns = view.ColumnTitles.ToList(),
            firstRow = view.FirstRowCells.ToList(),
            rows = view.Entries.Select(_ => _.Name).ToList(),
            setting = settings.HiddenColumns.ToList()
        };

        view.SetColumnShown(typeColumn, true);

        await Verify(
            new
            {
                hidden,
                shownAgain = new
                {
                    columns = view.ColumnTitles.ToList(),
                    firstRow = view.FirstRowCells.ToList(),
                    setting = settings.HiddenColumns.ToList()
                }
            });
    }

    // Name stays ticked and cannot be unticked.
    [Test]
    public async Task HeaderMenu()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false, HiddenColumns = ["Size"] };
        using var view = await Open(fixture.Root, settings);
        using var menu = view.HeaderMenu(clickedColumn: 1);

        await Verify(
            new
            {
                columns = view.ColumnTitles.ToList(),
                menu = menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem).ToList()
            });
    }

    static string DescribeMenuItem(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        var text = item.Text!;
        if (item is ToolStripMenuItem { Checked: true })
        {
            text = $"[x] {text}";
        }

        if (!item.Enabled)
        {
            text += " (disabled)";
        }

        return text;
    }

    // A folder Windows marks hidden and system is left out, even with hidden items shown,
    // until system files are shown too.
    [Test]
    public async Task SystemFilesWhenShown()
    {
        using var fixture = Fixture.Create();
        var protectedFolder = Directory.CreateDirectory(fixture.At("Protected"));
        protectedFolder.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        var settings = new Settings { ShowHidden = true };
        using var view = await Open(fixture.Root, settings);
        var withoutSystem = view.Entries.Select(_ => _.Name).ToList();

        settings.ShowSystem = true;
        view.Reload();
        await Pump.Loaded(view);

        await Verify(
            new
            {
                withoutSystem,
                withSystem = view.Entries.Select(_ => _.Name).ToList()
            });
    }

    static List<string> Selected(FolderView view) =>
        view.SelectedEntries().Select(_ => _.Name).ToList();

    static async Task<FolderView> Open(string path, Settings? settings = null)
    {
        // Hidden items off unless a test asks: most tests are about everything else.
        var view = new FolderView(settings ?? new() { ShowHidden = false }, noPlaces);
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static object Describe(FolderView view, Fixture fixture) =>
        new
        {
            path = fixture.Relative(view.CurrentPath),
            status = view.StatusText,
            filter = view.Filter,
            rows = view.Entries.Select(Row),
            selected = view.SelectedEntries().Select(_ => _.Name)
        };

    static string Row(FileEntry entry)
    {
        var kind = entry.IsDirectory ? "folder" : "file  ";
        var hidden = entry.IsHidden ? "  (hidden)" : "";
        var size = entry.IsDirectory ? "" : $"  {entry.Size:N0} bytes";
        return $"{kind}  {entry.Name}{size}{hidden}";
    }
}
