/// <summary>
/// How the Changes and History panes follow the window: where they go for how wide it is, the tab
/// chosen for each repository, sliding out and back, and the keys that do it.
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

    // The room beside the tree, the strip's included, decides where the panes go: beside the
    // list where it stays 500 wide with them there, both or one, otherwise over it.
    [Test]
    public async Task LayoutForTheRoom()
    {
        await Assert.That(GitPanes.LayoutFor(883, 380, 500, 4)).IsEqualTo(PaneLayout.Narrow);
        await Assert.That(GitPanes.LayoutFor(884, 380, 500, 4)).IsEqualTo(PaneLayout.Medium);
        await Assert.That(GitPanes.LayoutFor(1267, 380, 500, 4)).IsEqualTo(PaneLayout.Medium);
        await Assert.That(GitPanes.LayoutFor(1268, 380, 500, 4)).IsEqualTo(PaneLayout.Wide);
    }

    // The panes follow how wide the window is: too narrow, a tab for each on the strip; room for
    // one, the two as tabs beside the list; room for both, side by side, Changes then History. One
    // slid out stays out, beside the list, as the window widens.
    [Test]
    public async Task LayoutFollowsWidth()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings, narrow);
        await PaneStubs.Settled(form);
        var panes = form.GitPanes;
        await Assert.That(panes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History hidden, strip Changes (6) and History");

        panes.Float(GitPane.History, focus: false);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History slid out, strip Changes (6) and History");

        Resize(form, medium);
        await PaneStubs.Settled(form);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");
        await Assert.That(panes.HistoryPane.Header.Tabbed).IsTrue();
        await Assert.That(panes.Floating).IsNull();

        Resize(form, wide);
        await PaneStubs.Settled(form);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: wide, Changes beside, History beside, strip hidden");
        await Assert.That(panes.HistoryPane.Header.Tabbed).IsFalse();
        var changesAt = panes.ChangesPane.RectangleToScreen(panes.ChangesPane.ClientRectangle);
        var historyAt = panes.HistoryPane.RectangleToScreen(panes.HistoryPane.ClientRectangle);
        await Assert.That(changesAt.Right).IsLessThanOrEqualTo(historyAt.Left);
        await Assert.That(changesAt.Top).IsEqualTo(historyAt.Top);
        await Assert.That(panes.ChangesPane.Changes.Files.Count).IsEqualTo(PaneStubs.Files.Count);
        await Assert.That(panes.HistoryPane.Commits.Count).IsEqualTo(PaneStubs.History.Count);

        Resize(form, narrow);
        await PaneStubs.Settled(form);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History hidden, strip Changes (6) and History");
    }

    // Side by side, neither pane is narrower than 240 at 100%, however the line between them is
    // dragged, as the two together beside the list aren't narrower than both: a split kept from
    // before that leaves one narrower is held to it, at either side. Slid out, a pane's width kept
    // from before, narrower, is taken as the least too.
    [Test]
    public async Task NeitherPaneNarrowerThanTheLeast()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root,
            PaneSplit = 0.05
        };
        using var form = await Show(fixture.At("Docs"), settings, wide);
        await PaneStubs.Settled(form);
        var panes = form.GitPanes;
        var column = panes.ColumnSplit;
        var least = form.LogicalToDeviceUnits(240);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: wide, Changes beside, History beside, strip hidden");
        await Assert.That(column.Panel1MinSize).IsEqualTo(least);
        await Assert.That(column.Panel2MinSize).IsEqualTo(least);
        await Assert.That(panes.ChangesPane.Width).IsEqualTo(least);
        await Assert.That(panes.HistoryPane.Width).IsGreaterThan(least);

        settings.PaneSplit = 0.95;
        Resize(form, medium);
        await PaneStubs.Settled(form);
        Resize(form, wide);
        await PaneStubs.Settled(form);

        await Assert.That(panes.ChangesPane.Width).IsGreaterThan(least);
        await Assert.That(panes.HistoryPane.Width).IsEqualTo(least);

        settings.PaneWidth = 100;
        Resize(form, narrow);
        await PaneStubs.Settled(form);
        panes.Float(GitPane.Changes, focus: false, animate: false);

        await Assert.That(panes.ChangesPane.Width).IsEqualTo(least);
    }

    // Where there is room for one pane beside the list, the two are tabs along its top, Changes
    // showing to start with. The tab chosen in a repository shows there again, and only there,
    // kept in the settings.
    [Test]
    public async Task TabForEachRepository()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var panes = form.GitPanes;
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes showing, History hidden, strip hidden");
        await Assert.That(panes.ChangesPane.Header.Tabs().Select(_ => _.Pane)).IsEquivalentTo([GitPane.Changes, GitPane.History]);

        panes.ChooseTab(GitPane.History);
        await PaneStubs.Settled(form);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");
        await Assert.That(panes.HistoryPane.Commits.Count).IsEqualTo(PaneStubs.History.Count);
        await Assert.That(settings.PaneTabOf(fixture.At("Docs"))).IsEqualTo(GitPane.History);

        form.ActiveView!.Navigate(fixture.At("Source"));
        await PaneStubs.Settled(form);
        await Pump.Until(() => panes.Repo == fixture.At("Source"));
        await Assert.That(panes.Describe()).IsEqualTo("Source: medium, Changes showing, History hidden, strip hidden");

        form.ActiveView.Navigate(fixture.At("Docs"));
        await PaneStubs.Settled(form);
        await Pump.Until(() => panes.Repo == fixture.At("Docs"));
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");

        // Changes' tab along History's top, as a screen reader clicks it.
        var tabs = panes.HistoryPane.Header.AccessibilityObject;
        await Assert.That(tabs.GetChildCount()).IsEqualTo(2);
        await Assert.That(tabs.GetChild(0)!.Name).IsEqualTo("Changes (6)");
        await Assert.That(tabs.GetChild(1)!.State.HasFlag(AccessibleStates.Selected)).IsTrue();
        tabs.GetChild(0)!.DoDefaultAction();
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes showing, History hidden, strip hidden");
        await Assert.That(settings.PaneTabs).IsEmpty();
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
        using var form = await Show(fixture.At("Source"), settings);
        await PaneStubs.Settled(form);
        var repos = new List<string?>();
        form.ActiveView!.StatusChanged += () => repos.Add(form.GitPanes.Repo);

        form.ActiveView.Navigate(fixture.At("Source", "Deep"));
        await PaneStubs.Settled(form);

        await Assert.That(repos.All(_ => _ == fixture.At("Source"))).IsTrue();
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Source: medium, Changes showing, History hidden, strip hidden");
    }

    // Where there is no room beside the list, Ctrl+Shift+G and Ctrl+Shift+H slide a pane out with
    // the focus in it, and back again.
    [Test]
    public async Task KeysSlideThePanes()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings, narrow);
        await PaneStubs.Settled(form);

        Press(form, Keys.Control | Keys.Shift | Keys.H);
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History slid out, strip Changes (6) and History");
        await Assert.That(form.GitPanes.HistoryPane.Commits.Count).IsEqualTo(PaneStubs.History.Count);

        Press(form, Keys.Control | Keys.Shift | Keys.G);
        await PaneStubs.Settled(form);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: narrow, Changes slid out, History hidden, strip Changes (6) and History");
        await Assert.That(form.GitPanes.ChangesPane.Changes.Files.Count).IsEqualTo(PaneStubs.Files.Count);

        Press(form, Keys.Control | Keys.Shift | Keys.G);
        await Assert.That(form.GitPanes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History hidden, strip Changes (6) and History");
    }

    // Where there is room beside the list, Ctrl+Shift+H puts the focus in History, choosing its
    // tab where the two share the room, and again gives it back to the list. Side by side, the
    // pane the focus went into last is the tab to show once the window is narrower.
    [Test]
    public async Task KeysBesideTheList()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var panes = form.GitPanes;

        Press(form, Keys.Control | Keys.Shift | Keys.H);
        await PaneStubs.Settled(form);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");
        await Assert.That(panes.HistoryPane.ContainsFocus).IsTrue();
        await Assert.That(settings.PaneTabOf(fixture.At("Docs"))).IsEqualTo(GitPane.History);

        Press(form, Keys.Control | Keys.Shift | Keys.H);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");
        await Assert.That(panes.HistoryPane.ContainsFocus).IsFalse();

        settings.SetPaneTab(fixture.At("Docs"), GitPane.Changes);
        Resize(form, wide);
        await PaneStubs.Settled(form);
        Press(form, Keys.Control | Keys.Shift | Keys.H);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: wide, Changes beside, History beside, strip hidden");
        await Assert.That(panes.HistoryPane.ContainsFocus).IsTrue();
        await Assert.That(settings.PaneTabOf(fixture.At("Docs"))).IsEqualTo(GitPane.History);

        Resize(form, medium);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes hidden, History showing, strip hidden");
        await Assert.That(panes.HistoryPane.ContainsFocus).IsTrue();

        // Changes with the focus picks its first file, its diff showing in the Diff tab, which
        // keeps the panes on the repository.
        Press(form, Keys.Control | Keys.Shift | Keys.G);
        await Assert.That(panes.Describe()).IsEqualTo("Docs: medium, Changes showing, History hidden, strip hidden");
        await Assert.That(panes.ChangesPane.ContainsFocus).IsTrue();
        await Assert.That(settings.PaneTabOf(fixture.At("Docs"))).IsEqualTo(GitPane.Changes);
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
        using var first = await Show(fixture.At("Docs"), settings, narrow);
        using var second = await Show(fixture.At("Docs"), settings, narrow);
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
        await Assert.That(second.GitPanes.Describe()).IsEqualTo("Docs: narrow, Changes hidden, History hidden, strip Changes (6) and History");
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
        await Pump.NoModifiersHeld();
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
        await Pump.NoModifiersHeld();
        var escape = Message.Create(box.Handle, 0x0100, (IntPtr) Keys.Escape, IntPtr.Zero);
        box.PreProcessMessage(ref escape);
        await Assert.That(pane.Describing).IsFalse();

        box.Text = "From a draft";
        await Assert.That(pane.Describing).IsTrue();
        box.Text = "";
    }

    // A commit's menu offers to revert it and to tag it, and, for one with tags, to delete one of
    // them. The last, not pushed yet, can be amended or undone too.
    [Test]
    public async Task CommitMenuTags()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;

        string Entries(int index) =>
            string.Join(", ", pane.CommitCommands(fixture.At("Docs"), PaneStubs.History[index]).Select(_ => _?.Text ?? "----"));

        await Assert.That(Entries(0)).IsEqualTo("Copy SHA, Copy message, ----, Amend commit…, Undo commit…, Revert commit…, Tag…");
        await Assert.That(Entries(1)).IsEqualTo("Copy SHA, Copy message, ----, Revert commit…, Tag…, Delete tag…");
    }

    // The last commit pushed is neither amended nor undone, as changing it would mean pushing with
    // force; one never pushed, on a branch tracking none, is, though it has no arrow; the top
    // commit while comparing isn't, as it may be the other branch's.
    [Test]
    public async Task CommitMenuAmendsOnlyWhatIsNotPushed()
    {
        using var stubs = new PaneStubs();
        HistoryPane.ReadUnpushed = (_, _) => [];
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;

        string Entries() =>
            string.Join(", ", pane.CommitCommands(fixture.At("Docs"), PaneStubs.History[0]).Select(_ => _?.Text ?? "----"));

        await Assert.That(Entries()).IsEqualTo("Copy SHA, Copy message, ----, Revert commit…, Tag…");

        HistoryPane.ReadNeverPushed = (_, _) => true;
        pane.Reload();
        await Pump.Until(() => pane.Reading.IsCompleted);

        await Assert.That(Entries()).IsEqualTo("Copy SHA, Copy message, ----, Amend commit…, Undo commit…, Revert commit…, Tag…");

        HistoryPane.ReadUnpushed = (_, _) => [PaneStubs.History[0].Sha];
        HistoryPane.ReadRange = (_, _, skip, count, _) => PaneStubs.History.Skip(skip).Take(count).ToList();
        pane.Compare("feature");
        await Pump.Until(() => pane.Reading.IsCompleted && pane.Commits.Count > 0);

        await Assert.That(Entries()).IsEqualTo("Copy SHA, Copy message, ----, Revert commit…, Tag…");
    }

    // Amend commit carries on in Changes, as GitHub Desktop's does: the commit's message in the
    // summary, the focus in it, a notice saying the commit goes in place of the last, and Amend
    // last commit, which needs no file ticked. Stop amending makes it a new commit again, the
    // message kept; and amending ends by itself once the commit isn't the last any more.
    [Test]
    public async Task AmendCommitInChanges()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        var repo = fixture.At("Docs");
        settings.SetPaneTab(repo, GitPane.History);
        using var form = await Show(repo, settings);
        await PaneStubs.Settled(form);
        var history = form.GitPanes.HistoryPane;
        var changes = form.GitPanes.ChangesPane;

        history.CommitCommands(repo, PaneStubs.History[0]).Single(_ => _?.Text == "Amend commit…")!.Action();

        // Changes read once it shows, which it does for the amend.
        await Pump.Until(() => changes is
        {
            Amending: true,
            Reading.IsCompleted: true,
            Changes.Files.Count: > 0
        });

        await Assert.That(form.GitPanes.IsBeside(GitPane.Changes)).IsTrue();
        await Assert.That(changes.SummaryBox.Text).IsEqualTo(PaneStubs.History[0].Subject);
        await Assert.That(changes.SummaryBox.Focused).IsTrue();
        await Assert.That(changes.AmendNotice.Visible).IsTrue();
        await Assert.That(changes.AmendNotice.Height).IsGreaterThan(0);
        await Assert.That((changes.CommitButton.Text, changes.CommitButton.Enabled)).IsEqualTo(("Amend last commit", true));

        changes.StopAmending();

        await Assert.That(changes.Amending).IsFalse();
        await Assert.That(changes.AmendNotice.Visible).IsFalse();
        await Assert.That(changes.CommitButton.Text).IsEqualTo("Commit 6 files to ");
        await Assert.That(changes.SummaryBox.Text).IsEqualTo(PaneStubs.History[0].Subject);

        changes.TakeMessage(repo, "Amended\n\nThe why.", PaneStubs.History[0].Sha);
        await Assert.That((changes.Amending, changes.Describing, changes.DescriptionBox.Text)).IsEqualTo((true, true, "The why."));

        ChangesPane.Read = (_, _) => new("main", true, false, PaneStubs.Files, Head: PaneStubs.History[1].Sha);
        changes.Reload();
        await Pump.Until(() => changes.Reading.IsCompleted && !changes.Amending);

        await Assert.That(changes.CommitButton.Text).IsEqualTo("Commit 6 files to ");
        changes.SummaryBox.Text = "";
        changes.DescriptionBox.Text = "";
    }

    // With nothing uncommitted, what to do next: pull, then push after where both have moved on,
    // pull, push, or sync a fork; nothing where there is nothing to do.
    [Test]
    public async Task NoChangesSuggestions()
    {
        static string? Suggested(GitState? state)
        {
            if (NoChanges.SuggestionFor(state) is not { } suggestion)
            {
                return null;
            }

            return $"{suggestion.Button} {suggestion.Part}: {suggestion.Text}";
        }

        static GitState State(int ahead, int behind, int forkBehind = 0) =>
            new("main", "abc", ahead, behind, [])
            {
                UpstreamName = "origin/main",
                ForkBehind = forkBehind
            };

        await Assert.That(Suggested(null)).IsNull();
        await Assert.That(Suggested(State(0, 0))).IsNull();
        await Assert.That(Suggested(State(2, 0))).IsEqualTo("Push… Ahead: 2 commits not pushed yet.");
        await Assert.That(Suggested(State(0, 3))).IsEqualTo("Pull… Behind: 3 commits to pull, as of the last fetch.");
        await Assert.That(Suggested(State(1, 3))).IsEqualTo("Pull… Behind: 3 commits to pull, and 1 commit here to push after them, as of the last fetch.");
        await Assert.That(Suggested(State(0, 0, 4))).IsEqualTo("Sync fork… ForkBehind: 4 commits on the repository this was forked from to bring in, as of the last fetch.");
    }

    // Nothing uncommitted, once read, says so in place of the files, suggesting what to do next:
    // here to push the commit not pushed, which asks as the status bar's commits ahead do. Files
    // changed since are listed again.
    [Test]
    public async Task NoChangesInPlaceOfTheFiles()
    {
        using var stubs = new PaneStubs();
        var files = new List<Change>();
        ChangesPane.Read = (_, _) => new("main", true, false, files);
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;
        await Assert.That(pane.NoneShown).IsTrue();
        await Assert.That(pane.List.Visible).IsFalse();
        await Assert.That(pane.NoChanges.Suggested).IsEqualTo(new("1 commit not pushed yet.", "Push…", GitState.PartKind.Ahead));

        var ask = GitStatusActions.Ask;
        GitStatusActions.Question? asked = null;
        GitStatusActions.Ask = (_, question) =>
        {
            asked = question;
            return null;
        };
        try
        {
            pane.NoChanges.SuggestionButton.PerformClick();
            await Pump.Until(() => asked != null);
        }
        finally
        {
            GitStatusActions.Ask = ask;
        }

        await Assert.That(asked!.Caption).IsEqualTo("Git push");

        files.AddRange(PaneStubs.Files);
        pane.Reload();
        await PaneStubs.Settled(form);
        await Assert.That(pane.NoneShown).IsFalse();
        await Assert.That(pane.List.Visible).IsTrue();
    }

    // Git failing to read what is uncommitted, refusing a repository owned by another user say,
    // as it does one on exFAT or a share, says what git said in place of the files, with nothing
    // to commit, rather than that nothing is uncommitted, which forgot every file left unticked:
    // what was read last is kept, and the ticks with it, for once git reads it again.
    [Test]
    public async Task GitFailingSaysWhatGitSaid()
    {
        using var stubs = new PaneStubs();
        var read = new GitChanges("main", true, false, PaneStubs.Files);
        ChangesPane.Read = (_, _) => read;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;
        var draft = CommitDrafts.Of(form.GitPanes.Repo);
        draft.SetTick("notes.aleph", false);
        pane.SummaryBox.Text = "Summary";
        await Assert.That((pane.CommitButton.Text, pane.CommitButton.Enabled)).IsEqualTo(("Commit 5 files to ", true));

        const string said = "fatal: detected dubious ownership in repository at 'D:/Docs'";
        read = GitChanges.Failed(said);
        pane.Reload();
        await PaneStubs.Settled(form);

        await Assert.That(pane.NoneShown).IsTrue();
        await Assert.That(pane.List.Visible).IsFalse();
        await Assert.That(pane.NoChanges.Said).IsEqualTo(("Git couldn't read the changes", said));
        await Assert.That(pane.NoChanges.SuggestionButton.Visible).IsFalse();
        await Assert.That(pane.CommitButton.Enabled).IsFalse();
        await Assert.That(pane.Changes.Files.Count).IsEqualTo(PaneStubs.Files.Count);
        await Assert.That(draft.Unticked).IsEquivalentTo(["notes.aleph"]);

        read = new("main", true, false, PaneStubs.Files);
        pane.Reload();
        await PaneStubs.Settled(form);

        await Assert.That(pane.NoneShown).IsFalse();
        await Assert.That(pane.List.Visible).IsTrue();
        await Assert.That((pane.CommitButton.Text, pane.CommitButton.Enabled)).IsEqualTo(("Commit 5 files to ", true));
        await Assert.That(draft.Unticked).IsEquivalentTo(["notes.aleph"]);
        pane.SummaryBox.Text = "";
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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

        await Assert.That(pane.CommitTipAt(Right(0, 26))).IsEqualTo("Not pushed yet. Click to push");
        await Assert.That(pane.CommitTipAt(Right(1, 30))).IsEqualTo("Tag 0.25.0");
        await Assert.That(pane.CommitTipAt(Right(4, 30))).IsEqualTo("Tag stable");
        var subject = list.GetItemRect(0);
        await Assert.That(pane.CommitTipAt(new(subject.Left + list.LogicalToDeviceUnits(40), subject.Top + subject.Height / 2))).IsNull();
        await Assert.That(pane.CommitTipAt(Right(2, 30))).IsNull();
    }

    // The arrow of a commit not pushed clicked asks to push, as the status bar's commits ahead
    // do, leaving the commit picked as it was: a button, not part of the row. A tag clicked
    // picks its commit, as the rest of the row does.
    [Test]
    public async Task ClickingTheArrowAsksToPush()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var list = form.GitPanes.HistoryPane.CommitList;
        var picked = list.SelectedIndices.Cast<int>().ToList();

        Point Right(int index, int fromRight)
        {
            var row = list.GetItemRect(index);
            row.Width = list.Columns[0].Width;
            return new(row.Right - list.LogicalToDeviceUnits(fromRight), row.Top + row.Height / 2);
        }

        var ask = GitStatusActions.Ask;
        GitStatusActions.Question? asked = null;
        GitStatusActions.Ask = (_, question) =>
        {
            asked = question;
            return null;
        };
        try
        {
            ClickAt(list, Right(0, 26));
            await Pump.Until(() => asked != null);
        }
        finally
        {
            GitStatusActions.Ask = ask;
        }

        await Assert.That(asked!.Caption).IsEqualTo("Git push");
        await Assert.That(asked.Heading).StartsWith("main is 1 commit ahead of ");
        await Assert.That(list.SelectedIndices.Cast<int>()).IsEquivalentTo(picked);

        ClickAt(list, Right(1, 30));
        await Assert.That(list.SelectedIndices.Cast<int>()).IsEquivalentTo([1]);
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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

    // A click on the picked commit with Ctrl held picks as a click always does, leaving it picked
    // with its files: Ctrl as the press has it, not as the window's thread reads the keyboard
    // when it is handled, which can be the real keyboard's, a key pressed in another window.
    [Test]
    public async Task CtrlClickOnPickedCommitKeepsIt()
    {
        const int controlHeld = 0x0008;
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([0]);

        Click(list, 0, controlHeld);
        PumpFor(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime * 2 + 200));

        await Assert.That(list.SelectedIndices.Cast<int>().ToList()).IsEquivalentTo([0]);
        await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(PaneStubs.CommitFiles.Count);
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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

    // As in Desktop: the picked commit's short sha, between its author and when, can be selected,
    // dragged across or double-clicked, and what is selected copied, all of it in sight, a margin
    // before its first letter, whose smoothed edge an edit box cut off; and the button beside it
    // copies the whole sha. Another commit picked, its sha, nothing of it selected.
    [Test]
    public async Task ShaSelectableAndCopied()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var sha = pane.HeadingSha;
        var copy = pane.CopyShaButton;
        var newest = PaneStubs.History[0];
        await Assert.That(sha.Text).IsEqualTo(newest.ShortSha);
        await Assert.That(sha.Visible).IsTrue();
        await Assert.That(sha.TextInset).IsGreaterThan(0);
        await Assert.That(sha.Width).IsEqualTo(sha.TextWidth);
        await Assert.That(copy.Left).IsGreaterThanOrEqualTo(sha.Right);
        await Assert.That(Math.Abs(copy.Top + copy.Height / 2 - (sha.Top + sha.Height / 2))).IsLessThanOrEqualTo(1);

        // Dragged from before its first letter to the middle of its fourth's.
        var fourth = TextRenderer.MeasureText(sha.Text[..4], sha.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var y = sha.Height / 2;
        Send(sha, leftButtonDown, new(0, y));
        Send(sha, mouseMove, new(sha.TextInset + fourth - 1, y));
        Send(sha, leftButtonUp, new(sha.TextInset + fourth - 1, y));
        var dragged = sha.SelectedText;
        Send(sha, leftButtonDoubleClick, new(sha.TextInset + 1, y));
        Send(sha, leftButtonUp, new(sha.TextInset + 1, y));
        var doubleClicked = sha.SelectedText;

        var copied = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = copied.Add;
        try
        {
            sha.Copy();
            copy.PerformClick();
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
        }

        await Assert.That(dragged).IsEqualTo(newest.ShortSha[..4]);
        await Assert.That(doubleClicked).IsEqualTo(newest.ShortSha);
        await Assert.That(copied).IsEquivalentTo([newest.ShortSha, newest.Sha]);

        pane.CommitList.SelectedIndices.Clear();
        pane.CommitList.SelectedIndices.Add(1);
        await PaneStubs.Settled(form);
        await Assert.That(sha.Text).IsEqualTo(PaneStubs.History[1].ShortSha);
        await Assert.That(sha.SelectedText).IsEmpty();
    }

    // A commit picked that is gone once the commits are read again, as an amended one is, gives
    // way to the newest, with its files.
    [Test]
    public async Task PickedCommitGoneNewestPicked()
    {
        using var stubs = new PaneStubs();
        var history = PaneStubs.History.ToList();
        HistoryPane.ReadCommits = (_, skip, count, _, _) => history.Skip(skip).Take(count).ToList();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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

    // Another repository gone to while the one before is still read, as it is after every save,
    // is read once that read ends, in Changes and History both: its files and its commits
    // listed, rather than none until something in it changes. The read of the one before is
    // held until the window is in the other, so it ends after, as it would where git is slow.
    [Test]
    public async Task ReadOnceTheRepoBeforeIs()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var docs = fixture.At("Docs");
        var source = fixture.At("Source");
        using var held = new ManualResetEventSlim();
        var started = 0;
        ChangesPane.Read = (repo, _) =>
        {
            if (!ChangesPane.SameRepo(repo, docs))
            {
                return new("main", true, false, PaneStubs.Files.Take(2).ToList());
            }

            Interlocked.Increment(ref started);
            held.Wait(TimeSpan.FromSeconds(30));
            return new("main", true, false, PaneStubs.Files);
        };
        HistoryPane.ReadCommits = (repo, skip, count, _, _) =>
        {
            if (!ChangesPane.SameRepo(repo, docs))
            {
                return PaneStubs.History.Take(2).ToList();
            }

            Interlocked.Increment(ref started);
            held.Wait(TimeSpan.FromSeconds(30));
            return PaneStubs.History.Skip(skip).Take(count).ToList();
        };
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        try
        {
            using var form = await Show(docs, settings, wide);
            var panes = form.GitPanes;
            await Pump.Until(() => Volatile.Read(ref started) == 2);

            form.ActiveView!.Navigate(source);
            await Pump.Until(() => panes.Repo == source);
            held.Set();
            await PaneStubs.Settled(form);

            await Assert.That(panes.Describe()).IsEqualTo("Source: wide, Changes beside, History beside, strip hidden");
            await Assert.That(panes.ChangesPane.Changes.Files.Count).IsEqualTo(2);
            await Assert.That(panes.ChangesPane.List.VirtualListSize).IsEqualTo(2);
            await Assert.That(panes.HistoryPane.Commits.Count).IsEqualTo(2);
        }
        finally
        {
            held.Set();
        }
    }

    // A file saved reads Changes again, but not History, whose commits it doesn't change: read
    // again, History read every commit listed, every tag and the commits not pushed after every
    // save. Nor is History read again for a file staged, which changes only the index. A change
    // anywhere else in .git reads both: a commit, a branch or a tag made, a checkout, a fetch.
    // And so does the .mailmap, which History names authors by.
    [Test]
    public async Task HistoryIsReadAgainOnlyForAChangeOfItsOwn()
    {
        using var stubs = new PaneStubs();
        var changesRead = 0;
        var historyRead = 0;
        ChangesPane.Read = (_, _) =>
        {
            Interlocked.Increment(ref changesRead);
            return new("main", true, false, PaneStubs.Files);
        };
        HistoryPane.ReadCommits = (_, skip, count, _, _) =>
        {
            Interlocked.Increment(ref historyRead);
            return PaneStubs.History.Skip(skip).Take(count).ToList();
        };
        var settle = GitCache.Settle;
        GitCache.Settle = TimeSpan.FromMilliseconds(50);
        using var fixture = Repositories();
        var repo = fixture.At("Docs");
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        try
        {
            using var form = await Show(repo, settings, wide);
            await PaneStubs.Settled(form);

            // Whether History was read again as Changes was, once told of a change at the path.
            async Task<bool> HistoryReadFor(params string[] path)
            {
                var changesBefore = Volatile.Read(ref changesRead);
                var historyBefore = Volatile.Read(ref historyRead);
                GitCache.ChangedAt(Path.Combine([repo, .. path]));
                await Pump.Until(() => Volatile.Read(ref changesRead) > changesBefore);

                // Long enough for whatever else the change was told of with to have come too.
                await Pump.For(TimeSpan.FromMilliseconds(300));
                await PaneStubs.Settled(form);
                return Volatile.Read(ref historyRead) > historyBefore;
            }

            var saved = await HistoryReadFor("notes.aleph");
            var savedDeeper = await HistoryReadFor("Source", "main.aleph");
            var staged = await HistoryReadFor(".git", "index");
            var committed = await HistoryReadFor(".git", "refs", "heads", "main");
            var branched = await HistoryReadFor(".git", "refs", "heads", "feature");
            var tagged = await HistoryReadFor(".git", "refs", "tags", "v1.0");
            var packed = await HistoryReadFor(".git", "packed-refs");
            var checkedOut = await HistoryReadFor(".git", "HEAD");
            var fetched = await HistoryReadFor(".git", "refs", "remotes", "origin", "main");
            var mailmap = await HistoryReadFor(".mailmap");

            await Assert.That((saved, savedDeeper, staged)).IsEqualTo((false, false, false));
            await Assert.That((committed, branched, tagged, packed, checkedOut, fetched, mailmap)).IsEqualTo((true, true, true, true, true, true, true));
        }
        finally
        {
            GitCache.Settle = settle;
        }
    }

    // The next page, read as the list nears the end of those read, goes on from where the first
    // stopped, with its walk, rather than from HEAD past every commit listed. Read again from the
    // top, after a commit say, the commits are walked afresh, as HEAD may be another.
    [Test]
    public async Task NextPageGoesOnFromWhereTheLastStopped()
    {
        using var stubs = new PaneStubs();
        var history = Enumerable.Range(0, 500)
            .Select(_ => new GitCommit($"{_:x40}", ["0000000000000000000000000000000000000000"], "Ada Lovelace", PaneStubs.Now, [], $"Commit {_}"))
            .ToList();
        var asked = new List<(int Skip, int Count, HistoryWalk Walk)>();
        HistoryPane.ReadCommits = (_, skip, count, walk, _) =>
        {
            lock (asked)
            {
                asked.Add((skip, count, walk));
            }

            return history.Skip(skip).Take(count).ToList();
        };
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.HistoryPane;
        var list = pane.CommitList;
        (int Skip, int Count, HistoryWalk Walk) Last()
        {
            lock (asked)
            {
                return asked[^1];
            }
        }

        var first = Last();

        list.TopItem = list.Items[GitHistory.Page - 10];
        await Pump.Until(() => pane.Commits.Count == GitHistory.Page * 2);
        var next = Last();

        pane.Reload();
        await PaneStubs.Settled(form);
        var again = Last();

        await Assert.That((first.Skip, first.Count)).IsEqualTo((0, GitHistory.Page));
        await Assert.That((next.Skip, next.Count)).IsEqualTo((GitHistory.Page, GitHistory.Page));
        await Assert.That(ReferenceEquals(next.Walk, first.Walk)).IsTrue();
        await Assert.That((again.Skip, again.Count)).IsEqualTo((0, GitHistory.Page * 2));
        await Assert.That(ReferenceEquals(again.Walk, first.Walk)).IsFalse();
        await Assert.That(pane.Commits.Select(_ => _.Subject)).IsEquivalentTo(history.Take(GitHistory.Page * 2).Select(_ => _.Subject), CollectionOrdering.Matching);
    }

    // The files of a commit of the repository before, still being read once another is gone to,
    // aren't shown under it: here one with no commits, so none is picked whose files would be
    // read in their place.
    [Test]
    public async Task FilesOfTheRepoBeforeNotShown()
    {
        using var stubs = new PaneStubs();
        using var fixture = Repositories();
        var docs = fixture.At("Docs");
        var source = fixture.At("Source");
        using var held = new ManualResetEventSlim();
        var started = 0;
        HistoryPane.ReadCommits = (repo, skip, count, _, _) =>
        {
            if (!ChangesPane.SameRepo(repo, docs))
            {
                return [];
            }

            return PaneStubs.History.Skip(skip).Take(count).ToList();
        };
        HistoryPane.ReadFiles = (repo, _, _) =>
        {
            if (ChangesPane.SameRepo(repo, docs))
            {
                Interlocked.Increment(ref started);
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return PaneStubs.CommitFiles;
        };
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(docs, GitPane.History);
        settings.SetPaneTab(source, GitPane.History);
        try
        {
            using var form = await Show(docs, settings);
            var panes = form.GitPanes;
            var pane = panes.HistoryPane;
            await Pump.Until(() => Volatile.Read(ref started) == 1);

            form.ActiveView!.Navigate(source);
            await Pump.Until(() => panes.Repo == source);
            await Pump.Until(() => pane.Reading.IsCompleted);
            held.Set();
            await PaneStubs.Settled(form);

            await Assert.That(panes.Describe()).IsEqualTo("Source: medium, Changes hidden, History showing, strip hidden");
            await Assert.That(pane.Commits.Count).IsEqualTo(0);
            await Assert.That(pane.FileList.VirtualListSize).IsEqualTo(0);
        }
        finally
        {
            held.Set();
        }
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
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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
        HistoryPane.ReadCommits = (_, skip, count, _, _) => history.Skip(skip).Take(count).ToList();
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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

    // Part way through a merge with conflicts, as GitHub Desktop's: a bar over the files saying
    // what is merging and how many files are left, the summary starting as git's message, a
    // file's right-click entries picking a side, and Commit merge held back until none are left.
    [Test]
    public async Task ChangesSettleAMergesConflicts()
    {
        using var stubs = new PaneStubs();
        ChangesPane.Read = (_, _) => PaneStubs.Conflicted;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;

        string Entries(Change change) =>
            string.Join(
                ", ",
                pane.ConflictCommands([change]).Select(_ =>
                {
                    if (_ == null)
                    {
                        return "----";
                    }

                    if (_.Enabled)
                    {
                        return _.Text;
                    }

                    return $"{_.Text} (disabled)";
                }));

        await Assert.That(pane.MergeText).IsEqualTo("Merging feature into main: 2 conflicted files left. Right-click one to pick a side, or settle its markers in the editor.");
        await Assert.That(pane.SummaryBox.Text).IsEqualTo("Merge branch 'feature'");
        await Assert.That((pane.CommitButton.Text, pane.CommitButton.Enabled)).IsEqualTo(("Commit merge", false));
        await Assert.That(Entries(PaneStubs.Conflicted.Files[0])).IsEqualTo("Use the file from main, Use the deletion from feature, Mark as resolved, ----");
        await Assert.That(Entries(PaneStubs.Conflicted.Files[1])).IsEqualTo("Use the file from main, Use the file from feature, Mark as resolved (disabled), ----");
        await Assert.That(pane.ConflictCommands([PaneStubs.Conflicted.Files[2]])).IsEmpty();
        await Assert.That((pane.AbortButton.Text, pane.ContinueButton.Visible)).IsEqualTo(("Abort merge", false));
    }

    // Part way through a rebase stopped by conflicts, as git pull --rebase from a terminal
    // leaves one: the bar over the files says what is rebased onto what, with Continue rebase and
    // Abort rebase, and git's sides are named for what they are then, ours what it goes onto and
    // theirs the branch's own. Commit is Continue rebase, held back until no conflicts are left,
    // but needing no summary, as the commit being replayed keeps its own message. A file git
    // doesn't track isn't ticked, as it stays out of it.
    [Test]
    public async Task ChangesSettleARebasesConflicts()
    {
        using var stubs = new PaneStubs();
        var conflicted = new Change("Source/main.aleph", null, GitChange.Conflicted, new(2, false));
        var untracked = new Change("notes.aleph", null, GitChange.Untracked);
        var read = new GitChanges(null, true, true, [conflicted, untracked], Rebasing: new("feature", "origin/main"));
        ChangesPane.Read = (_, _) => read;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var pane = form.GitPanes.ChangesPane;
        var entries = string.Join(", ", pane.ConflictCommands([conflicted]).Select(_ => _?.Text ?? "----"));

        await Assert.That(pane.MergeText).IsEqualTo("Rebasing feature onto origin/main: 1 conflicted file left. Right-click one to pick a side, or settle its markers in the editor.");
        await Assert.That((pane.AbortButton.Text, pane.ContinueButton.Visible, pane.ContinueButton.Enabled)).IsEqualTo(("Abort rebase", true, false));
        await Assert.That((pane.CommitButton.Text, pane.CommitButton.Enabled)).IsEqualTo(("Continue rebase", false));
        await Assert.That(pane.SummaryBox.Text).IsEqualTo("");
        await Assert.That(entries).IsEqualTo("Use the file from origin/main, Use the file from feature, Mark as resolved, ----");
        await Assert.That((pane.TickOf(conflicted), pane.TickOf(untracked))).IsEqualTo(((bool?) true, (bool?) false));

        string? asked = null;
        var ask = ChangesPane.AskAbort;
        ChangesPane.AskAbort = (_, what) =>
        {
            asked = what;
            return DialogResult.Cancel;
        };
        try
        {
            pane.AbortButton.PerformClick();
        }
        finally
        {
            ChangesPane.AskAbort = ask;
        }

        await Assert.That(asked).IsEqualTo("rebase");

        read = read with
        {
            Files = [conflicted with { Conflict = new(0, false) }, untracked]
        };
        pane.Reload();
        await PaneStubs.Settled(form);

        await Assert.That(pane.MergeText).IsEqualTo("Rebasing feature onto origin/main: no conflicts left. Continue the rebase.");
        await Assert.That((pane.CommitButton.Text, pane.CommitButton.Enabled, pane.ContinueButton.Enabled)).IsEqualTo(("Continue rebase", true, true));
    }

    // Compare to branch, as GitHub Desktop's: the commits the branch has that the one checked
    // out hasn't, behind, first, with how many each way; Ahead lists the others; closed, the
    // branch's own commits again. Not for a repository the panes aren't about.
    [Test]
    public async Task CompareListsBehindThenAhead()
    {
        using var stubs = new PaneStubs();
        var ranges = new List<string>();
        HistoryPane.ReadRange = (_, range, _, _, _) =>
        {
            ranges.Add(range);
            if (range.StartsWith("HEAD..", StringComparison.Ordinal))
            {
                return PaneStubs.History.Take(2).ToList();
            }

            return PaneStubs.History.Skip(2).Take(1).ToList();
        };
        HistoryPane.ReadApart = (_, _, _) => (1, 2);
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
        using var form = await Show(fixture.At("Docs"), settings);
        await PaneStubs.Settled(form);
        var panes = form.GitPanes;
        var pane = panes.HistoryPane;

        await Assert.That(panes.Compare(fixture.At("Elsewhere"), "develop")).IsFalse();
        await Assert.That(panes.Compare(fixture.At("Docs"), "develop")).IsTrue();
        await Pump.Until(() => pane.Reading.IsCompleted);
        await Assert.That((pane.ComparedTo, pane.Commits.Count, ranges[^1])).IsEqualTo(("develop", 2, "HEAD..develop"));
        await Assert.That(pane.CompareText).IsEqualTo("Compared to develop: Behind (2), Ahead (1)");

        pane.ShowAhead(true);
        await Pump.Until(() => pane.Reading.IsCompleted);
        await Assert.That((pane.ShowingAhead, pane.Commits.Count, ranges[^1])).IsEqualTo((true, 1, "develop..HEAD"));

        pane.EndCompare();
        await Pump.Until(() => pane.Reading.IsCompleted);
        await Assert.That((pane.ComparedTo, pane.Commits.Count)).IsEqualTo(((string?) null, PaneStubs.History.Count));
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
        HistoryPane.ReadCommits = (_, skip, count, _, _) => history.Skip(skip).Take(count).ToList();
        var many = Enumerable.Range(0, 40)
            .Select(_ => new Change($"src/file{_:00}.cs", null, GitChange.Modified))
            .ToList();
        HistoryPane.ReadFiles = (_, _, _) => many;
        using var fixture = Repositories();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        settings.SetPaneTab(fixture.At("Docs"), GitPane.History);
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
    // A click on the row, with the keys, MK_CONTROL or MK_SHIFT, held with it.
    static void Click(RowList list, int index, int keys = 0)
    {
        var row = list.GetItemRect(index);
        var point = (IntPtr) ((row.Top + row.Height / 2) << 16 | (row.Left + list.LogicalToDeviceUnits(40)));
        PostMessage(list.Handle, 0x0202, keys, point);
        NativeMethods.SendMessage(list.Handle, 0x0201, 1 | keys, point);
        Application.DoEvents();
    }

    static void ClickAt(RowList list, Point at)
    {
        var point = (IntPtr) (at.Y << 16 | at.X);
        PostMessage(list.Handle, 0x0202, 0, point);
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

    const int mouseMove = 0x0200;
    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;
    const int leftButtonDoubleClick = 0x0203;

    // The mouse at a point of the control, the left button held for all but its letting go.
    static void Send(Control control, int message, Point point)
    {
        var buttons = 1;
        if (message == leftButtonUp)
        {
            buttons = 0;
        }

        NativeMethods.SendMessage(control.Handle, message, buttons, (point.Y << 16) | (point.X & 0xFFFF));
    }

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

    // How wide the window is, at 100% scale, for each place the panes go: none beside the list,
    // one, and both.
    const int narrow = 900;
    const int medium = 1200;
    const int wide = 1700;

    static async Task<QuietForm> Show(string folder, Settings settings, int width = medium)
    {
        var places = new Places([folder], [], ShowComputer: false, _ => "");
        var form = new QuietForm([folder], settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            // Wider than CI's screen, which Windows lets a window be only with a maximum size of
            // its own: WindowSize has it so.
            MaximumSize = new(10_000, 10_000)
        };
        Resize(form, width);
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        return form;
    }

    static void Resize(Form form, int width) =>
        WindowSize.Set(form, form.LogicalToDeviceUnits(width), form.LogicalToDeviceUnits(700));

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