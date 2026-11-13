#if DEBUG
/// <summary>
/// The menus and the command palette, drawn: the readme shows these. Pixels depend on the
/// Windows version, theme and fonts of the machine that captured them, so, as with
/// <see cref="MainFormTests"/>, these only run in Debug builds.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class MenuTests
{
    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    // In place of this machine's New types, which depend on what is installed.
    static readonly ShellNew.Entry[] newTypes =
    [
        new(".bmp", "Bitmap image"),
        new(".rtf", "Rich Text Document"),
        new(".txt", "Text Document"),
        new(".zip", "Compressed (zipped) Folder")
    ];

    // A folder's right-click menu, in the code folder, so with the Git and NuGet commands.
    [Test]
    public async Task FolderMenu()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false, CodeDirectory = fixture.Root };
        using var view = await Open(fixture.Root, settings);
        var docs = view.Entries.Single(_ => _.Name == "Docs");

        await CaptureMenu(QuickMenu.Build(LodeActions.ForMenu(view.ItemCommands([docs]), settings), () => { }, _ => { }));
    }

    // New, from the right-click menu on empty space: Folder, then each file type with its icon.
    [Test]
    public async Task NewMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() { ShowHidden = false });
        var children = view.BackgroundCommands(canPaste: true, newTypes)[0]!.Children!;

        await CaptureMenu(QuickMenu.Build(children, null, _ => { }));
    }

    // See more, at the end of the actions bar, with a file selected.
    [Test]
    public async Task SeeMore()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);

        await CaptureMenu(QuickMenu.Build(form.OverflowCommands(Point.Empty), null, _ => { }));
    }

    // The ⋯ menu at the right of the address bar.
    [Test]
    public async Task WindowMenu()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture);

        await CaptureMenu(QuickMenu.Build(form.WindowCommands(), null, _ => { }));
    }

    // Right-clicking a column header, with Size hidden.
    [Test]
    public async Task ColumnMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() { ShowHidden = false, HiddenColumns = ["Size"] });

        await CaptureMenu(view.HeaderMenu(clickedColumn: 1));
    }

    // Ctrl+Shift+P, with "close" typed, on the middle of three tabs.
    [Test]
    public async Task CommandPalette()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture);
        form.AddTab(fixture.At("Docs"), true);
        form.AddTab(fixture.At("Source"), false);
        using var palette = new CommandPalette(form.PaletteCommands())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            Search = "close"
        };
        palette.Show();
        Application.DoEvents();

        await Verify(palette);
    }

    static async Task<FolderView> Open(string path, Settings settings)
    {
        var view = new FolderView(settings, noPlaces);
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static async Task<MainForm> Show(Fixture fixture)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm([fixture.Root], new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        return form;
    }

    // A menu is a window of its own, shown off screen here and drawn as it would be on screen.
    static async Task CaptureMenu(ToolStripDropDown menu)
    {
        using (menu)
        {
            menu.Show(new Point(-20000, -20000));
            Application.DoEvents();
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new(Point.Empty, menu.Size));
            menu.Close();
            var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            await Verify(stream, "png");
        }
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
#endif
