/// <summary>
/// How the Changes and History panes follow the window: which are pinned for each repository,
/// sliding out and back, and the keys that do it.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class GitPanesTests
{
    // Slides back only once nothing keeps it out: the pointer, the focus, a menu of its own, a
    // button held down, or a window in front, the Discard question say.
    [Test]
    public async Task ShouldRetract()
    {
        await Assert.That(GitPanes.ShouldRetract(pointerOver: false, focusInside: false, windowActive: true, menuOpen: false, buttonDown: false)).IsTrue();
        await Assert.That(GitPanes.ShouldRetract(pointerOver: true, focusInside: false, windowActive: true, menuOpen: false, buttonDown: false)).IsFalse();
        await Assert.That(GitPanes.ShouldRetract(pointerOver: false, focusInside: true, windowActive: true, menuOpen: false, buttonDown: false)).IsFalse();
        await Assert.That(GitPanes.ShouldRetract(pointerOver: false, focusInside: false, windowActive: false, menuOpen: false, buttonDown: false)).IsFalse();
        await Assert.That(GitPanes.ShouldRetract(pointerOver: false, focusInside: false, windowActive: true, menuOpen: true, buttonDown: false)).IsFalse();
        await Assert.That(GitPanes.ShouldRetract(pointerOver: false, focusInside: false, windowActive: true, menuOpen: false, buttonDown: true)).IsFalse();
    }

    // Outside a repository, no strip and no panes.
    [Test]
    public async Task NothingOutsideARepository()
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.Root, settings);
        await Pump.Loaded(form.ActiveView!);

        await Assert.That(form.GitPanes.Describe()).IsEqualTo("No repository");
        await Assert.That(form.GitPanes.Strip.Visible).IsFalse();
    }

    // Pinned in one repository, a pane stays pinned there, and only there, kept in the settings.
    [Test]
    public async Task PinnedForEachRepository()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History hidden, strip Changes (6) and History");

        form.GitPanes.SetPinned(GitPane.History, true);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History pinned, strip Changes (6)");
        await Assert.That(settings.PinnedPanesOf(fixture.At("Docs"))).IsEqualTo(new(fixture.At("Docs"), false, true));

        form.ActiveView!.Navigate(fixture.At("Source"));
        await PaneStubs.Settled(form);
        await Pump.Until(() => form.GitPanes.Repo == fixture.At("Source"));
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Source: Changes hidden, History hidden, strip Changes (6) and History");

        form.ActiveView.Navigate(fixture.At("Docs"));
        await PaneStubs.Settled(form);
        await Pump.Until(() => form.GitPanes.Repo == fixture.At("Docs"));
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History pinned, strip Changes (6)");

        form.GitPanes.SetPinned(GitPane.Changes, true);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes pinned, History pinned, strip hidden");
        form.GitPanes.SetPinned(GitPane.Changes, false);
        form.GitPanes.SetPinned(GitPane.History, false);
        await Assert.That(settings.PinnedPanes).IsEmpty();
    }

    // Going into a folder of the repository leaves the panes as they are, rather than taking
    // them away while the folder is read.
    [Test]
    public async Task StayInsideTheRepository()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Source"), true, false);
        using var form = await Show(fixture.At("Source"), settings);
        await PaneStubs.Settled(form);
        var repos = new List<string?>();
        form.ActiveView!.StatusChanged += () => repos.Add(form.GitPanes.Repo);

        form.ActiveView.Navigate(fixture.At("Source", "Deep"));
        await PaneStubs.Settled(form);

        await Assert.That(repos.All(_ => _ == fixture.At("Source"))).IsTrue();
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Source: Changes pinned, History hidden, strip History");
    }

    // Ctrl+Shift+G and Ctrl+Shift+H slide a pane out with the focus in it, and back again.
    [Test]
    public async Task KeysSlideThePanes()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);

        Press(form, Keys.Control | Keys.Shift | Keys.H);
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History slid out, strip Changes (6) and History");
        await Assert.That(form.GitPanes.HistoryPane.Commits.Count).IsEqualTo(PaneStubs.History.Count);

        Press(form, Keys.Control | Keys.Shift | Keys.G);
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes slid out, History hidden, strip Changes (6) and History");
        await Assert.That(form.GitPanes.ChangesPane.Changes.Files.Count).IsEqualTo(PaneStubs.Files.Count);

        Press(form, Keys.Control | Keys.Shift | Keys.G);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History hidden, strip Changes (6) and History");
    }

    // Pinned in one window, a pane is pinned in another showing the same repository too.
    [Test]
    public async Task PinnedInEveryWindow()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var first = await Show(fixture.At("Docs"), settings);
        using var second = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(first);
        await PaneStubs.Settled(second);

        first.GitPanes.SetPinned(GitPane.Changes, true);

        await Assert.That(second.GitPanes.Describe()).IsEqualTo("Docs: Changes pinned, History hidden, strip History");
    }

    // Chosen from the strip's right-click menu in one window, the tabs are labelled that way in
    // every window, and kept: the strip as wide as its labels need.
    [Test]
    public async Task StripLabelsInEveryWindow()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var first = await Show(fixture.At("Docs"), settings);
        using var second = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(first);
        await PaneStubs.Settled(second);
        var strip = second.GitPanes.Strip;
        var vertical = strip.Width;
        var ticked = first.GitPanes.Strip.MenuCommands().Single(_ => _!.Checked)!;
        await Assert.That(ticked.Text).IsEqualTo("Vertical names");

        first.GitPanes.Strip.MenuCommands().Single(_ => _!.Text == "Horizontal names")!.Action();

        await Assert.That(settings.StripLabels).IsEqualTo(StripLabels.Horizontal);
        await Assert.That(strip.Labels).IsEqualTo(StripLabels.Horizontal);
        await Assert.That(strip.Width).IsGreaterThan(vertical);
        await Assert.That(strip.Width).IsEqualTo(strip.FitWidth());

        first.GitPanes.Strip.MenuCommands().Single(_ => _!.Text == "Icons only")!.Action();

        await Assert.That(strip.Labels).IsEqualTo(StripLabels.IconsOnly);
        await Assert.That(strip.Width).IsEqualTo(strip.LogicalToDeviceUnits(28));
        var tabs = strip.Tabs();
        await Assert.That(tabs.Count).IsEqualTo(2);
        await Assert.That(tabs.All(_ => _.Bounds.Height == strip.LogicalToDeviceUnits(32))).IsTrue();

        // What a screen reader is told is the same however the tabs are labelled.
        await Assert.That(second.GitPanes.Describe()).IsEqualTo("Docs: Changes hidden, History hidden, strip Changes (6) and History");
    }

    // The badge on Changes' icon: none for no changes, up to 99, then 99+.
    [Test]
    public async Task BadgeText()
    {
        await Assert.That(EdgeStrip.BadgeText(0)).IsNull();
        await Assert.That(EdgeStrip.BadgeText(7)).IsEqualTo("7");
        await Assert.That(EdgeStrip.BadgeText(99)).IsEqualTo("99");
        await Assert.That(EdgeStrip.BadgeText(1234)).IsEqualTo("99+");
    }

    // Over a file's status icon, what it stands for; elsewhere on the row, the file and what happened
    // to it; off the rows, nothing.
    [Test]
    public async Task ChangeTips()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), true, false);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var list = form.GitPanes.ChangesPane.List;
        var shown = PaneStubs.Files;

        Point Status(int index)
        {
            var row = list.GetItemRect(index);
            row.Width = list.Columns[0].Width;
            var status = ChangeRows.StatusBounds(list, row);
            return new(status.Left + status.Width / 2, status.Top + status.Height / 2);
        }

        await Assert.That(ChangeRows.TipAt(list, Status(0), shown)).IsEqualTo("Modified");
        await Assert.That(ChangeRows.TipAt(list, Status(1), shown)).IsEqualTo("Deleted");
        await Assert.That(ChangeRows.TipAt(list, Status(2), shown)).IsEqualTo("Renamed from report2.beta");
        await Assert.That(ChangeRows.TipAt(list, Status(4), shown)).IsEqualTo("Untracked: new to git");

        var name = list.GetItemRect(0);
        await Assert.That(ChangeRows.TipAt(list, new(name.Left + list.LogicalToDeviceUnits(60), name.Top + name.Height / 2), shown)).IsEqualTo("Docs/guide.aleph, modified");
        await Assert.That(ChangeRows.TipAt(list, new(4, list.ClientSize.Height - 2), shown)).IsNull();
    }

    // The filter is hidden until its button shows it, with the focus in it; hidden again, by the
    // button or Escape, it filters nothing, every file listed again.
    [Test]
    public async Task FilterHiddenUntilAskedFor()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), true, false);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;
        await Assert.That(pane.Filtering).IsFalse();
        await Assert.That(pane.FilterBox.Visible).IsFalse();

        pane.FilterButton.PerformClick();
        await Assert.That(pane.Filtering).IsTrue();
        await Assert.That(pane.FilterButton.Checked).IsTrue();
        await Assert.That(pane.FilterBox.Visible).IsTrue();
        await Assert.That(pane.FilterBox.Focused).IsTrue();

        pane.FilterBox.Text = "Deep";
        await Assert.That(pane.List.VirtualListSize).IsEqualTo(2);

        pane.FilterButton.PerformClick();
        await Assert.That(pane.Filtering).IsFalse();
        await Assert.That(pane.FilterBox.Text).IsEqualTo("");
        await Assert.That(pane.List.VirtualListSize).IsEqualTo(PaneStubs.Files.Count);

        pane.FilterButton.PerformClick();
        pane.FilterBox.Text = "Deep";

        // Escape as the box gets it, through the keys of each control it is in.
        var escape = Message.Create(pane.FilterBox.Handle, 0x0100, (IntPtr) Keys.Escape, IntPtr.Zero);
        pane.FilterBox.PreProcessMessage(ref escape);
        await Assert.That(pane.Filtering).IsFalse();
        await Assert.That(pane.List.VirtualListSize).IsEqualTo(PaneStubs.Files.Count);
        await Assert.That(pane.List.Focused).IsTrue();
    }

    // The description is hidden until its button shows it, with the focus in it. Holding something
    // it stays, the button only putting the focus there, so nothing goes into the commit unseen;
    // empty, the button or Escape puts it away. Filled from elsewhere, a draft say, it shows.
    [Test]
    public async Task DescriptionHiddenUntilAskedFor()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), true, false);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;
        var box = pane.DescriptionBox;
        await Assert.That(pane.Describing).IsFalse();
        await Assert.That(box.Visible).IsFalse();

        pane.DescribeButton.PerformClick();
        await Assert.That(pane.Describing).IsTrue();
        await Assert.That(pane.DescribeButton.Checked).IsTrue();
        await Assert.That(box.Focused).IsTrue();

        box.Text = "Why";
        pane.SummaryBox.Focus();
        pane.DescribeButton.PerformClick();
        await Assert.That(pane.Describing).IsTrue();
        await Assert.That(box.Focused).IsTrue();
        await Assert.That(box.Text).IsEqualTo("Why");

        box.Text = "";
        pane.DescribeButton.PerformClick();
        await Assert.That(pane.Describing).IsFalse();
        await Assert.That(pane.SummaryBox.Focused).IsTrue();

        pane.DescribeButton.PerformClick();
        var escape = Message.Create(box.Handle, 0x0100, (IntPtr) Keys.Escape, IntPtr.Zero);
        box.PreProcessMessage(ref escape);
        await Assert.That(pane.Describing).IsFalse();

        box.Text = "From a draft";
        await Assert.That(pane.Describing).IsTrue();
        box.Text = "";
    }

    // A commit's menu offers to tag it, and, for one with tags, to delete one of them.
    [Test]
    public async Task CommitMenuTags()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        string Entries(int index) =>
            string.Join(", ", pane.CommitCommands(fixture.At("Docs"), PaneStubs.History[index]).Select(_ => _?.Text ?? "----"));

        await Assert.That(Entries(0)).IsEqualTo("Copy SHA, Copy message, ----, Tag…");
        await Assert.That(Entries(1)).IsEqualTo("Copy SHA, Copy message, ----, Tag…, Delete tag…");
    }

    // Over the arrow of a commit not pushed, and over a tag, what each is; elsewhere on the row,
    // nothing.
    [Test]
    public async Task CommitTips()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;

        // Each row's indicators are at its right, the last of them furthest left.
        Point Right(int index, int fromRight)
        {
            var row = list.GetItemRect(index);
            row.Width = list.Columns[0].Width;
            return new(row.Right - list.LogicalToDeviceUnits(fromRight), row.Top + row.Height / 2);
        }

        await Assert.That(pane.CommitTipAt(Right(0, 26))).IsEqualTo("Not pushed yet");
        await Assert.That(pane.CommitTipAt(Right(1, 30))).IsEqualTo("Tag 0.25.0");
        await Assert.That(pane.CommitTipAt(Right(4, 30))).IsEqualTo("Tag stable");
        var subject = list.GetItemRect(0);
        await Assert.That(pane.CommitTipAt(new(subject.Left + list.LogicalToDeviceUnits(40), subject.Top + subject.Height / 2))).IsNull();
        await Assert.That(pane.CommitTipAt(Right(2, 30))).IsNull();
    }

    // The picked commit's heading draws the line between it and the commits itself, with a
    // commit picked or not, so they never run together, wherever the splitter is.
    [Test]
    public async Task CommitHeadingLine()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;

        await Assert.That(TopLine(pane.Heading)).IsEqualTo(ExplorerStyle.Divider.ToArgb());

        pane.CommitList.SelectedIndices.Add(2);
        await PaneStubs.Settled(form);
        await Assert.That(TopLine(pane.Heading)).IsEqualTo(ExplorerStyle.Divider.ToArgb());
    }

    // A click on the commit picked picks none, putting its files away, and it stays that way once
    // the double-click time has passed, when the list picks the row clicked again unless stopped;
    // another click picks it again, and a click on another commit picks that, as it always did.
    [Test]
    public async Task ClickPickedCommitDeselects()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        list.SelectedIndices.Add(1);
        await PaneStubs.Settled(form);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);

        Click(list, 1);
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(0);
        PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200));
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(0);

        Click(list, 1);
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([1]);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);

        Click(list, 3);
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([3]);
    }

    // The newest commit is picked as the commits show, with its files under it; once the picked
    // commit is clicked again to pick none, reading the commits again leaves none picked.
    [Test]
    public async Task NewestCommitPicked()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([0]);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);

        Click(list, 0);
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);
        PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200));
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);

        pane.Reload();
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(0);
    }

    // A commit picked that is gone once the commits are read again, as an amended one is, gives
    // way to the newest, with its files.
    [Test]
    public async Task PickedCommitGoneNewestPicked()
    {
        using var stubs = new PaneStubs();
        var history = PaneStubs.History.ToList();
        HistoryPane.ReadCommits = (_, skip, count, _) => history.Skip(skip).Take(count).ToList();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        list.SelectedIndices.Add(2);
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([2]);

        history.RemoveAt(2);
        pane.Reload();
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([0]);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);
    }

    // No commit picked, by clicking the picked one again, holds for that repository only: in
    // another, the newest is picked.
    [Test]
    public async Task OtherRepoNewestPicked()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        Click(list, 0);
        PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200));
        await Assert.That(list.SelectedIndices.Count).IsEqualTo(0);

        pane.Bind(fixture.At("Source"));
        await PaneStubs.Settled(form);
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([0]);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);
    }

    // The files a commit changed, scrolled down when the commit is clicked again to pick none,
    // show from their top once it is picked again.
    [Test]
    public async Task FilesFromTopAfterNone()
    {
        using var stubs = new PaneStubs();
        var many = Enumerable.Range(0, 40)
            .Select(_ => new Change($"src/file{_:00}.cs", null, GitChange.Modified))
            .ToList();
        HistoryPane.ReadFiles = (_, _, _) => many;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var commits = pane.CommitList;
        var files = pane.FileList;
        await Assert.That(files.VirtualListSize).IsEqualTo(many.Count);
        files.TopItem = files.Items[10];
        await Assert.That(files.TopItem?.Index).IsEqualTo(10);

        Click(commits, 0);
        PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200));
        await Assert.That(files.VirtualListSize).IsEqualTo(0);

        Click(commits, 0);
        await PaneStubs.Settled(form);
        await Assert.That(files.VirtualListSize).IsEqualTo(many.Count);
        await Assert.That(files.GetItemRect(0).Top).IsEqualTo(0);
        await Assert.That(files.TopItem?.Index).IsEqualTo(0);
    }

    // A list scrolled down that loses all its rows, as the commits do in a folder outside a
    // repository, shows them from its top once it has them again, not as far down as it had been
    // scrolled, with nothing above them.
    [Test]
    public async Task RowsFromTopAfterNone()
    {
        using var stubs = new PaneStubs();
        var history = Enumerable.Range(0, 40)
            .Select(_ => new GitCommit($"{_:x40}", ["0000000000000000000000000000000000000000"], "Ada Lovelace", PaneStubs.Now, [], $"Commit {_}"))
            .ToList();
        HistoryPane.ReadCommits = (_, skip, count, _) => history.Skip(skip).Take(count).ToList();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        list.TopItem = list.Items[10];
        await Assert.That(list.TopItem?.Index).IsEqualTo(10);

        pane.Bind(null);
        pane.Bind(fixture.At("Docs"));
        await PaneStubs.Settled(form);
        await Assert.That(list.GetItemRect(0).Top).IsEqualTo(0);
        await Assert.That(list.TopItem?.Index).IsEqualTo(0);
    }

    // History's commits and the newest's files, more of each than fit, as the window opens: a
    // scrollbar along the side of each, none along the bottom, which the files brought as the
    // pane was laid out narrower than it first was, and again once they came.
    [Test]
    public async Task HistoryNoScrollbarAlongBottom()
    {
        using var stubs = new PaneStubs();
        var history = Enumerable.Range(0, 40)
            .Select(_ => new GitCommit($"{_:x40}", ["0000000000000000000000000000000000000000"], "Ada Lovelace", PaneStubs.Now, [], $"Commit {_}"))
            .ToList();
        HistoryPane.ReadCommits = (_, skip, count, _) => history.Skip(skip).Take(count).ToList();
        var many = Enumerable.Range(0, 40)
            .Select(_ => new Change($"src/file{_:00}.cs", null, GitChange.Modified))
            .ToList();
        HistoryPane.ReadFiles = (_, _, _) => many;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPinnedPanes(fixture.At("Docs"), false, true);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        PumpFor(TimeSpan.FromMilliseconds(200));
        var pane = form.GitPanes.HistoryPane;
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(many.Count);
        await Assert.That(RowListTests.Scrollbars(pane.CommitList)).IsEqualTo("side");
        await Assert.That(RowListTests.Scrollbars(pane.FileList)).IsEqualTo("side");
    }

    // A click as Windows sends one: the button up waiting in the queue, for the list, which
    // doesn't come back from the press until the button is up, to find.
    static void Click(RowList list, int index)
    {
        var row = list.GetItemRect(index);
        var point = (IntPtr) ((row.Top + row.Height / 2) << 16 | (row.Left + list.LogicalToDeviceUnits(40)));
        PostMessage(list.Handle, 0x0202, IntPtr.Zero, point);
        NativeMethods.SendMessage(list.Handle, 0x0201, 1, point);
        Application.DoEvents();
    }

    // The window's messages handled, timers' included, for as long as given.
    static void PumpFor(TimeSpan time)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < time)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    static int TopLine(Control control)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new(Point.Empty, control.Size));
        return bitmap.GetPixel(control.Width / 2, 0).ToArgb();
    }

    // Two of the fixture's folders are repositories, in a code folder of the fixture.
    static Fixture Repositories([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var fixture = Fixture.Create(file, member);
        foreach (var name in (string[]) ["Docs", "Source"])
        {
            var git = fixture.At(name, ".git");
            Directory.CreateDirectory(git);
            File.SetAttributes(git, FileAttributes.Directory | FileAttributes.Hidden);
        }

        return fixture;
    }

    static async Task<QuietForm> Show(string folder, Settings settings)
    {
        var places = new Places([folder], [], ShowComputer: false, _ => "");
        var form = new QuietForm([folder], settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            Size = new(1200, 700)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        return form;
    }

    static void Press(QuietForm form, Keys keys) =>
        form.Press(keys);

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;

        public void Press(Keys keys)
        {
            var message = new Message();
            ProcessCmdKey(ref message, keys);
        }
    }
}
