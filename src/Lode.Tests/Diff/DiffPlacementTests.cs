/// <summary>
/// Where a file picked in the Changes or History pane shows its changes, as Options says: the
/// Diff tab, over the folder list, the diff window, or the text editor.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class DiffPlacementTests
{
    // Picked in Changes: the Diff tab, made and shown, the panes staying beside it, on the
    // repository, as the other tabs come and go.
    [Test]
    public async Task Tab()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);

        await Pick(form, 0);

        var host = form.DiffHost;
        await Pump.Until(() => host.TabView?.Source != null);
        await host.TabView!.Reading;
        await Assert.That(host.TabShowing).IsTrue();
        await Assert.That(host.TabView.Source!.File).IsEqualTo(PaneStubs.Files[0]);
        await Assert.That(host.Page!.Text).IsEqualTo("Diff: guide.aleph");
        await Assert.That(form.ActiveView).IsNull();
        await Assert.That(form.GitPanes.Repo).IsEqualTo(fixture.Root);
        await Assert.That(form.GitPanes.ChangesPane.Visible).IsTrue();

        // Back to the folder, and to the diff again: the panes stay.
        form.Tabs.SelectedIndex = 0;
        await Assert.That(form.GitPanes.Repo).IsEqualTo(fixture.Root);
        form.Tabs.SelectedTab = host.Page;
        await Assert.That(form.GitPanes.Repo).IsEqualTo(fixture.Root);
        await Assert.That(form.GitPanes.ChangesPane.Visible).IsTrue();

        // Not a folder's tab: not saved, and not counted as the tab showing among them.
        form.RememberTabs();
        await Assert.That(settings.Tabs).IsEquivalentTo([fixture.Root]);
        await Assert.That(settings.ActiveTab).IsEqualTo(0);

        // Closed, and made again for the next file.
        form.CloseTab(host.Page);
        await Assert.That(host.Page).IsNull();
        await Pick(form, 3);
        await Pump.Until(() => host.TabView?.Source?.File == PaneStubs.Files[3]);
        await Assert.That(host.TabShowing).IsTrue();
    }

    // The Diff tab has no folder: the address says what the tab is, the file's name with it, in
    // place of the folder the tab before it showed, and follows the file picked while it shows.
    // The folder's tab, showing again, has its folder back.
    [Test]
    public async Task TheAddressSaysDiffAndTheFile()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);
        var breadcrumb = (Breadcrumb) form.Controls.Find("address", true)[0].Parent!;
        var folder = breadcrumb.Reads;
        var host = form.DiffHost;

        await Pick(form, 0);
        await Pump.Until(() => host.TabView?.Source != null);
        var first = breadcrumb.Reads;
        await Pick(form, 5);
        await Pump.Until(() => host.TabView?.Source?.File == PaneStubs.Files[5]);
        var second = breadcrumb.Reads;
        form.Tabs.SelectedIndex = 0;
        var backOnTheFolder = breadcrumb.Reads;
        form.Tabs.SelectedTab = host.Page;

        await Assert.That(folder).EndsWith(" › Projects");
        await Assert.That(first).IsEqualTo("Diff: guide.aleph");
        await Assert.That(second).IsEqualTo("Diff: main.aleph");
        await Assert.That(backOnTheFolder).IsEqualTo(folder);
        await Assert.That(breadcrumb.Reads).IsEqualTo("Diff: main.aleph");
    }

    // A folder picked in the tree while the Diff tab shows, which has no folder: the folder's tab
    // that showed before it goes there, and shows in its place, the Diff tab staying open behind.
    [Test]
    public async Task TheTreeGoesToTheTabTheDiffShowedOver()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);
        var view = form.ActiveView!;
        view.Navigate(fixture.At("Docs"));
        await PaneStubs.Settled(form);
        await Pick(form, 0);
        var host = form.DiffHost;
        await Pump.Until(() => host.TabShowing);

        // Pinned's one folder, the repository's.
        form.Tree.SelectByUser(form.Tree.Nodes[0].Nodes[0]);
        await Pump.Loaded(view);

        await Assert.That(form.ActiveView).IsEqualTo(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(host.Page).IsNotNull();
        await Assert.That(host.TabView!.Source!.File).IsEqualTo(PaneStubs.Files[0]);
    }

    // The status bar stays as the folder's tab had it while the Diff tab shows, and acts on what
    // it says there too: the commits ahead clicked offer to push them.
    [Test]
    public async Task StatusBarActsWhileTheTabShows()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);
        await Pick(form, 0);
        await Pump.Until(() => form.DiffHost.TabShowing);

        var ask = GitStatusActions.Ask;
        GitStatusActions.Question? asked = null;
        GitStatusActions.Ask = (_, question) =>
        {
            asked = question;
            return null;
        };
        try
        {
            var ahead = form.StatusBar.Strip.Items["gitAhead"]!;
            await Assert.That(ahead.Text).IsEqualTo("1 ahead");
            ahead.PerformClick();
            await Pump.Until(() => asked != null);
        }
        finally
        {
            GitStatusActions.Ask = ask;
        }

        await Assert.That(asked!.Caption).IsEqualTo("Git push");
        await Assert.That(asked.Heading).StartsWith("main is 1 commit ahead of ");

        // And the branch clicked opens the branches, to switch to.
        var branch = form.StatusBar.Strip.Items["git"]!;
        await Assert.That(branch.Text).IsEqualTo("main");
        branch.PerformClick();
        var picker = form.OwnedForms.OfType<BranchPicker>().SingleOrDefault();
        try
        {
            await Assert.That(picker).IsNotNull();
        }
        finally
        {
            picker?.Close();
        }
    }

    // Several picked: said so, in the tab already showing a diff.
    [Test]
    public async Task SeveralPicked()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        using var form = await Show(fixture.Root, settings);
        await Pick(form, 0);
        var host = form.DiffHost;
        await Pump.Until(() => host.TabView?.Source != null);

        await Pick(form, 0, 3);

        await Pump.Until(() => host.TabView!.MessageText != null);
        await Assert.That(host.TabView!.MessageText).IsEqualTo("2 files picked");
    }

    // Over the folder list, the folder still the tab's; gone once nothing is picked, the tab
    // changes, or Escape is pressed in it.
    [Test]
    public async Task Folder()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Folder;
        using var form = await Show(fixture.Root, settings);
        var host = form.DiffHost;

        await Pick(form, 0);

        await Pump.Until(() => host.Overlaid);
        await Assert.That(host.Overlay!.Parent).IsEqualTo(form.ActiveView!.Parent);
        await Assert.That(host.Overlay.ShowsClose).IsTrue();
        await Assert.That(host.Page).IsNull();
        await Assert.That(form.GitPanes.Repo).IsEqualTo(fixture.Root);

        await Pick(form);
        await Pump.Until(() => !host.Overlaid);

        await Pick(form, 1);
        await Pump.Until(() => host.Overlaid);
        host.CloseOverlay(focus: true);
        await Assert.That(host.Overlaid).IsFalse();
    }

    // The window opens for a file asked for outright, and then follows what is picked; picking
    // alone opens none.
    [Test]
    public async Task Window()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Window;
        using var form = await Show(fixture.Root, settings);
        var host = form.DiffHost;

        await Pick(form, 0);
        await Pump.For(TimeSpan.FromMilliseconds(100));
        await Assert.That(host.Window).IsNull();

        var source = new DiffSource(fixture.Root, PaneStubs.Files[3]);
        host.Request(new(fixture.Root, source, PaneStubs.Files, null, 1, Open: true));
        var window = host.Window!;
        try
        {
            await Pump.Until(() => window.View.Source != null);
            await Assert.That(window.Files.SelectedIndices.Cast<int>()).IsEquivalentTo([3]);
            await Assert.That(window.Text).StartsWith("Uncommitted changes – ");

            // Picked down its own list.
            window.Files.SelectedIndices.Clear();
            window.Files.SelectedIndices.Add(5);
            await Pump.Until(() => window.View.Source?.File == PaneStubs.Files[5]);

            // And from the pane.
            await Pick(form, 1);
            await Pump.Until(() => window.View.Source?.File == PaneStubs.Files[1]);
        }
        finally
        {
            window.Close();
        }
    }

    // Ctrl+F with the focus in the diff over the folder list opens the diff's find bar, the
    // focus in it, rather than going to the folder's search box as the window's Ctrl+F does, and
    // F3 there goes to the next match rather than to the search box too. Escape closes the bar,
    // the focus back in the diff, which stays till Escape is pressed again. Outside the diff,
    // Ctrl+F is still the search box's.
    [Test]
    public async Task CtrlFInTheDiffFindsInIt()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Folder;
        using var form = await Show(fixture.Root, settings);
        await Pick(form, 0);
        var host = form.DiffHost;
        await Pump.Until(() => host.Overlay?.Source != null);
        var view = host.Overlay!;
        await Pump.Until(() => view.Reading.IsCompleted);
        var search = Descendants(form).OfType<TextBox>().Single(_ => _.Name == "filter");

        view.Canvas.Focus();
        await Assert.That(Keystroke.Press(view.Canvas, Keys.Control | Keys.F)).IsTrue();
        await Assert.That(view.FindBar.Visible).IsTrue();
        await Assert.That(view.FindBar.Box.Focused).IsTrue();
        await Assert.That(search.Focused).IsFalse();

        view.FindBar.Box.Text = "previous";
        view.Canvas.Focus();
        Keystroke.Press(view.Canvas, Keys.F3);
        await Assert.That(view.Canvas.CurrentMatch).IsEqualTo(1);
        await Assert.That(view.Canvas.Focused).IsTrue();

        view.FindBar.FocusBox();
        Keystroke.Press(view.FindBar.Box, Keys.Escape);
        await Assert.That(view.FindBar.Visible).IsFalse();
        await Assert.That(view.Canvas.Focused).IsTrue();
        await Assert.That(host.Overlaid).IsTrue();

        var list = form.GitPanes.ChangesPane.List;
        list.Focus();
        Keystroke.Press(list, Keys.Control | Keys.F);
        await Assert.That(search.Focused).IsTrue();
        await Assert.That(view.FindBar.Visible).IsFalse();
    }

    // And in the Diff tab, where the folder's search box is greyed, having no folder.
    [Test]
    public async Task CtrlFInTheDiffTabFindsInIt()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);
        await Pick(form, 0);
        var host = form.DiffHost;
        await Pump.Until(() => host.TabView?.Source != null);
        var view = host.TabView!;
        await Pump.Until(() => view.Reading.IsCompleted);

        view.Canvas.Focus();
        await Assert.That(Keystroke.Press(view.Canvas, Keys.Control | Keys.F)).IsTrue();
        await Assert.That(view.FindBar.Visible).IsTrue();
        await Assert.That(view.FindBar.Box.Focused).IsTrue();

        Keystroke.Press(view.FindBar.Box, Keys.Escape);
        await Assert.That(view.FindBar.Visible).IsFalse();
        await Assert.That(view.Canvas.Focused).IsTrue();
        await Assert.That(host.TabShowing).IsTrue();
    }

    // And in the diff window, which Escape closes only once the find bar is closed.
    [Test]
    public async Task CtrlFInTheDiffWindowFindsInIt()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Window;
        using var form = await Show(fixture.Root, settings);
        var host = form.DiffHost;
        host.Request(new(fixture.Root, new(fixture.Root, PaneStubs.Files[3]), PaneStubs.Files, null, 1, Open: true));
        var window = host.Window!;
        try
        {
            var view = window.View;
            await Pump.Until(() => view.Source != null && view.Reading.IsCompleted);

            view.Canvas.Focus();
            await Assert.That(Keystroke.Press(view.Canvas, Keys.Control | Keys.F)).IsTrue();
            await Assert.That(view.FindBar.Visible).IsTrue();
            await Assert.That(view.FindBar.Box.Focused).IsTrue();

            Keystroke.Press(view.FindBar.Box, Keys.Escape);
            await Assert.That(view.FindBar.Visible).IsFalse();
            await Assert.That(view.Canvas.Focused).IsTrue();
            await Assert.That(window.Visible).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }

    static IEnumerable<Control> Descendants(Control control) =>
        control.Controls
            .Cast<Control>()
            .SelectMany(_ => Descendants(_).Prepend(_));

    // In the text editor: nothing of Lode's own shows when a file is picked.
    [Test]
    public async Task Editor()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Editor;
        using var form = await Show(fixture.Root, settings);

        await Pick(form, 0);
        await Pump.For(TimeSpan.FromMilliseconds(100));

        await Assert.That(form.DiffHost.Page).IsNull();
        await Assert.That(form.DiffHost.Overlaid).IsFalse();
        await Assert.That(form.DiffHost.Window).IsNull();
    }

    // History's files: the commit's change.
    [Test]
    public async Task FromHistory()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.SetPaneTab(fixture.Root, GitPane.History);
        using var form = await Show(fixture.Root, settings);
        var history = form.GitPanes.HistoryPane;
        history.CommitList.SelectedIndices.Add(1);
        await Pump.Until(() => history.ReadingFiles.IsCompleted && history.FileList.VirtualListSize > 0);

        history.FileList.SelectedIndices.Add(2);

        var host = form.DiffHost;
        await Pump.Until(() => host.TabView?.Source != null);
        var source = host.TabView!.Source!;
        await Assert.That(source.Commit).IsEqualTo(PaneStubs.History[1]);
        await Assert.That(source.File).IsEqualTo(PaneStubs.CommitFiles[2]);

        // A commit's lines aren't discarded.
        await host.TabView.Reading;
        var canvas = host.TabView.Canvas;
        var added = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Added);
        await Assert.That(canvas.MenuCommands(added, right: false).Any(_ => _?.Text.StartsWith("Discard", StringComparison.Ordinal) == true)).IsFalse();
    }

    // Lines discarded in the Diff tab: the Changes pane reads the repository again, and the diff
    // its file, as after Discard changes in Changes.
    [Test]
    public async Task DiscardingReadsAgain()
    {
        using var stubs = new PaneStubs();
        using var diff = DiffSample.Stub();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        settings.DiffPlacement = DiffPlacement.Tab;
        using var form = await Show(fixture.Root, settings);
        await Pick(form, 0);
        var host = form.DiffHost;
        await Pump.Until(() => host.TabView?.Source != null);
        var view = host.TabView!;
        await view.Reading;

        var changesRead = 0;
        var readChanges = ChangesPane.Read;
        ChangesPane.Read = (repo, cancel) =>
        {
            Interlocked.Increment(ref changesRead);
            return readChanges(repo, cancel);
        };
        var diffsRead = 0;
        var readDiff = DiffView.Read;
        DiffView.Read = (source, hideWhitespace, evenIfLarge, cancel) =>
        {
            Interlocked.Increment(ref diffsRead);
            return readDiff(source, hideWhitespace, evenIfLarge, cancel);
        };
        IReadOnlyCollection<int>? discarded = null;
        var discardLines = DiffView.DiscardLines;
        DiffView.DiscardLines = (source, _, lines, _, _) =>
        {
            discarded = lines;
            return Task.FromResult(new CodeCommands.RepoResult(source.Repo, true, ""));
        };
        var ask = ChangesPane.AskDiscard;
        ChangesPane.AskDiscard = (_, _) => DialogResult.OK;
        try
        {
            var canvas = view.Canvas;
            var added = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Added);
            var index = canvas.Rows[added].After!.Index!.Value;

            canvas.MenuCommands(added, right: false).Single(_ => _?.Text == "Discard line…")!.Action();
            await view.Discarding;
            await Pump.Until(() => changesRead > 0 && diffsRead > 0);
            await Pump.Until(() => form.GitPanes.ChangesPane.Reading.IsCompleted && view.Reading.IsCompleted);

            await Assert.That(discarded).IsEquivalentTo([index]);
        }
        finally
        {
            ChangesPane.Read = readChanges;
            DiffView.Read = readDiff;
            DiffView.DiscardLines = discardLines;
            ChangesPane.AskDiscard = ask;
            ActionLog.Reset();
        }
    }

    static Task Pick(MainForm form, params int[] rows)
    {
        var list = form.GitPanes.ChangesPane.List;
        list.SelectedIndices.Clear();
        foreach (var row in rows)
        {
            list.SelectedIndices.Add(row);
        }

        return Pump.For(TimeSpan.FromMilliseconds(30));
    }

    static async Task<QuietForm> Show(string folder, Settings settings)
    {
        var places = new Places([folder], [], ShowComputer: false, _ => "");
        var form = new QuietForm([folder], settings, places)
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
