#if DEBUG
/// <summary>
/// The Changes and History panes, drawn in the window, for how wide it is: over the list, slid
/// out from the strip, beside it as tabs, and side by side. Debug only, as the other captures of
/// the window are.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class GitPanesCaptureTests
{
    // No room beside the list: the strip down the right edge, with a tab for each.
    [Test]
    public Task Strip() =>
        Capture(PaneLayout.Narrow);

    // The names across, under the icons, Changes' count in a badge beside its icon.
    [Test]
    public Task StripHorizontal() =>
        Capture(PaneLayout.Narrow, labels: StripLabels.Horizontal);

    // The icons alone, Changes' count in a badge beside its icon.
    [Test]
    public Task StripIconsOnly() =>
        Capture(PaneLayout.Narrow, labels: StripLabels.IconsOnly);

    // The filter shown, from the button beside how many files have changed, which shows it on.
    [Test]
    public Task ChangesFilter() =>
        Capture(
            PaneLayout.Medium,
            arrange: form => form.GitPanes.ChangesPane.ShowFilter(true));

    // The description shown, from the button beside the summary, which shows it on.
    [Test]
    public Task ChangesDescription() =>
        Capture(
            PaneLayout.Medium,
            arrange: form => form.GitPanes.ChangesPane.ShowDescription(true, focus: false));

    // A merge left part way by conflicts: the bar over the files, each in conflict saying how it
    // is left to settle, the summary as git's message, and Commit merge held back.
    [Test]
    public Task ChangesConflicted() =>
        Capture(
            PaneLayout.Medium,
            arrange: form =>
            {
                ChangesPane.Read = (_, _) => PaneStubs.Conflicted;
                form.GitPanes.ChangesPane.Reload();
            });

    // Room for one beside the list: the two as tabs along its top, Changes showing.
    [Test]
    public Task ChangesTab() =>
        Capture(PaneLayout.Medium);

    // Nothing uncommitted: saying so in place of the files, with the commit not pushed to push.
    [Test]
    public Task ChangesNone() =>
        Capture(
            PaneLayout.Medium,
            arrange: form =>
            {
                FolderView.ReadGit = (_, _) => new("main", PaneStubs.History[0].Sha, 1, 0, [])
                {
                    UpstreamName = "origin/main"
                };
                ChangesPane.Read = (_, _) => new("main", true, false, []);
                form.Reload();
            });

    // History's tab chosen, a commit picked, with the files it changed under it.
    [Test]
    public Task HistoryTab() =>
        Capture(
            PaneLayout.Medium,
            GitPane.History,
            form =>
            {
                var commits = form.GitPanes.HistoryPane.CommitList;
                commits.SelectedIndices.Add(1);
            });

    // No commit picked, the one picked clicked again: the heading says to pick one, with no files
    // under it. Once the double-click time has passed, when the list would pick the row again
    // unless stopped.
    [Test]
    public Task HistoryNonePicked() =>
        Capture(
            PaneLayout.Medium,
            GitPane.History,
            form =>
            {
                var commits = form.GitPanes.HistoryPane.CommitList;
                Click(commits, commits.SelectedIndices[0]);
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200))
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
            });

    // A click on a row as Windows sends one: the button up waiting in the queue, for the list,
    // which doesn't come back from the press until the button is up, to find.
    static void Click(RowList list, int index)
    {
        var row = list.GetItemRect(index);
        var point = (IntPtr) ((row.Top + row.Height / 2) << 16 | (row.Left + list.LogicalToDeviceUnits(40)));
        PostMessage(list.Handle, 0x0202, 0, point);
        NativeMethods.SendMessage(list.Handle, 0x0201, 1, point);
        Application.DoEvents();
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    // Compare to branch: the commits behind the branch compared with, how many each way, and
    // the button merging them in.
    [Test]
    public Task HistoryComparing() =>
        Capture(
            PaneLayout.Medium,
            GitPane.History,
            form =>
            {
                HistoryPane.ReadRange = (_, _, skip, count, _) => PaneStubs.History.Take(3).Skip(skip).Take(count).ToList();
                HistoryPane.ReadApart = (_, _, _) => (2, 3);
                form.GitPanes.Compare(form.GitPanes.Repo!, "develop");
            });

    // Room for both beside the list: side by side, Changes then History.
    [Test]
    public Task BothBeside() =>
        Capture(PaneLayout.Wide);

    // Slid out over the list from the strip, the other pane still a tab on it.
    [Test]
    public Task ChangesSlidOut() =>
        Capture(
            PaneLayout.Narrow,
            arrange: form => form.GitPanes.Float(GitPane.Changes, focus: false));

    static async Task Capture(PaneLayout layout, GitPane tab = GitPane.Changes, Action<MainForm>? arrange = null, StripLabels labels = StripLabels.Vertical)
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.CreateAtFixedPath();
        var settings = PaneStubs.InRepository(fixture);
        settings.SetPaneTab(fixture.Root, tab);
        settings.StripLabels = labels;
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], settings, places);
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);

        // Wider than CI's screen, which Windows lets a window be only with a maximum size of its
        // own: WindowSize has it so.
        form.MaximumSize = new(10_000, 10_000);
        WindowSize.Set(form, form.LogicalToDeviceUnits(WidthFor(layout)), form.LogicalToDeviceUnits(700));
        form.Show();
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Layout).IsEqualTo(layout);
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

    // How wide the window is, at 100% scale, for the panes to go where asked.
    static int WidthFor(PaneLayout layout)
    {
        if (layout == PaneLayout.Narrow)
        {
            return 900;
        }

        if (layout == PaneLayout.Medium)
        {
            return 1200;
        }

        return 1700;
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
