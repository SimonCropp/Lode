#if DEBUG
/// <summary>
/// A file's changes where each placement shows them, the Changes pane beside them. Debug only, as
/// the other captures of the window are.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class DiffPlacementCaptureTests
{
    // The Diff tab, beside the folder's tab, the pane staying beside it.
    [Test]
    public Task Tab() =>
        CaptureWindow(DiffPlacement.Tab);

    // Over the folder list, with the × that puts it away.
    [Test]
    public Task Folder() =>
        CaptureWindow(DiffPlacement.Folder);

    // A window of its own, the files changed down its left.
    [Test]
    public Task Window() =>
        CaptureDiffWindow(PaneStubs.CommitFiles, 1, PaneStubs.History[1]);

    // A window of its own for what is uncommitted: its heading says so, and the repository.
    [Test]
    public Task WindowUncommitted() =>
        CaptureDiffWindow(PaneStubs.Files, 0, null);

    static async Task CaptureDiffWindow(IReadOnlyList<Change> files, int picked, GitCommit? commit)
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Window;
        using var form = await Show(fixture, settings);
        var source = new DiffSource(fixture.Root, files[picked], commit);

        form.DiffHost.Request(new(fixture.Root, source, files, commit, 1, Open: true));

        using var window = form.DiffHost.Window!;
        window.Location = new(-20000, -20000);
        window.Size = new(1100, 560);
        await Pump.Until(() => window.View.Reading.IsCompleted && window.View.Source != null);
        window.ActiveControl = null;
        Application.DoEvents();
        await Verify(window);
    }

    static async Task CaptureWindow(DiffPlacement placement)
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = placement;
        using var form = await Show(fixture, settings);

        var list = form.GitPanes.ChangesPane.List;
        list.SelectedIndices.Add(5);
        await Pump.Until(() => (form.DiffHost.TabView ?? form.DiffHost.Overlay)?.Source != null);
        var view = (form.DiffHost.TabView ?? form.DiffHost.Overlay)!;
        await Pump.Until(() => view.Reading.IsCompleted);
        form.ActiveControl = null;
        Application.DoEvents();
        await Verify(form);
    }

    static async Task<QuietForm> Show(Fixture fixture, Settings settings)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm([fixture.Root], settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            // Wider than CI's screen, which Windows lets a window be only with a maximum size of
            // its own: WindowSize has it so. Then the size it opens at.
            MaximumSize = new(10_000, 10_000)
        };
        WindowSize.Set(form, form.LogicalToDeviceUnits(1200), form.LogicalToDeviceUnits(700));
        form.Show();
        await PaneStubs.Settled(form);
        return form;
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
#endif
