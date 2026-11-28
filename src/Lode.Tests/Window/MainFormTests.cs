#if DEBUG
/// <summary>
/// The whole window, drawn. Pixels depend on the Windows version, theme and fonts of the
/// machine that captured them, so these only run in Debug builds, on a developer's machine.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class MainFormTests
{
    const int sizeColumn = 6;

    [Test]
    public Task Default() =>
        Capture();

    // Two levels down: the tree expanded to the folder, and back and up available.
    [Test]
    public Task Subfolder() =>
        Capture((form, fixture) =>
        {
            form.ActiveView!.Navigate(fixture.At("Source"));
            form.ActiveView.Navigate(fixture.At("Source", "Deep"));
        });

    // Inside a zip: what it holds, listed as a folder's, the zip in the address bar.
    [Test]
    public Task InsideAZip() =>
        Capture((form, fixture) =>
        {
            var zip = fixture.At("Bundle.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var (name, size) in new[] {("Docs/guide.aleph", 900), ("Source/main.aleph", 5_000), ("readme.aleph", 120), ("notes.gamma", 12)})
                {
                    var entry = archive.CreateEntry(name);
                    entry.LastWriteTime = new(new(2025, 3, 14, 9, 26, 0));
                    using var stream = entry.Open();
                    stream.Write(new byte[size]);
                }
            }

            form.ActiveView!.Navigate(zip);
        });

    [Test]
    public Task Tabs() =>
        Capture((form, fixture) =>
        {
            form.AddTab(fixture.At("Source"), false);
            form.AddTab(fixture.At("Docs"), true);
        });

    // Typing a path: the box sits on the bar's text line, inside the bar's own border.
    [Test]
    public Task EditingAddress() =>
        Capture(
            (form, _) => ((Breadcrumb) form.Controls.Find("address", true)[0].Parent!).BeginEdit(),
            // Taking the focus away would end the editing.
            keepFocus: true);

    // Searched all the way down, with where each is from beside its name.
    [Test]
    public Task Filtered() =>
        Capture((form, _) => form.Controls.Find("filter", true)[0].Text = "*.aleph");

    // Searched for a name nothing has: a link to search inside the files instead.
    [Test]
    public Task FilteredFindingNothing() =>
        Capture((form, _) => form.Controls.Find("filter", true)[0].Text = "\"11.0");

    // Find in Files: the strip along the bottom, and what it found in the Find Results tab, laid
    // out as Sublime lays it out, each match marked.
    [Test]
    public async Task FindResults()
    {
        using var fixture = Fixture.CreateAtFixedPath();
        await File.WriteAllTextAsync(fixture.At("Docs", "guide.aleph"), "# Guide\n\nThe alpha release\nships the beta.\n\nSee alpha notes.\n");
        await File.WriteAllTextAsync(fixture.At("Source", "main.aleph"), "var alpha = 1;\nvar beta = alpha + 1;\n");
        var settings = new Settings();
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            Size = new(1200, 700)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";

        form.FindHost.Find();
        await form.FindHost.ResultsView!.Searching;
        form.ActiveControl = null;
        Application.DoEvents();

        await Verify(form);
    }

    [Test]
    public Task SortedBySizeDescending() =>
        Capture((form, _) =>
        {
            form.ActiveView!.SortBy(sizeColumn);
            form.ActiveView.SortBy(sizeColumn);
        });

    // Hidden items are listed, dimmed.
    [Test]
    public Task HiddenItems() =>
        Capture(showHidden: true);

    [Test]
    public Task Selection() =>
        Capture((form, fixture) => form.ActiveView!.Reveal(fixture.At("report10.beta")));

    // Too narrow for every column or every button: the address bar gives way, the list scrolls,
    // and the buttons that don't fit go under See more.
    [Test]
    public Task Narrow() =>
        Capture(width: 700);

    // Large icons: each item's thumbnail, or as here its icon, big, with its name under it. A
    // folder's picture shows what is in it once the shell has looked, which it may not have yet,
    // so here each folder shows its icon, and the capture is the same every run.
    [Test]
    public Task LargeIcons() =>
        Capture((form, _) =>
        {
            var view = form.ActiveView!;
            view.ReadThumbnail = (path, size) => Directory.Exists(path) ? null : Thumbnails.FromShell(path, size);
            view.ShowLargeIcons(true);
        });

    // A newer Lode, beside the free space.
    [Test]
    public async Task UpdateAvailable()
    {
        var previous = Updates.Instance;
        Updates.Instance = TestUpdates.Installed(
            new()
            {
                AvailableUpdate = "0.4.0"
            });

        try
        {
            await Capture();
        }
        finally
        {
            Updates.Instance = previous;
        }
    }

    // The actions panel under the tree: one command running, one done, one failed.
    [Test]
    public async Task Actions()
    {
        var running = new TaskCompletionSource<ActionLog.Outcome>();
        try
        {
            await ActionLog.Run("Git pull", @"C:\Code\Lode", () => Task.FromResult(new ActionLog.Outcome(true, "Done", "")));
            await ActionLog.Run("Git push", @"C:\Code", () => Task.FromResult(new ActionLog.Outcome(false, "1 of 12 failed", "")));
            _ = ActionLog.Run("Update NuGets", @"C:\Code\CountryData", () => running.Task);

            await Capture();
        }
        finally
        {
            running.SetResult(new(true, "Done", ""));
            ActionLog.Reset();
        }
    }

    // More actions than the panel shows at once: the list scrolls, and Clear stays in line with
    // its rows rather than over its scrollbar.
    [Test]
    public async Task ActionsScrolling()
    {
        try
        {
            for (var index = 1; index <= 8; index++)
            {
                await ActionLog.Run($"Git pull {index}", @"C:\Code\Lode", () => Task.FromResult(new ActionLog.Outcome(true, "Done", "")));
            }

            await Capture();
        }
        finally
        {
            ActionLog.Reset();
        }
    }

    static async Task Capture(Action<MainForm, Fixture>? arrange = null, bool showHidden = false, int width = 1200, bool keepFocus = false)
    {
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = new Settings {ShowHidden = showHidden};
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], settings, places);

        // Out of the taskbar. Changing this on a visible form makes Windows recreate every
        // control's window, which resets which tab page is showing, so it is off before the
        // form is shown.
        form.ShowInTaskbar = false;

        // Shown, off screen, here rather than by Verify.WinForms, so the list has loaded (and
        // been arranged) by the time it draws the form where it is.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        form.Size = new(width, 700);
        form.Show();
        await Pump.Loaded(form.ActiveView!);

        if (arrange != null)
        {
            arrange(form, fixture);
            await Pump.Loaded(form.ActiveView!);
        }

        // In Large icons, once every item's picture has been read off the UI thread.
        if (form.ActiveView is {LargeIcons: true} view)
        {
            await Pump.Until(() => view.ThumbnailsRead.Count == view.Entries.Count && !view.ReadingThumbnails);
        }

        // Windows can activate this window when another test closes its own, which gives the
        // list the focus and turns its selection from grey to blue. Take the focus off it, so
        // the capture is the same either way.
        if (!keepFocus)
        {
            form.ActiveControl = null;
        }

        Application.DoEvents();

        await Verify(form);
    }

    // Showing a window normally takes the focus from whatever the developer is doing, and
    // makes the selection colour depend on whether Windows allowed that.
    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
#endif
