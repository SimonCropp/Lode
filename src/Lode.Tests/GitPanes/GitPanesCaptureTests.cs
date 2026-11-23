#if DEBUG
/// <summary>
/// The Changes and History panes, drawn in the window: pinned beside the list, one or both, and
/// slid out over it. Debug only, as the other captures of the window are.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class GitPanesCaptureTests
{
    // Neither pinned: the strip down the right edge, with a tab for each.
    [Test]
    public Task Strip() =>
        Capture(changes: false, history: false);

    // The names across, under the icons, Changes' count in a badge beside its icon.
    [Test]
    public Task StripHorizontal() =>
        Capture(changes: false, history: false, labels: StripLabels.Horizontal);

    // The icons alone, Changes' count in a badge beside its icon.
    [Test]
    public Task StripIconsOnly() =>
        Capture(changes: false, history: false, labels: StripLabels.IconsOnly);

    // The filter shown, from the button beside how many files have changed, which shows it on.
    [Test]
    public Task ChangesFilter() =>
        Capture(
            changes: true,
            history: false,
            form => form.GitPanes.ChangesPane.ShowFilter(true));

    // The description shown, from the button beside the summary, which shows it on.
    [Test]
    public Task ChangesDescription() =>
        Capture(
            changes: true,
            history: false,
            form => form.GitPanes.ChangesPane.ShowDescription(true, focus: false));

    [Test]
    public Task ChangesPinned() =>
        Capture(changes: true, history: false);

    // A commit picked, with the files it changed under it.
    [Test]
    public Task HistoryPinned() =>
        Capture(
            changes: false,
            history: true,
            form =>
            {
                var commits = form.GitPanes.HistoryPane.CommitList;
                commits.SelectedIndices.Add(1);
            });

    [Test]
    public Task BothPinned() =>
        Capture(changes: true, history: true);

    // Slid out over the list from the strip, the other pane still a tab on it.
    [Test]
    public Task ChangesSlidOut() =>
        Capture(
            changes: false,
            history: false,
            form => form.GitPanes.Float(GitPane.Changes, focus: false));

    // Slid out over the pinned Changes, as Visual Studio's hidden windows do.
    [Test]
    public Task HistorySlidOutOverChanges() =>
        Capture(
            changes: true,
            history: false,
            form => form.GitPanes.Float(GitPane.History, focus: false));

    static async Task Capture(bool changes, bool history, Action<MainForm>? arrange = null, StripLabels labels = StripLabels.Vertical)
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = PaneStubs.InRepository(fixture);
        settings.SetPinnedPanes(fixture.Root, changes, history);
        settings.StripLabels = labels;
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], settings, places);
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        form.Size = new(1200, 700);
        form.Show();
        await PaneStubs.Settled(form);
        if (arrange != null)
        {
            arrange(form);
            await PaneStubs.Settled(form);
        }

        form.ActiveControl = null;
        Application.DoEvents();
        if (form.GitPanes.Floating is not { } floating)
        {
            await Verify(form);
            return;
        }

        // DrawToBitmap draws a control's children in the reverse of the order they are on
        // screen, so the pane over the list would be drawn under it. The window, then the pane
        // where it is over it.
        await Verify(Composite(form, floating), "png");
    }

    static MemoryStream Composite(MainForm form, GitPane floating)
    {
        Control pane = floating == GitPane.Changes ? form.GitPanes.ChangesPane : form.GitPanes.HistoryPane;
        using var window = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(window, new(Point.Empty, form.Size));
        using var paneImage = new Bitmap(pane.Width, pane.Height);
        pane.DrawToBitmap(paneImage, new(Point.Empty, pane.Size));
        var at = pane.PointToScreen(Point.Empty);
        using (var graphics = Graphics.FromImage(window))
        {
            graphics.DrawImage(paneImage, at.X - form.Left, at.Y - form.Top);
        }

        var stream = new MemoryStream();
        window.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return stream;
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
#endif
