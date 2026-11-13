#if DEBUG
/// <summary>
/// The whole window, drawn. Pixels depend on the Windows version, theme and fonts of the
/// machine that captured them, so these only run in Debug builds, on a developer's machine.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class MainFormTests
{
    const int sizeColumn = 3;

    [Test]
    public Task Default() =>
        Capture();

    // Two levels down: the tree expanded to the folder, and back and up available.
    [Test]
    public Task Subfolder() =>
        Capture(
            (form, fixture) =>
            {
                form.ActiveView!.Navigate(fixture.At("Source"));
                form.ActiveView.Navigate(fixture.At("Source", "Deep"));
            });

    [Test]
    public Task Tabs() =>
        Capture(
            (form, fixture) =>
            {
                form.AddTab(fixture.At("Source"), false);
                form.AddTab(fixture.At("Docs"), true);
            });

    // Typing a path: the box sits on the bar's text line, inside the bar's own border.
    [Test]
    public Task EditingAddress() =>
        Capture(
            (form, _) => ((Breadcrumb)form.Controls.Find("address", true)[0].Parent!).BeginEdit(),
            // Taking the focus away would end the editing.
            keepFocus: true);

    [Test]
    public Task Filtered() =>
        Capture((form, _) => form.Controls.Find("filter", true)[0].Text = "report");

    [Test]
    public Task SortedBySizeDescending() =>
        Capture(
            (form, _) =>
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

    // Too narrow for every column: the address bar gives way and the list scrolls.
    [Test]
    public Task Narrow() =>
        Capture(width: 700);

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

    static async Task Capture(Action<MainForm, Fixture>? arrange = null, bool showHidden = false, int width = 1200, bool keepFocus = false)
    {
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = new Settings { ShowHidden = showHidden };
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], settings, places);

        // Verify.WinForms turns this off before it draws. Changing it on a visible form makes
        // Windows recreate every control's window, which resets which tab page is showing,
        // so it is already off by the time the form is shown.
        form.ShowInTaskbar = false;

        // Shown, off screen: DrawToBitmap of a form that was never shown leaves it blank.
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
