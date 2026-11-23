/// <summary>
/// Find in Files in a window: the strip, the Find Results tab, and Replace from it.
/// </summary>
[STAThreadExecutor]
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
        form.FindPanel.WhereBox.Text = @"C:\Elsewhere, *.aleph";

        form.OpenFind();

        await Assert.That(form.FindPanel.Visible).IsTrue();
        await Assert.That(form.FindPanel.WhereBox.Text).IsEqualTo($"{fixture.Root}, *.aleph");
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
        form.OpenFind();
        form.FindPanel.FindBox.Text = "alpha";

        form.Find();
        await form.ResultsView!.Searching;
        var results = form.ResultsView.Results.ToText().Replace(fixture.Root, "{root}");
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
        form.OpenFind();
        form.FindPanel.FindBox.Text = "alpha";
        form.FindPanel.ReplaceBox.Text = "omega";
        var asked = new List<string>();
        form.ConfirmReplace = (matches, files, find, replacement) =>
        {
            asked.Add($"{matches} in {files}: {find} -> {replacement}");
            return true;
        };

        await form.Replace();

        await Verify(
            new
            {
                asked,
                file = await File.ReadAllTextAsync(file),
                results = form.ResultsView!.Results.ToText().Replace(fixture.Root, "{root}")
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
        await form.ResultsView!.Searching;

        await Verify(
            new
            {
                emptyText,
                find = form.FindPanel.FindBox.Text,
                where = form.FindPanel.WhereBox.Text.Replace(fixture.Root, "{root}"),
                form.FindPanel.ReadQuery().Regex,
                results = form.ResultsView.Results.ToText().Replace(fixture.Root, "{root}")
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
        form.OpenFind();
        form.FindPanel.FindBox.Text = "alpha";
        Application.DoEvents();
        var moves = new List<Rectangle>();
        form.FindPanel.LocationChanged += (_, _) => moves.Add(form.FindPanel.Bounds);
        form.FindPanel.SizeChanged += (_, _) => moves.Add(form.FindPanel.Bounds);

        form.Find();
        await form.ResultsView!.Searching;
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
        await form.ResultsView!.Searching;
        Application.DoEvents();

        await Verify(
            new
            {
                withoutFilter,
                withFilter,
                found,
                onResults = link.Visible,
                find = form.FindPanel.FindBox.Text,
                results = form.ResultsView.Results.ToText().Replace(fixture.Root, "{root}")
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
        form.OpenFind();
        form.FindPanel.FindBox.Text = "alpha";
        form.Find();
        await form.ResultsView!.Searching;
        var page = (TabPage) form.ResultsView.Parent!;

        form.CloseTab(page);

        await Assert.That(form.ResultsView).IsNull();
        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(form.CanReopenClosedTab).IsFalse();
    }

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
