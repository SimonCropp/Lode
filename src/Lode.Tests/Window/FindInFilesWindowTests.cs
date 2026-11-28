/// <summary>
/// Find in Files in a window: the strip, the Find Results tab, and Replace from it.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class FindInFilesWindowTests
{
    // Ctrl+Shift+F opens the strip on the folder open, keeping Where's patterns.
    [Test]
    public async Task OpeningTheStripPutsTheFolderInWhere()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new(), fixture.Root);
        form.FindHost.Panel.WhereBox.Text = @"C:\Elsewhere, *.aleph";

        form.FindHost.Open();

        await Assert.That(form.FindHost.Panel.Visible).IsTrue();
        await Assert.That(form.FindHost.Panel.WhereBox.Text).IsEqualTo($"{fixture.Root}, *.aleph");
    }

    // Results go in a tab of their own, which isn't a folder's: it isn't saved with the tabs,
    // closed it isn't offered to reopen, and the tab before it is the one to open on.
    [Test]
    public async Task FindResultsIsATabOfItsOwn()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Docs", "notes.txt"), "alpha\nbeta\n");
        var settings = new Settings();
        using var form = await Show(fixture, settings, fixture.Root, fixture.At("Docs"));
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";

        form.FindHost.Find();
        await form.FindHost.ResultsView!.Searching;
        var results = form.FindHost.ResultsView.Results.ToText().Replace(fixture.Root, "{root}");
        var title = form.Text;
        var addressEnabled = form.Controls.Find("address", true)[0].Parent!.Enabled;
        form.Close();

        await Verify(
            new
            {
                title,
                addressEnabled,
                results,
                savedTabs = settings.Tabs.Select(fixture.Relative),
                settings.ActiveTab,
                findHistory = settings.FindHistory
            })
            .Snapshot(
                """
                {
                  title: Find Results,
                  addressEnabled: false,
                  results:
                Searched 2 files for "alpha"

                {root}\Docs\notes.txt:
                    1: alpha
                    2  beta

                1 match across 1 file
                Left out: 1 binary file
                ,
                  savedTabs: [
                    Projects,
                    Projects/Docs
                  ],
                  ActiveTab: 1,
                  findHistory: [
                    alpha
                  ]
                }
                """);
    }

    // Replace replaces in what was found, asking first, and the results then show each line as
    // it now is.
    [Test]
    public async Task ReplaceFromTheStrip()
    {
        using var fixture = Fixture.Create();
        var file = fixture.At("Docs", "notes.txt");
        await File.WriteAllTextAsync(file, "alpha\nAlpha beta\n");
        using var form = await Show(fixture, new(), fixture.Root);
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";
        form.FindHost.Panel.ReplaceBox.Text = "omega";
        var asked = new List<string>();
        form.FindHost.ConfirmReplace = (matches, files, find, replacement) =>
        {
            asked.Add($"{matches} in {files}: {find} -> {replacement}");
            return true;
        };

        try
        {
            await form.FindHost.Replace();
        }
        finally
        {
            // Replace runs in the Actions panel: cleared, so windows captured after this in the
            // same run don't show its entry.
            ActionLog.Reset();
        }

        await Verify(
            new
            {
                asked,
                file = await File.ReadAllTextAsync(file),
                results = form.FindHost.ResultsView!.Results.ToText().Replace(fixture.Root, "{root}")
            })
            .Snapshot(
                """
                {
                  asked: [
                    2 in 1: alpha -> omega
                  ],
                  file:
                omega
                omega beta
                ,
                  results:
                Replaced "alpha" with "omega"

                {root}\Docs\notes.txt:
                    1: omega
                    2: omega beta

                2 occurrences replaced across 1 file

                }
                """);
    }

    // A filter matching no names offers to search inside the files instead, which runs Find in
    // Files on the tab's folder for it, as text, whatever the strip had.
    [Test]
    public async Task AFilterMatchingNothingSearchesInsideTheFiles()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Docs", "notes.txt"), "version \"11.0\" here\n");
        using var form = await Show(fixture, new() {FindRegex = true}, fixture.Root);
        var view = form.ActiveView!;
        form.Controls.Find("filter", true)[0].Text = "\"11.0";
        await Pump.Loaded(view);
        var emptyText = view.EmptyText;

        view.SearchInsideTheFiles();
        await form.FindHost.ResultsView!.Searching;

        await Verify(
            new
            {
                emptyText,
                find = form.FindHost.Panel.FindBox.Text,
                where = form.FindHost.Panel.WhereBox.Text.Replace(fixture.Root, "{root}"),
                form.FindHost.Panel.ReadQuery().Regex,
                results = form.FindHost.ResultsView.Results.ToText().Replace(fixture.Root, "{root}")
            })
            .Snapshot(
                """
                {
                  emptyText: No items match your search. [Search inside the files],
                  find: "11.0,
                  where: {root},
                  Regex: false,
                  results:
                Searched 10 files for ""11.0"

                {root}\Docs\notes.txt:
                    1: version "11.0" here

                1 match across 1 file
                Left out: 8 binary files

                }
                """);
    }

    // The strip stays where it is while a search goes on. The status bar under it, emptied for
    // a moment each time it said how far the search had got, was shorter, and the strip went
    // up and down with it.
    [Test]
    public async Task TheStripStaysPutWhileSearching()
    {
        using var fixture = Fixture.Create();
        for (var index = 0; index < 200; index++)
        {
            await File.WriteAllTextAsync(fixture.At("Docs", $"note{index}.txt"), "alpha\n");
        }

        using var form = await Show(fixture, new(), fixture.Root);
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";
        Application.DoEvents();
        var moves = new List<Rectangle>();
        form.FindHost.Panel.LocationChanged += (_, _) => moves.Add(form.FindHost.Panel.Bounds);
        form.FindHost.Panel.SizeChanged += (_, _) => moves.Add(form.FindHost.Panel.Bounds);

        form.FindHost.Find();
        await form.FindHost.ResultsView!.Searching;
        Application.DoEvents();

        await Assert.That(moves).IsEmpty();
    }

    // While the search box has found names, the status bar offers to search for it inside the
    // files instead. Not with nothing in the box, nor on Find Results.
    [Test]
    public async Task TheStatusBarOffersToSearchInsideTheFiles()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Docs", "notes.txt"), "the aleph is here\n");
        using var form = await Show(fixture, new(), fixture.Root);
        var view = form.ActiveView!;
        var link = form.Controls.OfType<StatusStrip>().Single().Items["searchInside"]!;
        var withoutFilter = link.Visible;
        form.Controls.Find("filter", true)[0].Text = "*aleph";
        await Pump.Loaded(view);
        Application.DoEvents();
        var withFilter = link.Visible;
        var found = view.Entries.Count;

        link.PerformClick();
        await form.FindHost.ResultsView!.Searching;
        Application.DoEvents();

        await Verify(
            new
            {
                withoutFilter,
                withFilter,
                found,
                onResults = link.Visible,
                find = form.FindHost.Panel.FindBox.Text,
                results = form.FindHost.ResultsView.Results.ToText().Replace(fixture.Root, "{root}")
            })
            .Snapshot(
                """
                {
                  withoutFilter: false,
                  withFilter: true,
                  found: 5,
                  onResults: false,
                  find: aleph,
                  results:
                Searched 10 files for "aleph"

                {root}\Docs\notes.txt:
                    1: the aleph is here

                1 match across 1 file
                Left out: 8 binary files

                }
                """);
    }

    // Closing Find Results lets go of it; the next search makes it again.
    [Test]
    public async Task ClosingFindResults()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new(), fixture.Root);
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";
        form.FindHost.Find();
        await form.FindHost.ResultsView!.Searching;
        var page = (TabPage) form.FindHost.ResultsView.Parent!;

        form.CloseTab(page);

        await Assert.That(form.FindHost.ResultsView).IsNull();
        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(form.CanReopenClosedTab).IsFalse();
    }

    // A folder picked in the tree while Find Results shows, which has no folder: the folder's tab
    // that showed before it goes there, and shows in its place, rather than the tab beside it.
    // Find Results stays open behind.
    [Test]
    public async Task TheTreeGoesToTheTabFindResultsWasOpenedOver()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new(), fixture.At("Docs"), fixture.At("Archive"));
        var docs = form.Views.ElementAt(0);
        var archive = form.Views.ElementAt(1);
        await Pick(form, docs);
        await Find(form);

        PickRootInTree(form);
        await Pump.Loaded(docs);

        await Assert.That(form.ActiveView).IsEqualTo(docs);
        await Assert.That(docs.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(archive.CurrentPath).IsEqualTo(fixture.At("Archive"));
        await Assert.That(form.FindHost.ResultsView).IsNotNull();
    }

    // With the tab Find Results was opened over closed since, the tree goes to the folder's tab
    // looked at last, not the one beside Find Results; and with no folder's tab left, to a new
    // one.
    [Test]
    public async Task TheTreeFromFindResultsOnceTheTabBeforeItHasClosed()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new(), fixture.At("Docs"), fixture.At("Archive"), fixture.At("Source"));
        var docs = form.Views.ElementAt(0);
        var archive = form.Views.ElementAt(1);
        var source = form.Views.ElementAt(2);
        await Pick(form, archive);
        await Pick(form, docs);
        await Find(form);
        form.CloseTab((TabPage)docs.Parent!);

        PickRootInTree(form);
        await Pump.Loaded(archive);

        await Assert.That(form.ActiveView).IsEqualTo(archive);
        await Assert.That(archive.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(source.CurrentPath).IsEqualTo(fixture.At("Source"));

        form.CloseTab((TabPage)archive.Parent!);
        form.CloseTab((TabPage)source.Parent!);
        await Assert.That(form.FindHost.ResultsShowing).IsTrue();

        PickRootInTree(form);
        var made = form.ActiveView!;
        await Pump.Loaded(made);

        await Assert.That(made.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(form.Views.Count()).IsEqualTo(1);
        await Assert.That(form.FindHost.ResultsView).IsNotNull();
    }

    // New on the tree's right-click menu while Find Results shows: made in the folder's tab that
    // showed before it.
    [Test]
    public async Task NewInTheTreeFromFindResults()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new(), fixture.Root);
        var view = form.ActiveView!;
        await Find(form);

        await form.CreateFromTree(fixture.At("Docs"), null);
        await Pump.Loaded(view);

        await Assert.That(form.ActiveView).IsEqualTo(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
        await Assert.That(Directory.Exists(fixture.At("Docs", "New folder"))).IsTrue();
    }

    static Task Pick(MainForm form, FolderView view)
    {
        form.Tabs.SelectedTab = (TabPage)view.Parent!;
        return Pump.Loaded(view);
    }

    static Task Find(MainForm form)
    {
        form.FindHost.Open();
        form.FindHost.Panel.FindBox.Text = "alpha";
        form.FindHost.Find();
        return form.FindHost.ResultsView!.Searching;
    }

    // Pinned's one folder, the fixture's, clicked.
    static void PickRootInTree(MainForm form) =>
        form.Tree.SelectByUser(form.Tree.Nodes[0].Nodes[0]);

    static async Task<MainForm> Show(Fixture fixture, Settings settings, params string[] paths)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm(paths, settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        foreach (var view in form.Views)
        {
            await Pump.Loaded(view);
        }

        return form;
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
