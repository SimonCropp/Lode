/// <summary>
/// What the file list shows and selects, as text. These don't depend on how anything is
/// drawn, so they hold on any machine.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class FolderViewTests
{
    const int folderColumn = 3;
    const int dateColumn = 4;
    const int typeColumn = 5;
    const int sizeColumn = 6;

    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    [Test]
    public Task DisplayNameHidesShortcutExtensions()
    {
        (string Name, bool IsDirectory)[] entries =
        [
            ("DbToMermaid.slnx.lnk", false),
            ("Site.url", false),
            ("UPPER.LNK", false),
            (".lnk", false),
            ("notes.txt", false),
            ("Folder.lnk", true)
        ];
        return Verify(entries.ToDictionary(
            _ => $"{_.Name} (directory: {_.IsDirectory})",
            _ => FolderView.DisplayName(
                new(_.Name, $@"C:\{_.Name}", _.IsDirectory, 0, default, _.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal))))
                .Snapshot(
                    """
                    {
                      .lnk (directory: False): .lnk,
                      DbToMermaid.slnx.lnk (directory: False): DbToMermaid.slnx,
                      Folder.lnk (directory: True): Folder.lnk,
                      notes.txt (directory: False): notes.txt,
                      Site.url (directory: False): Site,
                      UPPER.LNK (directory: False): UPPER
                    }
                    """);
    }

    // As in Explorer: what the shortcut points at, with the shell's arrow over it.
    [Test]
    public async Task AShortcutShowsTheArrow()
    {
        using var fixture = Fixture.Create();
        var target = fixture.At("Docs", "guide.aleph");
        var shortcut = fixture.At("Docs", "Link to guide.lnk");
        ShellLink.Save(shortcut, target);
        using var view = await Open(fixture.At("Docs"));

        var file = view.FindRow("guide", 0);
        var link = view.FindRow("Link to guide", 0);

        await Assert.That(view.RowIcon(link))
            .IsEqualTo(ShellListView.PaddedIcon(ShellIcons.GetIcon(shortcut, false), ShellIcons.ShortcutOverlay));
        await Assert.That(view.RowIcon(file))
            .IsEqualTo(ShellListView.PaddedIcon(ShellIcons.GetIcon(target, false)));
    }

    // At a repository's root, the files a folder down that Options names are listed too, as
    // shortcuts to them: not those further down, nor any in a hidden folder while hidden items
    // aren't shown.
    [Test]
    public async Task PromotesFilesFromTheFoldersBelowARepositoryRoot()
    {
        using var fixture = CreateRepository();
        await File.WriteAllBytesAsync(fixture.At("Cache", "cached.aleph"), new byte[5]);
        using var view = await Open(fixture.Root, Promoting("*.aleph"));

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 10 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    guide.aleph  900 bytes  (promoted)  in Docs,
                    file    main.aleph  5,000 bytes  (promoted)  in Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task NothingIsPromotedOutsideARepository()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, Promoting("*.aleph"));

        await Assert.That(view.Entries.Any(_ => _.Promoted)).IsFalse();
    }

    // Never what git keeps for itself, even with hidden items shown.
    [Test]
    public async Task NothingIsPromotedFromGit()
    {
        using var fixture = CreateRepository();
        await File.WriteAllBytesAsync(fixture.At(".git", "config.aleph"), new byte[5]);
        using var view = await Open(fixture.Root, Promoting("*.aleph", showHidden: true));

        await Assert.That(Names(view)).Contains("guide.aleph");
        await Assert.That(Names(view)).DoesNotContain("config.aleph");
    }

    [Test]
    public async Task APromotedFileShowsTheArrow()
    {
        using var fixture = CreateRepository();
        using var view = await Open(fixture.Root, Promoting("main.aleph"));

        var row = view.FindRow("main", 0);

        await Assert.That(view.RowIcon(row))
            .IsEqualTo(ShellListView.PaddedIcon(ShellIcons.GetIcon(fixture.At("Source", "main.aleph"), false), ShellIcons.ShortcutOverlay));
    }

    // Written to where it is, a folder down, which the watch of the root alone wouldn't hear of.
    [Test]
    public async Task APromotedFileChangedOnDiskIsPickedUp()
    {
        using var fixture = CreateRepository();
        using var view = await Open(fixture.Root, Promoting("main.aleph"));

        await File.WriteAllBytesAsync(fixture.At("Source", "main.aleph"), new byte[7_000]);

        await Pump.Until(() => view.Entries.Any(_ => _ is {Name: "main.aleph", Size: 7_000}));
    }

    // The search of the subfolders finds it where it is, with its folder, so the row promoted
    // to the root makes way for that one.
    [Test]
    public async Task ASearchListsAPromotedFileOnce()
    {
        using var fixture = CreateRepository();
        using var view = await Open(fixture.Root, Promoting("main.aleph"));

        view.Filter = "main";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 1 item found,
                  filter: main,
                  rows: [
                    file    main.aleph  5,000 bytes  in Source
                  ],
                  selected: []
                }
                """);
    }

    // It is a shortcut to the file: deleting or renaming it from the root does nothing, nor
    // does renaming another item selected with it.
    [Test]
    public async Task APromotedFileIsNotDeletedOrRenamed()
    {
        using var fixture = CreateRepository();
        using var view = await Open(fixture.Root, Promoting("main.aleph"));
        var names = Names(view);
        var promoted = names.IndexOf("main.aleph");
        var other = names.IndexOf("notes.aleph");
        view.List.SelectedIndices.Add(promoted);

        view.Delete(false);
        var renamingIt = new LabelEditEventArgs(promoted);
        view.OnBeforeLabelEdit(null, renamingIt);
        view.List.SelectedIndices.Add(other);
        var renamingAnother = new LabelEditEventArgs(other);
        view.OnBeforeLabelEdit(null, renamingAnother);

        await Assert.That(File.Exists(fixture.At("Source", "main.aleph"))).IsTrue();
        await Assert.That(renamingIt.CancelEdit).IsTrue();
        await Assert.That(renamingAnother.CancelEdit).IsTrue();
    }

    // The fixture with a .git folder at its root, hidden as git for Windows makes it.
    static Fixture CreateRepository([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var fixture = Fixture.Create(file, member);
        var git = fixture.At(".git");
        Directory.CreateDirectory(git);
        File.SetAttributes(git, FileAttributes.Directory | FileAttributes.Hidden);
        return fixture;
    }

    static Settings Promoting(string pattern, bool showHidden = false) =>
        new()
        {
            ShowHidden = showHidden,
            PromotedFiles = [pattern]
        };

    [Test]
    public async Task ListsFoldersFirstInNaturalOrder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task SortBySizeDescending()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Source,
                    folder  Docs,
                    folder  Archive,
                    file    Report1.beta  1,500,000 bytes,
                    file    report10.beta  300,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    notes.aleph  12 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    // A folder of very many items is sorted by another column off the UI thread, the rows
    // changing once it is: the same order as one sorted where it is, and the other way from
    // there only turned round.
    [Test]
    public async Task ALargeFolderIsSortedOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        using var here = await Open(fixture.Root);
        here.SortBy(sizeColumn);
        var sortedHere = Names(here);
        here.SortBy(sizeColumn);
        var turnedHere = Names(here);
        using var view = await Open(fixture.Root);
        var from = FolderView.SortOffThreadFrom;
        FolderView.SortOffThreadFrom = 0;
        try
        {
            view.SortBy(sizeColumn);
            var atOnce = Names(view);
            await Pump.Until(() => Names(view).SequenceEqual(sortedHere));
            view.SortBy(sizeColumn);

            await Assert.That(atOnce).IsNotEquivalentTo(sortedHere, CollectionOrdering.Matching);
            await Assert.That(Names(view)).IsEquivalentTo(turnedHere, CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.SortOffThreadFrom = from;
        }
    }

    // Sorted another way, the list scrolls to the item selected, which the sort has moved from
    // the rows showing to the end, as Explorer's does. A large folder, sorted off the UI thread,
    // scrolls once its rows are in their new order.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SortingScrollsToTheSelection(bool offThread)
    {
        using var fixture = Fixture.Create();
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < 100; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, $"item{index:D3}.txt"), new byte[index]);
        }

        using var form = Host();
        var view = await OpenIn(form, folder);
        view.SortBy(sizeColumn);
        var picked = Names(view).IndexOf("item000.txt");
        view.List.SelectedIndices.Add(picked);
        view.List.Items[picked].Focused = true;
        var from = FolderView.SortOffThreadFrom;
        if (offThread)
        {
            FolderView.SortOffThreadFrom = 0;
        }

        try
        {
            view.SortBy(sizeColumn);
            await Pump.Until(() => Names(view).IndexOf("item000.txt") == 99);
        }
        finally
        {
            FolderView.SortOffThreadFrom = from;
        }

        var row = view.List.GetItemRect(99);

        await Assert.That(Selected(view)).IsEquivalentTo(["item000.txt"]);
        await Assert.That(view.List.ClientRectangle.Contains(row)).IsTrue();
    }

    // Clicked twice in a row, a large folder is sorted off the UI thread both times: the first
    // sort, overtaken by the second before it is done, gives the list nothing, rather than its
    // rows sorted the second way where they are, and the list ends sorted the second way.
    [Test]
    public async Task ALargeFolderSortedTwiceInARowIsNotSortedOnTheUIThread()
    {
        using var fixture = Fixture.Create();
        using var here = await Open(fixture.Root);
        here.SortBy(sizeColumn);
        here.SortBy(sizeColumn);
        var descending = Names(here);
        using var view = await Open(fixture.Root);
        var uiThread = Environment.CurrentManagedThreadId;
        var sortedHere = 0;
        using var second = new ManualResetEventSlim();
        var from = FolderView.SortOffThreadFrom;
        FolderView.SortOffThreadFrom = 0;
        FolderView.Sorting = (_, ascending) =>
        {
            if (Environment.CurrentManagedThreadId == uiThread)
            {
                sortedHere++;
                return;
            }

            // The second sort is held until the first is done, as it is when the second's
            // thread is slower to start.
            if (!ascending)
            {
                second.Wait(TimeSpan.FromSeconds(10));
            }
        };
        try
        {
            view.SortBy(sizeColumn);
            view.SortBy(sizeColumn);
            await Pump.For(TimeSpan.FromMilliseconds(300));
            second.Set();
            await Pump.Until(() => Names(view).SequenceEqual(descending));

            await Assert.That(sortedHere).IsEqualTo(0);
            await Assert.That(view.Sort).IsEqualTo((sizeColumn, false));
        }
        finally
        {
            second.Set();
            FolderView.Sorting = null;
            FolderView.SortOffThreadFrom = from;
        }
    }

    // While a large folder is sorted off the UI thread, what gives the list its rows meanwhile,
    // more typed in the filter say, gives them in the order they are in until the sort is done,
    // rather than sorting them there as well.
    [Test]
    public async Task WhileALargeFolderIsSortedOffTheUIThreadItIsNotSortedOnIt()
    {
        using var fixture = Fixture.Create();
        using var here = await Open(fixture.Root);
        here.Filter = "a";
        await Pump.Loaded(here);
        here.SortBy(sizeColumn);
        here.Filter = "al";
        await Pump.Loaded(here);
        var sortedHere = Names(here);
        using var view = await Open(fixture.Root);
        view.Filter = "a";
        await Pump.Loaded(view);
        var uiThread = Environment.CurrentManagedThreadId;
        var sortedOnUIThread = 0;
        using var release = new ManualResetEventSlim();
        var from = FolderView.SortOffThreadFrom;
        FolderView.SortOffThreadFrom = 0;
        FolderView.Sorting = (column, _) =>
        {
            if (Environment.CurrentManagedThreadId == uiThread)
            {
                sortedOnUIThread++;
                return;
            }

            if (column == sizeColumn)
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        try
        {
            view.SortBy(sizeColumn);
            view.Filter = "al";
            await Pump.For(TimeSpan.FromMilliseconds(500));
            var whileSorting = sortedOnUIThread;
            release.Set();
            await Pump.Loaded(view);
            await Pump.Until(() => Names(view).SequenceEqual(sortedHere));

            await Assert.That(whileSorting).IsEqualTo(0);
            await Assert.That(sortedOnUIThread).IsEqualTo(0);
        }
        finally
        {
            release.Set();
            FolderView.Sorting = null;
            FolderView.SortOffThreadFrom = from;
        }
    }

    [Test]
    public async Task SortByType()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(typeColumn);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    // Date modified starts newest first, as Explorer sorts it.
    [Test]
    public async Task SortByDate()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(dateColumn);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Source,
                    folder  Docs,
                    folder  Archive,
                    file    summary.gamma  0 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes
                  ],
                  selected: []
                }
                """);
    }

    // Clicked again, Date modified is oldest first.
    [Test]
    public async Task SortByDateTwice()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(dateColumn);
        view.SortBy(dateColumn);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    Report1.beta  1,500,000 bytes,
                    file    notes.aleph  12 bytes,
                    file    report10.beta  300,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task FilterByPartOfName()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "REPORT";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 3 items found,
                  filter: REPORT,
                  rows: [
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes
                  ],
                  selected: []
                }
                """);
    }

    // Everything beneath the folder is searched, all the way down, but for hidden folders.
    [Test]
    public async Task FilterByWildcard()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Cache", "cached.aleph"), "");
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 4 items found,
                  filter: *.aleph,
                  rows: [
                    file    notes.aleph  12 bytes,
                    file    guide.aleph  900 bytes  in Docs,
                    file    main.aleph  5,000 bytes  in Source,
                    file    leaf.aleph  40 bytes  in Source\Deep
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task SearchFindsInHiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Cache", "cached.aleph"), "");
        using var view = await Open(fixture.Root, new() {ShowHidden = true});

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 6 items found,
                  filter: *.aleph,
                  rows: [
                    file    hidden.aleph  64 bytes  (hidden),
                    file    notes.aleph  12 bytes,
                    file    cached.aleph  0 bytes  in Cache,
                    file    guide.aleph  900 bytes  in Docs,
                    file    main.aleph  5,000 bytes  in Source,
                    file    leaf.aleph  40 bytes  in Source\Deep
                  ],
                  selected: []
                }
                """);
    }

    // In a repository, the search box leaves out what git ignores, as Find in Files does, and
    // never goes into .git; a folder in no repository is searched whole. With Search what git
    // ignores too ticked in Options, the ignored is found as well.
    [Test]
    public async Task SearchLeavesOutWhatGitIgnores()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\n*.log\n");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "notes.cs"), "");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Notes");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        await File.WriteAllTextAsync(Path.Combine(repo, "bin", "notes.dll"), "");
        await File.WriteAllTextAsync(Path.Combine(repo, "notes.md"), "");
        await File.WriteAllTextAsync(Path.Combine(repo, "notes.log"), "");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "notes"), "");
        Directory.CreateDirectory(code.At("Plain", "bin"));
        await File.WriteAllTextAsync(code.At("Plain", "bin", "notes.dll"), "");
        async Task<List<string>> Found(bool includeIgnored)
        {
            using var view = await Open(code.Root, new() { ShowHidden = true, SearchIgnored = includeIgnored });
            view.Filter = "notes";
            await Pump.Loaded(view);
            return view.Entries
                .Select(_ => code.Relative(_.FullPath))
                .Where(_ => !_.StartsWith("origins/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        var leftOut = await Found(false);
        var everything = await Found(true);

        await Assert.That(leftOut).IsEquivalentTo(["Plain/bin/notes.dll", "Repo/notes.md", "Repo/src/notes.cs"], CollectionOrdering.Matching);
        await Assert.That(everything).IsEquivalentTo(["Plain/bin/notes.dll", "Repo/bin/notes.dll", "Repo/notes.log", "Repo/notes.md", "Repo/src/notes.cs"], CollectionOrdering.Matching);
    }

    // A search shows where each item is, beside its name, and starts sorted by it: by how many
    // levels down, then by name. Clearing it takes the column away.
    [Test]
    public async Task SearchShowsTheFolderOfEachItem()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);
        var searched = view.ColumnTitles.ToList();
        var startsSorted = view.Sort;
        var sorted = view.Entries.Select(_ => Row(_, view.CurrentPath)).ToList();
        view.SortBy(folderColumn);
        var descending = view.Entries.Select(_ => Row(_, view.CurrentPath)).ToList();
        var cells = view.FirstRowCells.ToList();

        view.Filter = "";

        await Verify(
            new
            {
                searched,
                startsSorted,
                firstRow = cells,
                sorted,
                descending,
                cleared = view.ColumnTitles
            })
            .Snapshot(
                """
                {
                  searched: [
                    Name,
                    Folder,
                    Date modified,
                    Type,
                    Size
                  ],
                  startsSorted: {
                    Item1: 3,
                    Item2: true
                  },
                  firstRow: [
                    leaf.aleph,
                    Source\Deep,
                    23/03/2025 10:26am,
                    aleph File,
                    1 KB
                  ],
                  sorted: [
                    file    notes.aleph  12 bytes,
                    file    guide.aleph  900 bytes  in Docs,
                    file    main.aleph  5,000 bytes  in Source,
                    file    leaf.aleph  40 bytes  in Source\Deep
                  ],
                  descending: [
                    file    leaf.aleph  40 bytes  in Source\Deep,
                    file    main.aleph  5,000 bytes  in Source,
                    file    guide.aleph  900 bytes  in Docs,
                    file    notes.aleph  12 bytes
                  ],
                  cleared: [
                    Name,
                    Date modified,
                    Type,
                    Size
                  ]
                }
                """);
    }

    // A search doesn't go through a junction, which here leads back up to the folder searched.
    [Test]
    public async Task SearchDoesNotFollowLinks()
    {
        using var fixture = Fixture.Create();
        var link = fixture.At("Source", "Loop");
        using (var junction = Process.Start(
                   new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{fixture.Root}\"")
                   {
                       CreateNoWindow = true,
                       UseShellExecute = false
                   })!)
        {
            await junction.WaitForExitAsync();
        }

        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(
            new
            {
                linked = Directory.Exists(Path.Combine(link, "Docs")),
                rows = view.Entries.Select(_ => Row(_, view.CurrentPath))
            })
            .Snapshot(
                """
                {
                  linked: true,
                  rows: [
                    file    notes.aleph  12 bytes,
                    file    guide.aleph  900 bytes  in Docs,
                    file    main.aleph  5,000 bytes  in Source,
                    file    leaf.aleph  40 bytes  in Source\Deep
                  ]
                }
                """);
    }

    [Test]
    public async Task SearchThatFindsNothing()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "nothing";
        var searching = new
        {
            status = view.StatusText,
            emptyText = view.EmptyText
        };
        await Pump.Loaded(view);

        await Verify(
            new
            {
                searching,
                status = view.StatusText,
                emptyText = view.EmptyText
            })
            .Snapshot(
                """
                {
                  searching: {
                    status: 0 items found so far, searching subfolders,
                    emptyText: Searching subfolders...
                  },
                  status: 0 items found,
                  emptyText: No items match your search. [Search inside the files]
                }
                """);
    }

    // What a filter that matched no names searches for inside the files.
    [Test]
    public Task TextAFilterSearchesFor() =>
        Verify(
            new[] {"11.0", "\"11.0", "*.csproj", "*version*", "v?.*", "a*b"}
                .ToDictionary(_ => _, _ => FolderView.TextFor(_).ToString()))
                .Snapshot(
                    """
                    {
                      "11.0: ("11.0, False),
                      *.csproj: (.csproj, False),
                      *version*: (version, False),
                      11.0: (11.0, False),
                      a*b: (a.*b, True),
                      v?.*: (v.\., True)
                    }
                    """);

    // A search stops once it has found the most it may in the subfolders, and says so. The
    // folder's own matches are shown whatever.
    [Test]
    public async Task SearchStopsAtTheMostItFinds()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.MostFound = 2;

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 3 items found, only the first 2 in subfolders,
                  filter: *.aleph,
                  rows: [
                    file    notes.aleph  12 bytes,
                    file    guide.aleph  900 bytes  in Docs,
                    file    main.aleph  5,000 bytes  in Source
                  ],
                  selected: []
                }
                """);
    }

    // What is selected among what a search found stays selected as the list is given its rows
    // again, sorted another way say, and an item of the same name in another folder doesn't
    // take its place.
    [Test]
    public async Task SelectionAmongWhatASearchFoundStays()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllBytesAsync(fixture.At("Docs", "notes.aleph"), new byte[3]);
        await File.WriteAllBytesAsync(fixture.At("Source", "Deep", "notes.aleph"), new byte[3]);
        using var view = await Open(fixture.Root);
        view.Filter = "*.aleph";
        await Pump.Loaded(view);
        var picked = new[] {fixture.At("Docs", "notes.aleph"), fixture.At("Source", "main.aleph")};
        for (var index = 0; index < view.Entries.Count; index++)
        {
            if (picked.Contains(view.Entries[index].FullPath))
            {
                view.List.SelectedIndices.Add(index);
            }
        }

        List<string> SelectedPaths() =>
            view.SelectedEntries().Select(_ => fixture.Relative(_.FullPath)).Order().ToList();

        var before = SelectedPaths();
        view.SortBy(folderColumn);
        var byFolder = SelectedPaths();
        view.SortBy(sizeColumn);
        var bySize = SelectedPaths();

        await Assert.That(before).Count().IsEqualTo(2);
        await Assert.That(byFolder).IsEquivalentTo(before);
        await Assert.That(bySize).IsEquivalentTo(before);
    }

    // Sorted another way before the search sent anything, what it found, sorted as the list
    // was when it started, is put in the list's order now.
    [Test]
    public async Task SearchSortedAnotherWayAsItRunsEndsInOrder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 4 items found,
                  filter: *.aleph,
                  rows: [
                    file    main.aleph  5,000 bytes  in Source,
                    file    guide.aleph  900 bytes  in Docs,
                    file    leaf.aleph  40 bytes  in Source\Deep,
                    file    notes.aleph  12 bytes
                  ],
                  selected: []
                }
                """);
    }

    // What a search found is not watched, so what Lode renames or moves among it is followed
    // there: a folder renamed in the list takes what was found inside it along, and goes itself,
    // its name no longer matching; a file moved to another subfolder shows there; and one moved
    // out from beneath the subfolders, out of the folder or into it, is no longer among what was
    // found, the folder's own shown once.
    [Test]
    public async Task WhatASearchFoundFollowsARenameOrAMove()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Plans"));
        await File.WriteAllBytesAsync(fixture.At("Docs", "Plans", "plans.aleph"), new byte[7]);
        await File.WriteAllBytesAsync(fixture.At("Source", "plan.beta"), new byte[3]);
        await File.WriteAllBytesAsync(fixture.At("Source", "Deep", "planned.aleph"), new byte[5]);
        using var view = await Open(fixture.Root);
        view.Filter = "plan";
        await Pump.Loaded(view);
        List<string> Rows() =>
            view.Entries.Select(_ => Row(_, view.CurrentPath)).ToList();
        var searched = Rows();

        var index = Names(view).IndexOf("Plans");
        view.OnBeforeLabelEdit(null, new(index));
        view.OnAfterLabelEdit(null, new(index, "Drafts"));
        await Pump.Until(() => Rows().Contains(@"file    plans.aleph  7 bytes  in Docs\Drafts") &&
                               !Names(view).Contains("Drafts"));
        var renamed = Rows();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Source", "plan.beta")], fixture.At("Archive"), move: true);
        await Pump.Until(() => Rows().Contains("file    plan.beta  3 bytes  in Archive"));
        var movedToAnotherSubfolder = Rows();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Source", "Deep", "planned.aleph")], Path.GetDirectoryName(fixture.Root)!, move: true);
        await Pump.Until(() => !Names(view).Contains("planned.aleph"));
        var movedOut = Rows();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Archive", "plan.beta")], fixture.Root, move: true);
        await Pump.Until(() => Rows().Contains("file    plan.beta  3 bytes") &&
                               !Rows().Contains("file    plan.beta  3 bytes  in Archive"));
        var movedIntoTheFolder = Rows();

        await Verify(
            new
            {
                searched,
                renamed,
                movedToAnotherSubfolder,
                movedOut,
                movedIntoTheFolder
            })
            .Snapshot(
                """
                {
                  searched: [
                    folder  Plans  in Docs,
                    file    plan.beta  3 bytes  in Source,
                    file    planned.aleph  5 bytes  in Source\Deep,
                    file    plans.aleph  7 bytes  in Docs\Plans
                  ],
                  renamed: [
                    file    plan.beta  3 bytes  in Source,
                    file    planned.aleph  5 bytes  in Source\Deep,
                    file    plans.aleph  7 bytes  in Docs\Drafts
                  ],
                  movedToAnotherSubfolder: [
                    file    plan.beta  3 bytes  in Archive,
                    file    planned.aleph  5 bytes  in Source\Deep,
                    file    plans.aleph  7 bytes  in Docs\Drafts
                  ],
                  movedOut: [
                    file    plan.beta  3 bytes  in Archive,
                    file    plans.aleph  7 bytes  in Docs\Drafts
                  ],
                  movedIntoTheFolder: [
                    file    plan.beta  3 bytes,
                    file    plans.aleph  7 bytes  in Docs\Drafts
                  ]
                }
                """);
    }

    // On This PC the box narrows the drives, rather than searching every one of them.
    [Test]
    public async Task FilterOnThisPCDoesNotSearch()
    {
        using var view = new FolderView(new(), noPlaces);
        view.Navigate(Locations.ThisPC);
        await Pump.Loaded(view);

        view.Filter = "no drive is called this";
        await Pump.Loaded(view);

        // How many drives there are depends on the machine.
        await Verify(
            new
            {
                narrowed = view.StatusText.StartsWith("0 items (filtered from "),
                emptyText = view.EmptyText,
                columns = view.ColumnTitles
            })
            .Snapshot(
                """
                {
                  narrowed: true,
                  emptyText: No items match your search.,
                  columns: [
                    Name,
                    Free space,
                    Type,
                    Total size
                  ]
                }
                """);
    }

    [Test]
    public async Task FilterIsClearedByNavigating()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Filter = "report";

        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects/Source,
                  status: 2 items,
                  filter: ,
                  rows: [
                    folder  Deep,
                    file    main.aleph  5,000 bytes
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task HiddenItemsWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() {ShowHidden = true});

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 10 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Cache  (hidden),
                    folder  Docs,
                    folder  Source,
                    file    hidden.aleph  64 bytes  (hidden),
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: []
                }
                """);
    }

    [Test]
    public async Task History()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var steps = new List<object>();

        async Task Step(string name, Action action)
        {
            action();
            await Pump.Loaded(view);
            steps.Add(
                new
                {
                    step = name,
                    path = fixture.Relative(view.CurrentPath),
                    view.CanGoBack,
                    view.CanGoForward
                });
        }

        await Step("start", () =>
        {
        });
        await Step("open Source", () => view.Navigate(fixture.At("Source")));
        await Step("open Deep", () => view.Navigate(fixture.At("Source", "Deep")));
        await Step("back", view.GoBack);
        await Step("back", view.GoBack);
        await Step("back at the start does nothing", view.GoBack);
        await Step("forward", view.GoForward);
        await Step("open Docs drops the forward history", () => view.Navigate(fixture.At("Docs")));
        await Step("forward at the end does nothing", view.GoForward);
        await Step("up", view.GoUp);

        await Verify(steps)
            .Snapshot(
                """
                [
                  {
                    step: start,
                    path: Projects,
                    CanGoBack: false,
                    CanGoForward: false
                  },
                  {
                    step: open Source,
                    path: Projects/Source,
                    CanGoBack: true,
                    CanGoForward: false
                  },
                  {
                    step: open Deep,
                    path: Projects/Source/Deep,
                    CanGoBack: true,
                    CanGoForward: false
                  },
                  {
                    step: back,
                    path: Projects/Source,
                    CanGoBack: true,
                    CanGoForward: true
                  },
                  {
                    step: back,
                    path: Projects,
                    CanGoBack: false,
                    CanGoForward: true
                  },
                  {
                    step: back at the start does nothing,
                    path: Projects,
                    CanGoBack: false,
                    CanGoForward: true
                  },
                  {
                    step: forward,
                    path: Projects/Source,
                    CanGoBack: true,
                    CanGoForward: true
                  },
                  {
                    step: open Docs drops the forward history,
                    path: Projects/Docs,
                    CanGoBack: true,
                    CanGoForward: false
                  },
                  {
                    step: forward at the end does nothing,
                    path: Projects/Docs,
                    CanGoBack: true,
                    CanGoForward: false
                  },
                  {
                    step: up,
                    path: Projects,
                    CanGoBack: true,
                    CanGoForward: false
                  }
                ]
                """);
    }

    // Going to the folder already shown, by its button in the breadcrumb or Enter in the address
    // bar, reads it again, as in Explorer. What is forward in the history stays, nothing is
    // added to it, and the selection and the filter are kept.
    [Test]
    public async Task GoingWhereItIsAlreadyReadsItAgain()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        view.GoBack();
        await Pump.Loaded(view);
        view.Reveal(fixture.At("Source", "main.aleph"));
        await Pump.Loaded(view);
        view.Filter = "a";
        await Pump.Loaded(view);
        await File.WriteAllBytesAsync(fixture.At("Source", "arrived.aleph"), new byte[3]);

        view.Navigate(fixture.At("source").ToLowerInvariant());
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(view.CanGoForward).IsTrue();
        await Assert.That(Selected(view)).IsEquivalentTo(["main.aleph"]);
        await Assert.That(view.Filter).IsEqualTo("a");
        view.GoBack();
        await Pump.Loaded(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
    }

    // Where the view is in its history changes only once it gets there. Sent back to somewhere
    // on the network, then elsewhere before that has been heard from, Back is to the folder it
    // was in, not the one it never got back to.
    [Test]
    public async Task BackOvertakenLeavesTheHistoryAsItWas()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(view);
        view.IsRemote = _ => Locations.Same(_, fixture.At("Source"));

        view.GoBack();
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));
        view.GoBack();
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source", "Deep"));
    }

    // A folder in the history deleted since is no place to go back to: Back goes to the one
    // before it, rather than saying it can't be found every time, with nothing before it to be
    // got to. It is out of the history from then on.
    [Test]
    public async Task BackGoesPastAFolderThatHasGone()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        Directory.Delete(fixture.At("Docs"), true);

        view.GoBack();
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(view.CanGoBack).IsFalse();
        await Assert.That(view.ForwardHistory.Select(_ => _.Path)).IsEquivalentTo([fixture.At("Source")]);
    }

    // Back's list is where it would go, the nearest first, and Forward's the same the other
    // way. Gone to from the list, somewhere keeps the rest of the history either side of it.
    [Test]
    public async Task GoingStraightToSomewhereInTheHistory()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(view);
        var back = view.BackHistory.Select(_ => Path.GetFileName(_.Path)).ToList();

        view.GoToHistory(view.BackHistory[^1].Position);
        await Pump.Loaded(view);
        var forward = view.ForwardHistory.Select(_ => Path.GetFileName(_.Path)).ToList();
        view.GoToHistory(view.ForwardHistory[1].Position);
        await Pump.Loaded(view);

        await Assert.That(back).IsEquivalentTo(["Source", "Docs", "Projects"], CollectionOrdering.Matching);
        await Assert.That(forward).IsEquivalentTo(["Docs", "Source", "Deep"], CollectionOrdering.Matching);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(view.CanGoBack).IsTrue();
        await Assert.That(view.CanGoForward).IsTrue();
    }

    [Test]
    public async Task UpSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));

        view.GoUp();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items    1 item selected,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: [
                    Source
                  ]
                }
                """);
    }

    // A Reveal overtaken while its folder is still being asked after selects nothing where the
    // view went instead: going up still lands with the folder left selected.
    [Test]
    public async Task UpAfterAnOvertakenRevealSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));
        view.IsRemote = _ => Locations.Same(_, fixture.At("Docs"));

        view.Reveal(fixture.At("Docs", "guide.aleph"));
        view.GoUp();
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["Source"]);
    }

    // As clicking a folder above in the breadcrumb does.
    [Test]
    public async Task GoingToAFolderAboveSelectsTheFolderLeadingBack()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        view.Navigate(fixture.Root);
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items    1 item selected,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: [
                    Source
                  ]
                }
                """);
    }

    [Test]
    public async Task BackToAFolderAboveSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));
        view.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(view);

        view.GoBack();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects/Source,
                  status: 2 items    1 item selected,
                  filter: ,
                  rows: [
                    folder  Deep,
                    file    main.aleph  5,000 bytes
                  ],
                  selected: [
                    Deep
                  ]
                }
                """);
    }

    // A drive refusing to say how big it is, or gone meanwhile, is listed in This PC without
    // its size, rather than making the whole listing an error.
    [Test]
    public async Task ADriveThatCannotBeSizedIsListedWithoutItsSize()
    {
        var refused = FolderView.DriveEntry(@"X:\", () => throw new UnauthorizedAccessException());
        var gone = FolderView.DriveEntry(@"Y:\", () => throw new IOException());
        var sized = FolderView.DriveEntry(@"Z:\", () => (1_000, 400));

        await Assert.That(refused).IsEqualTo(new(@"X:\", @"X:\", true, 0, default, FileAttributes.Directory, IsDrive: true, FreeSpace: 0));
        await Assert.That(gone.Size).IsEqualTo(0);
        await Assert.That((sized.Size, sized.FreeSpace)).IsEqualTo((1_000L, 400L));
    }

    // Delete and Create shortcut chosen from Show more options are done by Lode, as from its own
    // menu, so Ctrl+Z hears of them, rather than by the shell alone.
    [Test]
    public async Task DeleteAndCreateShortcutFromTheClassicMenuAreLodes()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var deleted = new List<string>();
        view.DeleteItems = (_, paths, _) =>
        {
            deleted.AddRange(paths);
            return Task.FromResult(true);
        };
        view.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(view);
        var selection = view.Entries.Where(_ => _.Name == "notes.aleph").ToList();

        var deletes = view.HandleItemVerb("delete", selection);
        var links = view.HandleItemVerb("link", selection);
        await Pump.Loaded(view);

        await Assert.That((deletes, links)).IsEqualTo((true, true));
        await Assert.That(deleted).IsEquivalentTo([fixture.At("notes.aleph")]);
        await Assert.That(File.Exists(fixture.At("notes - Shortcut.lnk"))).IsTrue();
    }

    // A folder of 500 files, item000.txt to item499.txt, each as many bytes as its number.
    static string FiveHundred(Fixture fixture)
    {
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < 500; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"item{index:D3}.txt"), new byte[index]);
        }

        return folder;
    }

    // Read again as soon as something in it changes, however long the first read took on a busy
    // machine, which would otherwise leave it ten times as long.
    static async Task<FolderView> OpenReadingAgainAtOnce(Form form, string folder)
    {
        var view = await OpenIn(form, folder);
        view.ReadTook(TimeSpan.Zero);
        return view;
    }

    // The row with the focus: its name, and where it is.
    static (string? Name, int Index) Focus(FolderView view) =>
        ((view.List.FocusedItem?.Tag as FileEntry)?.Name, view.List.FocusedItem?.Index ?? -1);

    static void FocusRow(FolderView view, int index, bool select)
    {
        if (select)
        {
            view.List.SelectedIndices.Add(index);
        }

        view.List.Items[index].Focused = true;
    }

    // Deleted, the item focused is gone from the list, and the one that took its place has the
    // focus, selected as it was, as in Explorer, so Down or Delete go on from there rather than
    // from the top.
    [Test]
    public async Task DeletingTheFocusedItemFocusesTheOneInItsPlace()
    {
        using var fixture = Fixture.Create();
        var folder = FiveHundred(fixture);
        using var form = Host();
        var view = await OpenReadingAgainAtOnce(form, folder);
        view.DeleteItems = (_, paths, _) =>
        {
            foreach (var path in paths)
            {
                File.Delete(path);
            }

            return Task.FromResult(true);
        };
        FocusRow(view, 300, select: true);

        view.Delete(false);
        await Pump.Until(() => view.Entries.Count == 499);

        await Assert.That(Focus(view)).IsEqualTo(("item301.txt", 300));
        await Assert.That(Selected(view)).IsEquivalentTo(["item301.txt"]);
    }

    // The last deleted, the new last has the focus.
    [Test]
    public async Task DeletingTheLastFocusesTheNewLast()
    {
        using var fixture = Fixture.Create();
        var folder = FiveHundred(fixture);
        using var form = Host();
        var view = await OpenReadingAgainAtOnce(form, folder);
        FocusRow(view, 499, select: true);

        File.Delete(Path.Combine(folder, "item499.txt"));
        await Pump.Until(() => view.Entries.Count == 499);

        await Assert.That(Focus(view)).IsEqualTo(("item498.txt", 498));
        await Assert.That(Selected(view)).IsEquivalentTo(["item498.txt"]);
    }

    // Deleted by something else, or moved away, the item focused has the one in its place
    // focused too, selected only if it was.
    [Test]
    public async Task AFocusedItemGoneFromDiskHasTheOneInItsPlaceFocused()
    {
        using var fixture = Fixture.Create();
        var folder = FiveHundred(fixture);
        using var form = Host();
        var view = await OpenReadingAgainAtOnce(form, folder);
        FocusRow(view, 200, select: false);

        File.Move(Path.Combine(folder, "item200.txt"), fixture.At("item200.txt"));
        await Pump.Until(() => view.Entries.Count == 499);

        await Assert.That(Focus(view)).IsEqualTo(("item201.txt", 200));
        await Assert.That(Selected(view)).IsEmpty();
    }

    // An item focused that is still there keeps the focus wherever it now is: another deleted
    // before it, the list sorted another way, or the item renamed. Left out by the filter, it is
    // not gone, so no other takes its place.
    [Test]
    public async Task AFocusedItemStillThereKeepsTheFocus()
    {
        using var fixture = Fixture.Create();
        var folder = FiveHundred(fixture);
        using var form = Host();
        var view = await OpenReadingAgainAtOnce(form, folder);
        FocusRow(view, 300, select: true);

        File.Delete(Path.Combine(folder, "item100.txt"));
        await Pump.Until(() => view.Entries.Count == 499);
        var anotherDeleted = Focus(view);
        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);
        var sorted = Focus(view);
        var renaming = new LabelEditEventArgs(sorted.Index, "renamed.txt");
        view.OnBeforeLabelEdit(null, renaming);
        view.OnAfterLabelEdit(null, renaming);
        await Pump.Until(() => File.Exists(Path.Combine(folder, "renamed.txt")));
        await Pump.Until(() => view.Entries.Any(_ => _.Name == "renamed.txt") && !view.Entries.Any(_ => _.Name == "item300.txt"));
        await Pump.For(TimeSpan.FromMilliseconds(600));
        var renamed = Focus(view).Name;
        var selectedRenamed = Selected(view);
        view.Filter = "item1";
        var filtered = Focus(view).Name;
        var selectedFiltered = Selected(view);

        await Assert.That(anotherDeleted).IsEqualTo(("item300.txt", 299));
        await Assert.That(sorted).IsEqualTo(("item300.txt", 498 - 299));
        await Assert.That(renamed).IsEqualTo("renamed.txt");
        await Assert.That(selectedRenamed).IsEquivalentTo(["renamed.txt"]);
        await Assert.That(filtered).IsNull();
        await Assert.That(selectedFiltered).IsEmpty();
    }

    // New's entries chosen from Show more options are done by Lode, as from its own New, so
    // Ctrl+Z hears of them: by the shell's verbs, NewFolder and a type's extension. A type Lode
    // doesn't know, and Shortcut, a wizard, are left to the shell.
    [Test]
    public async Task NewFromTheClassicMenuIsLodes()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));
        ShellNew.Entry[] types = [new(".txt", "Text Document")];

        var folder = view.HandleBackgroundVerb("NewFolder", types);
        await Pump.Loaded(view);
        var file = view.HandleBackgroundVerb(".txt", types);
        await Pump.Loaded(view);
        var unknown = view.HandleBackgroundVerb(".docx", types);
        var shortcut = view.HandleBackgroundVerb("NewLink", types);

        await Assert.That((folder, file, unknown, shortcut)).IsEqualTo((true, true, false, false));
        await Assert.That(Directory.Exists(fixture.At("Archive", "New folder"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("Archive", "New Text Document.txt"))).IsTrue();
    }

    // Typed in the address bar, what isn't somewhere here is run in the folder open, as Explorer
    // runs cmd, powershell, wt or code there. A folder of that name here, and This PC, are gone
    // to instead: but not for a command given the folder open, `code .`, which as a path would
    // be the folder named code, Windows dropping the dot.
    [Test]
    public async Task TypedCommandsRunInTheFolder()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("cmd"));
        Directory.CreateDirectory(fixture.At("Docs", "code"));
        using var view = await Open(fixture.At("Docs"));
        var started = new List<string>();
        var startTyped = FolderView.StartTyped;
        FolderView.StartTyped = _ => started.Add($"{_.FileName}|{_.Arguments}|{_.WorkingDirectory == fixture.At("Docs")}|{_.UseShellExecute}");
        try
        {
            view.GoToTyped("code .");
            view.GoToTyped("  powershell  ");
            var stayed = view.CurrentPath;
            view.GoToTyped(@"..\cmd");
            await Pump.Loaded(view);
            var folder = view.CurrentPath;
            view.GoToTyped("This PC");
            await Pump.Loaded(view);

            await Assert.That(started).IsEquivalentTo(["code|.|True|True", "powershell||True|True"], CollectionOrdering.Matching);
            await Assert.That(stayed).IsEqualTo(fixture.At("Docs"));
            await Assert.That(folder).IsEqualTo(fixture.At("cmd"));
            await Assert.That(view.CurrentPath).IsEqualTo(Locations.ThisPC);
        }
        finally
        {
            FolderView.StartTyped = startTyped;
        }
    }

    // A folder named as a variable is written, which a script that never filled it in leaves
    // behind, is gone into as any other is, and typed, the variable is where it goes.
    [Test]
    public async Task AFolderNamedAsAVariableIsGoneInto()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("%TEMP%"));
        using var view = await Open(fixture.Root);

        view.Navigate(fixture.At("%TEMP%"));
        await Pump.Loaded(view);
        var named = view.CurrentPath;
        view.GoToTyped("%TEMP%");
        await Pump.Loaded(view);

        await Assert.That(named).IsEqualTo(fixture.At("%TEMP%"));
        await Assert.That(view.CurrentPath).IsEqualTo(Locations.Normalize(Environment.ExpandEnvironmentVariables("%TEMP%")));
    }

    // Run for real, a program is found as Run finds it, on the PATH, and starts in the folder
    // open.
    [Test]
    public async Task ATypedCommandRunsForReal()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));
        var startTyped = FolderView.StartTyped;
        FolderView.StartTyped = startInfo =>
        {
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            using var process = Process.Start(startInfo)!;
            process.WaitForExit();
        };
        try
        {
            view.GoToTyped("cmd /c cd > here.txt");

            var here = await File.ReadAllTextAsync(fixture.At("Docs", "here.txt"));
            await Assert.That(here.Trim()).IsEqualTo(fixture.At("Docs"));
        }
        finally
        {
            FolderView.StartTyped = startTyped;
        }
    }

    // The shell's names for folders go to where they are on disk.
    [Test]
    public async Task ShellNamesGoToTheirFolders()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.GoToTyped("shell:SendTo");
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
    }

    [Test]
    public async Task RevealSelectsTheFile()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces);

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items    1 item selected  293 KB,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes,
                    file    summary.gamma  0 bytes
                  ],
                  selected: [
                    report10.beta
                  ]
                }
                """);
    }

    [Test]
    public async Task PicksUpChangesOnDisk()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);
        File.Delete(fixture.At("summary.gamma"));
        await Pump.Until(() => view.Entries.Any(_ => _.Name == "arrived.aleph") && view.Entries.All(_ => _.Name != "summary.gamma"));

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects,
                  status: 8 items,
                  filter: ,
                  rows: [
                    folder  Archive,
                    folder  Docs,
                    folder  Source,
                    file    arrived.aleph  3,000 bytes,
                    file    notes.aleph  12 bytes,
                    file    Report1.beta  1,500,000 bytes,
                    file    report2.beta  2,048 bytes,
                    file    report10.beta  300,000 bytes
                  ],
                  selected: []
                }
                """);
    }

    // As in Explorer: the folder deleted from under the view, from Lode or from anywhere else,
    // takes it up to the folder it was in.
    [Test]
    public async Task ADeletedFolderIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));

        Directory.Delete(fixture.At("Source"), true);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("Docs");
        await Assert.That(Names(view)).DoesNotContain("Source");
        await Assert.That(view.StatusText).DoesNotContain("find");
    }

    // Deleted and made again at once, as a build emptying its output folder does, the folder
    // stays open, and what changes in it afterwards still shows.
    [Test]
    public async Task AFolderDeletedAndMadeAgainIsStillWatched()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Delete(fixture.At("Docs"), true);
        Directory.CreateDirectory(fixture.At("Docs"));
        await Pump.Until(() => !Names(view).Contains("guide.aleph"));
        await File.WriteAllBytesAsync(fixture.At("Docs", "arrived.aleph"), new byte[3]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
    }

    // Sent to the Recycle Bin, a folder is moved there, and the view's watch on it goes with it.
    // It is seen to go from the folder it was in.
    [Test]
    public async Task AFolderMovedAwayIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Move(fixture.At("Docs"), fixture.At("Archive", "Docs"));
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Docs");
    }

    // As in Explorer: the folder renamed by another program, the view follows it to its new
    // name, seen from the folder it is in.
    [Test]
    public async Task AFolderRenamedElsewhereIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Move(fixture.At("Docs"), fixture.At("Manuals"));
        await Pump.Until(() => view.CurrentPath == fixture.At("Manuals"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // Hidden by another program, attrib +h say, a file leaves the list while hidden items aren't
    // shown, and unhidden, it comes back, as in Explorer: a change to a file's attributes changes
    // neither its name, its size nor when it was written, which was all the watch heard of.
    [Test]
    public async Task AFileHiddenElsewhereLeavesTheList()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var notes = fixture.At("notes.aleph");

        File.SetAttributes(notes, File.GetAttributes(notes) | FileAttributes.Hidden);
        await Pump.Until(() => !Names(view).Contains("notes.aleph"));
        await Pump.Loaded(view);
        var hidden = Names(view);
        File.SetAttributes(notes, File.GetAttributes(notes) & ~FileAttributes.Hidden);
        await Pump.Until(() => Names(view).Contains("notes.aleph"));
        await Pump.Loaded(view);

        await Assert.That(hidden).DoesNotContain("notes.aleph");
        await Assert.That(Names(view)).Contains("notes.aleph");
    }

    // Renamed by Lode, from the tree say, the view follows it, with what was selected still
    // selected, and Back still goes where it went before.
    [Test]
    public async Task AFolderLodeRenamesIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Reveal(fixture.At("Docs", "guide.aleph"));
        await Pump.Loaded(view);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.Until(() => view.CurrentPath == fixture.At("Documents"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects/Documents,
                  status: 1 item    1 item selected  900 bytes,
                  filter: ,
                  rows: [
                    file    guide.aleph  900 bytes
                  ],
                  selected: [
                    guide.aleph
                  ]
                }
                """);

        view.GoBack();
        await Pump.Loaded(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
    }

    // A change of case alone is followed too, so the tab is named as the folder now is.
    [Test]
    public async Task AFolderRenamedToAnotherCaseIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "DOCS");
        await Pump.Until(() => view.CurrentPath == fixture.At("DOCS"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // Moved by Lode, a folder above the one showing takes the view with it.
    [Test]
    public async Task AFolderLodeMovesIsFollowedFromInsideIt()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Source")], fixture.At("Archive"), move: true);
        await Pump.Until(() => view.CurrentPath == fixture.At("Archive", "Source", "Deep"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("leaf.aleph");
    }

    // Somewhere in the history that moved is where it went, so Back goes there rather than to
    // somewhere that is no longer there.
    [Test]
    public async Task BackGoesWhereAFolderInTheHistoryWent()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));
        view.GoUp();
        await Pump.Loaded(view);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.For(TimeSpan.FromMilliseconds(300));
        view.GoBack();
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Documents"));
    }

    // Past the folders above it that have gone too, to the nearest still there.
    [Test]
    public async Task AFolderGoneWithTheOneAboveItIsLeftForTheNearestStillThere()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        Directory.Delete(fixture.At("Source"), true);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Source");
    }

    // A tab nobody could see when its folder went reads it when it shows, and finding it gone,
    // goes up instead.
    [Test]
    public async Task AFolderThatWentOutOfSightIsLeftWhenTheViewShows()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Showing = false;
        view.Navigate(fixture.At("Docs"));

        Directory.Delete(fixture.At("Docs"), true);
        view.Showing = true;
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Docs");
    }

    [Test]
    public async Task AFolderStillThereIsNotLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        await view.LeaveIfGone();

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
    }

    // Whether somewhere on the network is there is asked off the UI thread, since the answer
    // can be a long time coming. The view stays where it is, saying what it is looking for,
    // until it has heard.
    [Test]
    public async Task SomewhereOnTheNetworkIsAskedAfterOffTheUiThread()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.IsRemote = _ => true;

        view.Navigate(fixture.At("Source"));
        var whileAsking = (view.CurrentPath, view.StatusText);
        await Pump.Loaded(view);

        await Assert.That(whileAsking.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(whileAsking.StatusText).IsEqualTo($"Looking for {fixture.At("Source")}...");
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(Names(view)).Contains("main.aleph");
    }

    // Going somewhere else while somewhere on the network is still being asked after leaves
    // the view where it went, whatever is then heard.
    [Test]
    public async Task GoingElsewhereOvertakesSomewhereBeingAskedAfter()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.IsRemote = _ => Locations.Same(_, fixture.At("Source"));

        view.Navigate(fixture.At("Source"));
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // A tab started somewhere on the network, as one restored when Lode starts is, has its
    // name at once, taken on trust. It asks whether the folder is there when it shows.
    [Test]
    public async Task ATabOnTheNetworkIsTakenOnTrustUntilItShows()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("Source"));
        var atOnce = (view.CurrentPath, Names(view).Count);
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(atOnce).IsEqualTo((fixture.At("Source"), 0));
        await Assert.That(Names(view)).Contains("main.aleph");
    }

    // Started at a file, which means "show me this file", it shows the file's folder with the
    // file selected, once it has found that a file is what it is.
    [Test]
    public async Task ATabStartedAtAFileOnTheNetworkShowsItsFolder()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("report10.beta"));
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
        await Assert.That(view.CanGoBack).IsFalse();
    }

    // Not yet shown, it finds out behind the window that it was started at a file, so its tab
    // is named for the file's folder before it shows, and reads nothing until it does.
    [Test]
    public async Task ATabStartedAtAFileOnTheNetworkIsInItsFolderBeforeItShows()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("report10.beta"));
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        var hidden = Names(view).Count;
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(hidden).IsEqualTo(0);
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
        await Assert.That(view.CanGoBack).IsFalse();
    }

    // Started somewhere that turns out not to be there, a share that is offline say, the tab
    // stays, and says so, rather than Lode waiting on it before it opens.
    [Test]
    public async Task ATabOnTheNetworkThatIsNotThereSaysSo()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("Gone"));
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Gone"));
        await Assert.That(view.StatusText).IsEqualTo($"Lode can't find '{fixture.At("Gone")}'.");
        await Assert.That(Names(view)).IsEmpty();
    }

    // Starting a watch on the network waits on it, there and back several times, and for as long
    // as the network takes to give up on a server that has stopped answering. A folder there has
    // its watch started off the UI thread, and kept once it has.
    [Test]
    public async Task AFolderOnTheNetworkIsWatchedFromOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var uiThread = Environment.CurrentManagedThreadId;
        var source = fixture.At("Source");
        view.IsRemote = _ => Locations.Same(_, source);
        using var release = new ManualResetEventSlim();
        var startedOn = -1;
        var remote = new RecordedWatch();
        var start = view.StartWatch;
        view.StartWatch = (path, changed) =>
        {
            if (!Locations.Same(path, source))
            {
                return start(path, changed);
            }

            Volatile.Write(ref startedOn, Environment.CurrentManagedThreadId);
            release.Wait(TimeSpan.FromSeconds(5));
            return remote;
        };
        try
        {
            view.Navigate(source);
            await Pump.Until(() => Volatile.Read(ref startedOn) != -1);
            var whileStarting = view.Watcher;
            release.Set();
            await Pump.Loaded(view);
            await Pump.Until(() => view.Watcher == remote);

            await Assert.That(startedOn).IsNotEqualTo(uiThread);
            await Assert.That(whileStarting).IsNull();
            await Assert.That(view.CurrentPath).IsEqualTo(source);
            await Assert.That(Names(view)).Contains("main.aleph");
        }
        finally
        {
            release.Set();
        }
    }

    // Gone somewhere else before the watch on the network has started, the view lets go of it
    // once it has, off the UI thread, rather than keeping a watch on where it no longer is.
    [Test]
    public async Task AWatchOnTheNetworkStartedAfterGoingElsewhereIsLetGo()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var uiThread = Environment.CurrentManagedThreadId;
        var source = fixture.At("Source");
        view.IsRemote = _ => Locations.Same(_, source);
        using var release = new ManualResetEventSlim();
        var starting = false;
        var remote = new RecordedWatch();
        var start = view.StartWatch;
        view.StartWatch = (path, changed) =>
        {
            if (!Locations.Same(path, source))
            {
                return start(path, changed);
            }

            Volatile.Write(ref starting, true);
            release.Wait(TimeSpan.FromSeconds(5));
            return remote;
        };
        try
        {
            view.Navigate(source);
            await Pump.Until(() => Volatile.Read(ref starting));
            view.Navigate(fixture.At("Docs"));
            await Pump.Loaded(view);
            release.Set();
            await Pump.Until(() => remote.DisposedOn != null);
            await Pump.For(TimeSpan.FromMilliseconds(100));

            await Assert.That(remote.DisposedOn).IsNotEqualTo(uiThread);
            await Assert.That(view.Watcher).IsNotNull();
            await Assert.That(view.Watcher).IsNotEqualTo(remote);
            await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
        }
        finally
        {
            release.Set();
        }
    }

    // Letting go of a watch on the network waits on it too, so it is done off the UI thread:
    // going somewhere else, and closing the view.
    [Test]
    public async Task AWatchOnTheNetworkIsLetGoOfOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        var view = await Open(fixture.Root);
        var uiThread = Environment.CurrentManagedThreadId;
        var source = fixture.At("Source");
        view.IsRemote = _ => Locations.Same(_, source);
        var made = new List<RecordedWatch>();
        var start = view.StartWatch;
        view.StartWatch = (path, changed) =>
        {
            if (!Locations.Same(path, source))
            {
                return start(path, changed);
            }

            var watch = new RecordedWatch();
            lock (made)
            {
                made.Add(watch);
            }

            return watch;
        };
        RecordedWatch Made(int index)
        {
            lock (made)
            {
                return made[index];
            }
        }

        int MadeCount()
        {
            lock (made)
            {
                return made.Count;
            }
        }

        try
        {
            view.Navigate(source);
            await Pump.Loaded(view);
            await Pump.Until(() => MadeCount() == 1 && view.Watcher == Made(0));
            view.Navigate(fixture.At("Docs"));
            await Pump.Until(() => Made(0).DisposedOn != null);
            view.Navigate(source);
            await Pump.Loaded(view);
            await Pump.Until(() => MadeCount() == 2 && view.Watcher == Made(1));
            view.Dispose();
            await Pump.Until(() => Made(1).DisposedOn != null);

            await Assert.That(Made(0).DisposedOn).IsNotEqualTo(uiThread);
            await Assert.That(Made(1).DisposedOn).IsNotEqualTo(uiThread);
        }
        finally
        {
            view.Dispose();
        }
    }

    // A folder on this machine is watched as it always was, at once, and let go of at once.
    [Test]
    public async Task AFolderHereIsWatchedAtOnce()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var uiThread = Environment.CurrentManagedThreadId;
        var startedOn = -1;
        RecordedWatch? docs = null;
        var start = view.StartWatch;
        view.StartWatch = (path, changed) =>
        {
            startedOn = Environment.CurrentManagedThreadId;
            var watch = new RecordedWatch(start(path, changed));
            if (Locations.Same(path, fixture.At("Docs")))
            {
                docs = watch;
            }

            return watch;
        };

        view.Navigate(fixture.At("Docs"));
        var atOnce = view.Watcher;
        var startedOnAtOnce = startedOn;
        await Pump.Loaded(view);
        await File.WriteAllBytesAsync(fixture.At("Docs", "arrived.aleph"), new byte[10]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));
        view.Navigate(fixture.At("Source"));
        var disposedOnAtOnce = docs?.DisposedOn;

        await Assert.That(atOnce).IsNotNull();
        await Assert.That(atOnce).IsEqualTo(docs);
        await Assert.That(startedOnAtOnce).IsEqualTo(uiThread);
        await Assert.That(disposedOnAtOnce).IsEqualTo(uiThread);
    }

    // A watch that notes which thread let go of it, standing in for one on the network, or
    // around a real one, which it passes everything on to.
    sealed class RecordedWatch(IFolderWatch? inner = null) : IFolderWatch
    {
        int disposedOn = -1;

        public int? DisposedOn
        {
            get
            {
                var thread = Volatile.Read(ref disposedOn);
                return thread < 0 ? null : thread;
            }
        }

        public void Revive() =>
            inner?.Revive();

        public void Dispose()
        {
            Volatile.Write(ref disposedOn, Environment.CurrentManagedThreadId);
            inner?.Dispose();
        }
    }

    // A folder slow to read is left a while after each read before a change has it read
    // again, rather than being read over and over while something in it keeps changing.
    [Test]
    public async Task ASlowFolderIsNotReadAgainStraightAway()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        // As if the read just finished had taken this long, which leaves it ten times as long.
        view.ReadTook(TimeSpan.FromMilliseconds(300));
        var wait = FolderView.WaitAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Well past when a folder quick to read would have been read again.
        await Pump.For(TimeSpan.FromSeconds(1.5));
        var readEarly = Names(view).Contains("arrived.aleph");
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(readEarly).IsFalse();
        await Assert.That(watch.Elapsed).IsGreaterThanOrEqualTo(wait - TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task HowLongAFolderIsLeftAfterARead()
    {
        // Quick to read: no longer than changes are already held back.
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromMilliseconds(20))).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromMilliseconds(250))).IsEqualTo(TimeSpan.FromSeconds(2.5));

        // However slow, still read again in the end.
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromSeconds(30))).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    // A search hands over what it found a quarter of a second at a time, until showing it, all
    // that has been found so far merged in again, comes to take long: then it holds what it
    // finds ten times as long as that took, so the window is not kept busy with it.
    [Test]
    public async Task ASearchHoldsWhatItFindsLongerAsShowingItTakesLonger()
    {
        var pace = new FolderView.SearchPace();
        var first = pace.Hold;
        pace.Shown(TimeSpan.FromMilliseconds(5));
        var quick = pace.Hold;
        pace.Shown(TimeSpan.FromMilliseconds(200));
        var slow = pace.Hold;
        pace.Shown(TimeSpan.FromSeconds(5));
        var slowest = pace.Hold;

        await Assert.That(first).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(quick).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(slow).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(slowest).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    // A view nobody can see, such as a tab in the background, leaves changes on disk unread
    // until it shows again.
    [Test]
    public async Task LeavesChangesUnreadWhileNotShowing()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Showing = false;

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Several times as long as a change takes to be read by a view that is showing.
        await Pump.For(TimeSpan.FromSeconds(2));
        var readWhileHidden = Names(view).Contains("arrived.aleph");

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(readWhileHidden).IsFalse();
        await Assert.That(Names(view)).Contains("arrived.aleph");
    }

    // A folder opened in a view nobody can see, as a tab opened in the background is, is read
    // when the view shows, and the item it was opened for selected then.
    [Test]
    public async Task FolderOpenedWhileNotShowingIsReadWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
        {
            Showing = false
        };

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);
        var path = view.CurrentPath;
        var readWhileHidden = Names(view);

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(path).IsEqualTo(fixture.Root);
        await Assert.That(readWhileHidden).IsEmpty();
        await Assert.That(Names(view)).Contains("report10.beta");
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
    }

    static List<string> Names(FolderView view) =>
        view.Entries.Select(_ => _.Name).ToList();

    // The list holds no rows: it asks for each as it draws it. However many items a folder has,
    // rows are only made for those.
    [Test]
    public async Task MakesOnlyTheRowsItDraws()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 600);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new(Point.Empty, form.ClientSize));

        await Assert.That(view.Entries.Count).IsEqualTo(600);
        await Assert.That(view.RowsMade).IsGreaterThan(0);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // Size column to fit goes by every row in the folder, though the list only ever asks for,
    // and so only measures, those on screen.
    [Test]
    public async Task SizeColumnToFitGoesByTheWholeFolder()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);

        // Sorts last, far below the rows on screen.
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var menu = view.HeaderMenu(clickedColumn: 0);
        menu.Items[0].PerformClick();

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);

        // The text, its icon and the margins around them, and no more.
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // A double-click on the divider at the right of a column's header sizes it to fit too, and
    // by the whole folder too.
    [Test]
    public async Task DoubleClickingAHeaderDividerFitsByTheWholeFolder()
    {
        const int notifyMessage = 0x004E;
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);
        var list = view.Controls.OfType<ListView>().Single();
        var header = NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);

        // NMHEADER, as the header sends it: itself, an id, the code, then the column.
        var notification = Marshal.AllocHGlobal(6 * IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(notification, header);
            Marshal.WriteIntPtr(notification, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(notification, 2 * IntPtr.Size, NativeMethods.HeaderDividerDoubleClick);
            Marshal.WriteInt32(notification, 3 * IntPtr.Size, 0);
            NativeMethods.SendMessage(list.Handle, notifyMessage, IntPtr.Zero, notification);
        }
        finally
        {
            Marshal.FreeHGlobal(notification);
        }

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
    }

    // A column sized by dragging its divider, or sized to fit, is that wide in every view from
    // then on, and the next time Lode starts, kept at 100% scale. Hidden and shown again, it is as
    // wide as it was. A width set otherwise, as Windows might set one, is not kept.
    [Test]
    public async Task ColumnWidthsAreKeptForEveryView()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var form = Host();
        var view = await OpenIn(form, fixture.Root, settings);
        var list = view.Controls.OfType<ListView>().Single();
        var sized = new List<FolderView>();
        view.ColumnWidthsChanged += sized.Add;

        // Name, Date modified, Type and Size: Folder only a search has.
        list.Columns[3].Width = 333;
        Header.LetGoOfDivider(list, 1, view.LogicalToDeviceUnits(200));
        using (var menu = view.HeaderMenu(clickedColumn: 0))
        {
            menu.Items[0].PerformClick();
        }

        var nameWidth = (int)Math.Round(view.ColumnWidth(0) * 96.0 / view.DeviceDpi);
        using var other = await Open(fixture.Root, settings);
        var opened = (other.ColumnWidth(0), other.ColumnWidth(1));
        other.SetColumnShown(dateColumn, false);
        other.SetColumnShown(dateColumn, true);

        await Assert.That(settings.ColumnWidths.OrderBy(_ => _.Key).Select(_ => (_.Key, _.Value)))
            .IsEquivalentTo([("Date", 200), ("Name", nameWidth)], CollectionOrdering.Matching);
        await Assert.That(sized).IsEquivalentTo([view, view], CollectionOrdering.Matching);
        await Assert.That(opened).IsEqualTo((other.LogicalToDeviceUnits(nameWidth), other.LogicalToDeviceUnits(200)));
        await Assert.That(other.ColumnWidth(1)).IsEqualTo(other.LogicalToDeviceUnits(200));
    }

    // Each folder is sorted as it was last, in any view, as Explorer keeps each folder's: gone
    // away from and back to, or opened in another view. Other folders are by Name, A to Z. Sorted
    // back to that, a folder is noted no more.
    [Test]
    public async Task EachFolderKeepsItsSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.Root, settings);
        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);

        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        var elsewhere = view.Sort;
        view.GoBack();
        await Pump.Loaded(view);
        var back = view.Sort;
        using var other = await Open(fixture.Root, settings);
        var opened = other.Sort;
        var noted = settings.FolderSorts.Select(_ => (_.Column, _.Ascending)).ToList();

        other.SortBy(FolderView.nameColumn);

        await Assert.That(elsewhere).IsEqualTo((FolderView.nameColumn, true));
        await Assert.That(back).IsEqualTo((sizeColumn, false));
        await Assert.That(opened).IsEqualTo((sizeColumn, false));
        await Assert.That(noted).IsEquivalentTo([("Size", false)]);
        await Assert.That(settings.FolderSorts).IsEmpty();
    }

    // A search starts sorted by Folder, and goes back, once it ends, to how its folder is sorted,
    // which it leaves as it was: Folder is the search's alone.
    [Test]
    public async Task EndingASearchSortedByFolderGoesBackToTheFoldersSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.Root, settings);
        view.SortBy(dateColumn);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);
        var searching = view.Sort;
        view.Filter = "";
        await Pump.Loaded(view);

        await Assert.That(searching).IsEqualTo((folderColumn, true));
        await Assert.That(view.Sort).IsEqualTo((dateColumn, false));
        await Assert.That(settings.FolderSorts.Select(_ => (_.Column, _.Ascending))).IsEquivalentTo([("Date", false)]);
    }

    // A folder renamed while a view shows it, the view follows, sorted as it was, and the folder
    // is sorted so under its new name.
    [Test]
    public async Task ARenamedFolderKeepsItsSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.At("Docs"), settings);
        view.SortBy(typeColumn);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.Until(() => view.CurrentPath == fixture.At("Documents"));
        await Pump.Loaded(view);

        await Assert.That(view.Sort).IsEqualTo((typeColumn, true));
        await Assert.That(settings.SortOf(fixture.At("Documents"))?.Column).IsEqualTo("Type");
    }

    // Sort by offers every column the folder has, hidden ones too, ticking the one sorted by and
    // which way. Sorted by a hidden one, newest first for Date modified as its header starts, it
    // shows while the list is, still unticked in the header's menu, and goes once sorted by
    // another. Opened again, the folder is sorted by it, and it shows.
    [Test]
    public async Task SortByAHiddenColumn()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            ShowHidden = false,
            HiddenColumns = ["Date"]
        };
        using var view = await Open(fixture.Root, settings);
        var before = Ticked(view);
        var columns = view.ColumnTitles.ToList();

        Pick(view, "Date modified");
        var sorted = (view.Sort, Ticked(view), columns: view.ColumnTitles.ToList(), first: view.Entries[0].Name);
        bool headerTicked;
        using (var menu = view.HeaderMenu(clickedColumn: 0))
        {
            headerTicked = menu.Items.OfType<ToolStripMenuItem>().Single(_ => _.Text == "Date modified").Checked;
        }

        Pick(view, "Ascending");
        var ascending = (view.Sort, Ticked(view));
        using var other = await Open(fixture.Root, settings);
        var opened = (other.Sort, columns: other.ColumnTitles.ToList());

        Pick(view, "Type");
        Pick(view, "Type");

        await Assert.That(before).IsEqualTo("Name, Ascending");
        await Assert.That(columns).IsEquivalentTo(["Name", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(sorted.Sort).IsEqualTo((dateColumn, false));
        await Assert.That(sorted.Item2).IsEqualTo("Date modified, Descending");
        await Assert.That(sorted.columns).IsEquivalentTo(["Name", "Date modified", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(sorted.first).IsEqualTo("Source");
        await Assert.That(headerTicked).IsFalse();
        await Assert.That(ascending).IsEqualTo(((dateColumn, true), "Date modified, Ascending"));
        await Assert.That(opened.Sort).IsEqualTo((dateColumn, true));
        await Assert.That(opened.columns).Contains("Date modified");
        await Assert.That(view.Sort).IsEqualTo((typeColumn, true));
        await Assert.That(view.ColumnTitles).IsEquivalentTo(["Name", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(settings.HiddenColumns).IsEquivalentTo(["Date"]);
    }

    // Runs Sort by's entry of that name.
    static void Pick(FolderView view, string text) =>
        view.SortCommands().Single(_ => _?.Text == text)!.Action();

    // Sort by's ticked entries: the column, then which way.
    static string Ticked(FolderView view) =>
        string.Join(", ", view.SortCommands().Where(_ => _ is { Checked: true }).Select(_ => _!.Text));

    // Typing the start of a name moves to the next item that has it, going round to the top.
    [Test]
    public async Task TypingTheStartOfANameFindsTheNextWithIt()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var names = Names(view);

        string? Find(string text, int start)
        {
            var index = view.FindRow(text, start);
            if (index < 0)
            {
                return null;
            }

            return names[index];
        }

        var first = view.FindRow("rep", 0);

        await Assert.That(Find("rep", 0)).IsEqualTo("Report1.beta");
        await Assert.That(Find("REP", first + 1)).IsEqualTo("report2.beta");
        await Assert.That(Find("a", names.Count - 1)).IsEqualTo("Archive");
        await Assert.That(Find("zz", 0)).IsNull();
    }

    // Typed at the list itself, which holds no rows to look through and has the view do it.
    [Test]
    public async Task TypingAtTheListSelectsTheItemFound()
    {
        const int characterMessage = 0x0102;
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.Controls.OfType<ListView>().Single();

        foreach (var character in "rep")
        {
            NativeMethods.SendMessage(list.Handle, characterMessage, character, IntPtr.Zero);
        }

        await Assert.That(Selected(view)).IsEquivalentTo(["Report1.beta"]);
    }

    // F2 selects the name but not its extension, so typing keeps the file's type, and a folder's
    // whole name. A name that is all extension, a dotfile's such as .gitignore, is selected
    // whole, as Explorer selects it: nothing was, so typing foo gave foo.gitignore. A dotfile
    // with an extension of its own has what comes before it selected, .env of .env.local.
    [Test]
    public async Task RenamingSelectsTheNameButNotTheExtension()
    {
        const int editGetSelection = 0x00B0;
        const int keyDown = 0x0100;
        const int virtualKeyEscape = 0x1B;
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At(".gitignore"), "");
        await File.WriteAllTextAsync(fixture.At(".env.local"), "");
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        IntPtr Edit() =>
            NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetEditControl, IntPtr.Zero, IntPtr.Zero);

        // What F2 on the item selects of its name, the edit then cancelled.
        async Task<string> Selects(string name)
        {
            var index = Names(view).IndexOf(name);
            for (var attempt = 0; ; attempt++)
            {
                list.SelectedIndices.Clear();
                list.SelectedIndices.Add(index);
                list.FocusedItem = list.Items[index];
                view.HandleKey(Keys.F2);

                // The part to select is selected once the edit has begun, by a callback posted
                // as it began, which the pump runs as it ends.
                await Pump.Until(() => Edit() != IntPtr.Zero);
                var edit = Edit();
                var selection = (int) NativeMethods.SendMessage(edit, editGetSelection, IntPtr.Zero, IntPtr.Zero);

                // Another window taking the focus, as another test run's may, ends the edit,
                // perhaps before its selection was read: then it is begun again.
                var stillEditing = edit != IntPtr.Zero &&
                                   Edit() == edit;
                NativeMethods.SendMessage(edit, keyDown, virtualKeyEscape, IntPtr.Zero);
                await Pump.Until(() => Edit() == IntPtr.Zero);
                if (stillEditing ||
                    attempt == 10)
                {
                    return name[(selection & 0xFFFF)..(selection >> 16)];
                }
            }
        }

        await Assert.That(await Selects("notes.aleph")).IsEqualTo("notes");
        await Assert.That(await Selects("Docs")).IsEqualTo("Docs");
        await Assert.That(await Selects(".gitignore")).IsEqualTo(".gitignore");
        await Assert.That(await Selects(".env.local")).IsEqualTo(".env");
    }

    // The list asks for a renamed row again the moment the edit ends. It has the new name by
    // then, not only once the folder has been read again.
    [Test]
    public async Task RenamingShowsTheNewNameAtOnce()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var index = Names(view).IndexOf("notes.aleph");

        view.OnAfterLabelEdit(null, new(index, "renamed.aleph"));
        var atOnce = view.Entries[index];

        await Assert.That(atOnce.Name).IsEqualTo("renamed.aleph");
        await Assert.That(atOnce.FullPath).IsEqualTo(fixture.At("renamed.aleph"));
        await Assert.That(File.Exists(fixture.At("renamed.aleph"))).IsTrue();
    }

    // The list says only which row an edit was on, and the rows can be shown again under it: a
    // read of the folder landing, here with a file made that sorts above. The item renamed is
    // the one the edit was begun on, not the one now in its row.
    [Test]
    public async Task RenamingGoesByTheItemNotItsRow()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var index = Names(view).IndexOf("notes.aleph");

        view.OnBeforeLabelEdit(null, new(index));
        await File.WriteAllTextAsync(fixture.At("above.aleph"), "");
        view.Reload();
        await Pump.Loaded(view);
        var inItsRow = view.Entries[index].Name;
        view.OnAfterLabelEdit(null, new(index, "renamed.aleph"));

        await Assert.That(inItsRow).IsNotEqualTo("notes.aleph");
        await Assert.That(File.Exists(fixture.At("renamed.aleph"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("notes.aleph"))).IsFalse();
        await Assert.That(File.Exists(fixture.At(inItsRow))).IsTrue();
    }

    // With several selected, the name typed for one is given to them all, numbered from it,
    // each keeping its own extension, as Explorer does. They are selected under their new
    // names.
    [Test]
    public async Task RenamingSeveralNumbersThem()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var names = Names(view);
        string[] chosen = ["notes.aleph", "report2.beta", "Docs"];
        foreach (var name in chosen)
        {
            view.List.SelectedIndices.Add(names.IndexOf(name));
        }

        var index = names.IndexOf("report2.beta");
        view.OnBeforeLabelEdit(null, new(index));
        view.OnAfterLabelEdit(null, new(index, "Plan.beta"));
        await Pump.Loaded(view);
        var renamed = Selected(view);

        await Assert.That(renamed).IsEquivalentTo(["Plan (1).beta", "Plan (2)", "Plan (3).aleph"]);
        await Assert.That(Directory.Exists(fixture.At("Plan (2)"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("notes.aleph"))).IsFalse();
    }

    // The list goes by where a row is, which moves when the folder changes. What was selected
    // stays selected.
    [Test]
    public async Task KeepsTheSelectionWhenTheFolderChanges()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces);
        view.Reveal(fixture.At("report2.beta"));
        await Pump.Loaded(view);

        // Sorts above it, which moves it down a row.
        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[10]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(Selected(view)).IsEquivalentTo(["report2.beta"]);
    }

    [Test]
    public async Task SortingKeepsEverythingSelected()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(view.Entries.Count - 1).Top + 2));
        marquee.End();
        var selectedBefore = Selected(view).Count;

        view.SortBy(sizeColumn);

        await Assert.That(selectedBefore).IsEqualTo(view.Entries.Count);
        await Assert.That(Selected(view).Count).IsEqualTo(view.Entries.Count);
    }

    // A folder of many files, each with nothing in it.
    static string Many(Fixture fixture, int count)
    {
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"file{index}.aleph"), []);
        }

        return folder;
    }

    // A drag over a folder in the list has it highlighted, as Explorer has it, as where the drop
    // would go. Over a file, the drop would go into the folder open, so nothing is.
    [Test]
    public async Task TheFolderADragWouldGoIntoIsHighlighted()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Archive", "dragged.txt"), "");
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        int Index(string name) =>
            view.Entries.ToList().FindIndex(_ => _.Name == name);
        DragEventArgs Over(string name)
        {
            var bounds = list.GetItemRect(Index(name));
            var screen = list.PointToScreen(new(bounds.Left + 20, bounds.Top + bounds.Height / 2));
            return new(new DataObject(DataFormats.FileDrop, new[] { fixture.At("Archive", "dragged.txt") }), 0, screen.X, screen.Y, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
        }

        view.OnDragOver(null, Over("Docs"));
        var overFolder = (list.DrawsDropTarget(Index("Docs")), list.DrawsDropTarget(Index("notes.aleph")));
        view.OnDragOver(null, Over("notes.aleph"));
        var overFile = (list.DrawsDropTarget(Index("Docs")), list.DrawsDropTarget(Index("notes.aleph")));

        await Assert.That(overFolder).IsEqualTo((true, false));
        await Assert.That(overFile).IsEqualTo((false, false));
    }

    // Beside a drag's picture is what a drop would do, in Explorer's words: move into the folder
    // under it, or, over a file, into the folder open.
    [Test]
    public async Task WhatADropWouldDoIsSaidBesideThePicture()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Archive", "dragged.txt"), "");
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        string Said(string name)
        {
            var bounds = list.GetItemRect(view.Entries.ToList().FindIndex(_ => _.Name == name));
            var screen = list.PointToScreen(new(bounds.Left + 20, bounds.Top + bounds.Height / 2));
            var args = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { fixture.At("Archive", "dragged.txt") }), 0, screen.X, screen.Y, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
            view.OnDragOver(null, args);
            return $"{args.DropImageType}: {args.Message?.Replace("%1", args.MessageReplacementToken)}";
        }

        await Assert.That(Said("Docs")).IsEqualTo("Move: Move to Docs");
        await Assert.That(Said("notes.aleph")).IsEqualTo("Move: Move to Projects");
    }

    // What is cut, by Lode or Explorer, is dimmed, as Explorer dims it: the list asks as it draws
    // each row, and is told so for those on the clipboard as cut, and not once they are pasted
    // or something else is copied.
    [Test]
    public async Task CutItemsAreDimmed()
    {
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        int Index(string name) =>
            view.Entries.ToList().FindIndex(_ => _.Name == name);
        byte[] Draw()
        {
            using var bitmap = new Bitmap(list.Width, list.Height);
            list.DrawToBitmap(bitmap, new(Point.Empty, list.Size));
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        try
        {
            var plain = Draw();
            CutItems.Set([fixture.At("notes.aleph"), fixture.At("Docs")]);
            var askedBefore = list.CutAsked;
            var dimmed = Draw();
            var asked = list.CutAsked > askedBefore;
            var cut = (list.DrawsCut(Index("notes.aleph")), list.DrawsCut(Index("Docs")), list.DrawsCut(Index("summary.gamma")));
            CutItems.Set([]);
            var pasted = list.DrawsCut(Index("notes.aleph"));

            await Assert.That(asked).IsTrue();
            await Assert.That(dimmed.SequenceEqual(plain)).IsFalse();
            await Assert.That(cut).IsEqualTo((true, true, false));
            await Assert.That(pasted).IsFalse();
        }
        finally
        {
            CutItems.Set([]);
        }
    }

    // What is pasted is selected once it is there, as Explorer has it: under the name it was
    // given, a copy into its own folder becoming "- Copy", and nothing else that was selected.
    [Test]
    public async Task WhatIsPastedIsSelected()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));
        await File.WriteAllTextAsync(fixture.At("Archive", "already.aleph"), "");
        view.Reveal(fixture.At("Archive", "already.aleph"));
        await Pump.Loaded(view);

        // The shell copies on a thread of its own, slowly on a busy machine, and the list has
        // already loaded: waiting on that, the second paste could start before the first had
        // made the file it copies, and the shell would stop to say it can't find it. So each
        // waits for what was pasted to be selected, as it is once it is there.
        view.Paste(new([fixture.At("notes.aleph"), fixture.At("Docs")], false));
        await Pump.Until(() => Selected(view).Order().SequenceEqual(["Docs", "notes.aleph"]));
        var intoArchive = Selected(view);

        view.Paste(new([fixture.At("Archive", "notes.aleph")], false));
        await Pump.Until(() => Selected(view).SequenceEqual(["notes - Copy.aleph"]));

        await Assert.That(intoArchive).IsEquivalentTo(["Docs", "notes.aleph"]);
        await Assert.That(Selected(view)).IsEquivalentTo(["notes - Copy.aleph"]);
    }

    // Ctrl+Shift+C copies what is selected as paths, as in Windows 11's Explorer: quoted, one
    // a line. With nothing selected it copies nothing.
    [Test]
    public async Task CtrlShiftCCopiesAsPath()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var copied = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = copied.Add;
        try
        {
            var withNothing = view.HandleKey(Keys.Control | Keys.Shift | Keys.C);
            view.Reveal([fixture.At("notes.aleph"), fixture.At("Docs")]);
            await Pump.Loaded(view);
            var handled = view.HandleKey(Keys.Control | Keys.Shift | Keys.C);

            await Assert.That((withNothing, handled)).IsEqualTo((true, true));
            await Assert.That(copied).IsEquivalentTo([$"\"{fixture.At("Docs")}\"{Environment.NewLine}\"{fixture.At("notes.aleph")}\""]);
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
        }
    }

    // A window for a view, off screen. Only in a window does the list draw rows, and measure them.
    static Form Host() =>
        new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(900, 400)
        };

    static async Task<FolderView> OpenIn(Form form, string path, Settings? settings = null)
    {
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces)
        {
            Dock = DockStyle.Fill
        };
        form.Controls.Add(view);
        form.Show();
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    // Showing a window normally takes the focus from whatever the developer is doing.
    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [Test]
    public async Task CreateFolder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));

        view.CreateFolder();
        await Pump.Loaded(view);
        view.CreateFolder();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects/Archive,
                  status: 2 items    1 item selected,
                  filter: ,
                  rows: [
                    folder  New folder,
                    folder  New folder (2)
                  ],
                  selected: [
                    New folder (2)
                  ]
                }
                """);
    }

    static string MakeZip(Fixture fixture, string name, params string[] entries)
    {
        var zip = fixture.At(name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var created = archive.CreateEntry(entry);
            if (entry.EndsWith('/'))
            {
                continue;
            }

            using var stream = created.Open();
            stream.Write(new byte[entry.Length]);
        }

        return zip;
    }

    static readonly string[] bundle = ["readme.aleph", "Docs/guide.aleph", "Docs/Deep/", "tool.exe"];

    // A zip lists as a folder: what is at its top, its folders first, those it has no entries
    // of its own for included.
    [Test]
    public async Task ListsAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);

        await Assert.That(view.InArchive).IsTrue();
        await Verify(Describe(view, fixture))
            .Snapshot(
                """
                {
                  path: Projects/Bundle.zip,
                  status: 3 items,
                  filter: ,
                  rows: [
                    folder  Docs,
                    file    readme.aleph  12 bytes,
                    file    tool.exe  8 bytes
                  ],
                  selected: []
                }
                """);
    }

    // Opened, a zip is gone into, as in Explorer, and so is a folder in it. Up from inside comes
    // back out with what led there selected.
    [Test]
    public async Task GoingIntoAndUpOutOfAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(fixture.Root);
        view.Reveal(zip);
        await Pump.Loaded(view);

        OpenSelected(view);
        await Pump.Loaded(view);
        var atTop = view.CurrentPath;
        view.Reveal(Path.Combine(zip, "Docs"));
        await Pump.Loaded(view);
        OpenSelected(view);
        await Pump.Loaded(view);
        var inDocs = view.CurrentPath;
        var docsNames = Names(view);
        view.GoUp();
        await Pump.Loaded(view);
        var upToTop = Selected(view);
        view.GoUp();
        await Pump.Loaded(view);

        await Assert.That(atTop).IsEqualTo(zip);
        await Assert.That(inDocs).IsEqualTo(Path.Combine(zip, "Docs"));
        await Assert.That(docsNames).IsEquivalentTo(["Deep", "guide.aleph"], CollectionOrdering.Matching);
        await Assert.That(upToTop).IsEquivalentTo(["Docs"]);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["Bundle.zip"]);
    }

    static void OpenSelected(FolderView view) =>
        view.ItemCommands(view.SelectedEntries()).First(_ => _?.Id == "open")!.Action();

    // Ctrl+A and Enter would open everything in the folder, going into the first folder and
    // opening a tab for each of the rest, and starting every file. As Explorer does, opening more
    // than 15 at once asks first, and answered no, opens nothing.
    [Test]
    public async Task OpeningMoreThanFifteenAsksFirst()
    {
        using var fixture = Fixture.Create();
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < 16; index++)
        {
            Directory.CreateDirectory(Path.Combine(folder, $"Folder{index:D2}"));
        }

        using var view = await Open(folder);
        var tabs = new List<string>();
        view.NewTabRequested += tabs.Add;
        var asked = new List<string>();
        var answer = DialogResult.No;
        var ask = FolderView.AskOpenMany;
        FolderView.AskOpenMany = (_, question) =>
        {
            asked.Add(question);
            return answer;
        };
        // The first so many folders, selected and opened from the folder they are in: where the
        // view went, how many tabs were asked for, and what was asked first.
        async Task<(string Path, int Tabs, List<string> Asked)> OpenFirst(int count)
        {
            if (view.CurrentPath != folder)
            {
                view.Navigate(folder);
                await Pump.Loaded(view);
            }

            tabs.Clear();
            asked.Clear();
            view.List.SelectedIndices.Clear();
            for (var index = 0; index < count; index++)
            {
                view.List.SelectedIndices.Add(index);
            }

            OpenSelected(view);
            await Pump.Loaded(view);
            return (Path.GetFileName(view.CurrentPath), tabs.Count, asked.ToList());
        }

        try
        {
            var declined = await OpenFirst(16);
            answer = DialogResult.Yes;
            var accepted = await OpenFirst(16);
            var fifteen = await OpenFirst(15);

            await Assert.That(declined.Path).IsEqualTo("Many");
            await Assert.That(declined.Tabs).IsEqualTo(0);
            await Assert.That(declined.Asked).IsEquivalentTo(["Open all 16 items?"]);
            await Assert.That(accepted.Path).IsEqualTo("Folder00");
            await Assert.That(accepted.Tabs).IsEqualTo(15);
            await Assert.That(accepted.Asked).IsEquivalentTo(["Open all 16 items?"]);
            await Assert.That(fifteen.Path).IsEqualTo("Folder00");
            await Assert.That(fifteen.Tabs).IsEqualTo(14);
            await Assert.That(fifteen.Asked).IsEmpty();
        }
        finally
        {
            FolderView.AskOpenMany = ask;
        }
    }

    // Typed in the address bar, or reached from the breadcrumb, a folder in a zip opens, and a
    // file in one is shown selected.
    [Test]
    public async Task NavigatingToSomewhereInAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs", "Deep"));
        var deep = view.CurrentPath;
        var deepNames = Names(view);

        view.Navigate(Path.Combine(zip, "Docs", "guide.aleph"));
        await Pump.Loaded(view);

        await Assert.That(deep).IsEqualTo(Path.Combine(zip, "Docs", "Deep"));
        await Assert.That(deepNames).IsEmpty();
        await Assert.That(view.CurrentPath).IsEqualTo(Path.Combine(zip, "Docs"));
        await Assert.That(Selected(view)).IsEquivalentTo(["guide.aleph"]);
    }

    // Lode doesn't change a zip: what is in one can be opened, copied and its path copied, and
    // nothing can be made, pasted, dropped, cut, renamed or deleted there. Windows' own menu,
    // which would change it, isn't offered.
    [Test]
    public async Task AZipIsReadOnly()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);
        var deleted = new List<string>();
        view.DeleteItems = (_, paths, _) =>
        {
            deleted.AddRange(paths);
            return Task.FromResult(true);
        };
        view.Reveal([Path.Combine(zip, "Docs"), Path.Combine(zip, "readme.aleph")]);
        await Pump.Loaded(view);

        var itemCommands = view.ItemCommands(view.SelectedEntries()).Select(_ => _?.Id).ToList();
        var backgroundCommands = view.BackgroundCommands(canPaste: true).Select(_ => _?.Id).ToList();
        view.Delete(false);
        view.Paste(new([fixture.At("notes.aleph")], false));
        view.CreateFolder();
        await Pump.Loaded(view);

        await Assert.That(view.IsReadOnly).IsTrue();
        await Assert.That(view.CanShowShellMenu).IsFalse();
        await Assert.That(itemCommands).IsEquivalentTo(["open", null, "copy", "copyAsPath"], CollectionOrdering.Matching);
        await Assert.That(backgroundCommands).IsEquivalentTo(["sortBy", null, "undo", "redo", null, "extractAll", "copyAsPath"], CollectionOrdering.Matching);
        await Assert.That(deleted).IsEmpty();
        await Assert.That(Names(view)).IsEquivalentTo(["Docs", "readme.aleph", "tool.exe"], CollectionOrdering.Matching);
    }

    // A zip renamed by another program, the view inside it follows it to its new name.
    [Test]
    public async Task AZipRenamedElsewhereIsFollowed()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs"));

        File.Move(zip, fixture.At("Renamed.zip"));
        await Pump.Until(() => view.CurrentPath == fixture.At("Renamed.zip", "Docs"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // A zip deleted from under the view takes it to the folder the zip was in.
    [Test]
    public async Task ADeletedZipIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs"));

        File.Delete(zip);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Bundle.zip");
    }

    // A zip written to while the view is in it is read again.
    [Test]
    public async Task AZipWrittenToIsReadAgain()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);

        await using (var archive = await ZipFile.OpenAsync(zip, ZipArchiveMode.Update))
        {
            archive.CreateEntry("arrived.aleph");
        }

        await Pump.Until(() => Names(view).Contains("arrived.aleph"));
    }

    // A folder named as a zip is a folder.
    [Test]
    public async Task AFolderNamedAsAZipIsAFolder()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Folder.zip"));
        await File.WriteAllTextAsync(fixture.At("Folder.zip", "inside.aleph"), "");
        using var view = await Open(fixture.At("Folder.zip"));

        await Assert.That(view.InArchive).IsFalse();
        await Assert.That(Names(view)).IsEquivalentTo(["inside.aleph"]);
    }

    // A zip whose entries hold their own names, so what is extracted from it can be told apart.
    static string MakeZipOfNames(Fixture fixture, string name, params string[] entries)
    {
        var zip = fixture.At(name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var created = archive.CreateEntry(entry);
            if (entry.EndsWith('/'))
            {
                continue;
            }

            using var writer = new StreamWriter(created.Open());
            writer.Write($"this is {entry}");
        }

        return zip;
    }

    // What every file and folder beneath these is, and what each file holds, from where they are.
    static List<string> Contents(IEnumerable<string> paths)
    {
        var contents = new List<string>();
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path)!;
            if (File.Exists(path))
            {
                contents.Add($"{Path.GetFileName(path)}: {File.ReadAllText(path)}");
                continue;
            }

            contents.Add($"{Path.GetFileName(path)}/");
            foreach (var inside in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(parent, inside).Replace('\\', '/');
                contents.Add(Directory.Exists(inside) ? $"{relative}/" : $"{relative}: {File.ReadAllText(inside)}");
            }
        }

        contents.Sort(StringComparer.Ordinal);
        return contents;
    }

    // Dragged out of a zip, what is dragged is extracted off the UI thread, the drag starting
    // once it is, with every file and folder in it as it is in the zip, only to be copied.
    [Test]
    public async Task DraggingOutOfAZipExtractsOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZipOfNames(fixture, "Bundle.zip", "readme.aleph", "Docs/guide.aleph", "Docs/Deep/", "Docs/Deep/leaf.aleph", "Docs/Empty/", "tool.exe");
        using var view = await Open(zip);
        var uiThread = Environment.CurrentManagedThreadId;
        var extractedOn = -1;
        var extract = view.ExtractToTemp;
        view.ExtractToTemp = (paths, progress, cancel) =>
        {
            Volatile.Write(ref extractedOn, Environment.CurrentManagedThreadId);
            return extract(paths, progress, cancel);
        };
        view.StillDragging = _ => true;
        string[]? dragged = null;
        var effects = DragDropEffects.None;
        view.DragOut = (_, data, allowed) =>
        {
            data.TryGetData<string[]>(DataFormats.FileDrop, out dragged);
            effects = allowed;
            return DragDropEffects.Copy;
        };
        view.Reveal([Path.Combine(zip, "Docs"), Path.Combine(zip, "readme.aleph")]);
        await Pump.Loaded(view);

        view.OnItemDrag(null, new(MouseButtons.Left));
        var draggedAtOnce = dragged;
        await Pump.Until(() => view.DraggingOutOfArchive.IsCompleted);
        await view.DraggingOutOfArchive;

        try
        {
            await Assert.That(draggedAtOnce).IsNull();
            await Assert.That(extractedOn).IsNotEqualTo(uiThread);
            await Assert.That(effects).IsEqualTo(DragDropEffects.Copy);
            await Assert.That(dragged!.All(_ => _.StartsWith(Zip.TempRoot, StringComparison.OrdinalIgnoreCase))).IsTrue();
            await Assert.That(Contents(dragged!))
                .IsEquivalentTo(
                    [
                        "Docs/",
                        "Docs/Deep/",
                        "Docs/Deep/leaf.aleph: this is Docs/Deep/leaf.aleph",
                        "Docs/Empty/",
                        "Docs/guide.aleph: this is Docs/guide.aleph",
                        "readme.aleph: this is readme.aleph"
                    ],
                    CollectionOrdering.Matching);
        }
        finally
        {
            foreach (var path in dragged ?? [])
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }

    // A drag out of a zip given up while what it carries is still being extracted, the button
    // let go or Escape pressed, stops the extraction at once, leaves nothing of it in the temp
    // folder, and never begins.
    [Test]
    public async Task ADragOutOfAZipGivenUpStopsExtractingAndLeavesNothing()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var stream = archive.CreateEntry("big.aleph").Open();
            stream.Write(new byte[4_000_000]);
        }

        var target = Zip.TempTarget(new(zip, "big.aleph"));
        using var view = await Open(zip);
        using var underWay = new ManualResetEventSlim();
        var extract = view.ExtractToTemp;
        view.ExtractToTemp = (paths, progress, cancel) =>
            extract(paths, new HeldOnceUnderWay(progress, underWay, cancel.WaitHandle), cancel);
        view.StillDragging = _ => !underWay.IsSet;
        var dragged = false;
        view.DragOut = (_, _, _) =>
        {
            dragged = true;
            return DragDropEffects.Copy;
        };
        view.Reveal(Path.Combine(zip, "big.aleph"));
        await Pump.Loaded(view);

        view.OnItemDrag(null, new(MouseButtons.Left));
        await Pump.Until(() => underWay.IsSet);
        var givenUp = Stopwatch.StartNew();
        await Pump.Until(() => view.DraggingOutOfArchive.IsCompleted);
        givenUp.Stop();

        await Assert.That(dragged).IsFalse();
        await Assert.That(givenUp.Elapsed).IsLessThan(TimeSpan.FromSeconds(4));
        await Assert.That(Path.Exists(target)).IsFalse();
        await Assert.That(Directory.Exists(Path.GetDirectoryName(target))).IsFalse();
    }

    // While what a drag carries takes a while to extract, the status bar says how far it has got,
    // and says what it said before once the drag has begun.
    [Test]
    public async Task ADragOutOfAZipSaysHowFarItHasGot()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var stream = archive.CreateEntry("big.aleph").Open();
            stream.Write(new byte[4_000_000]);
        }

        using var view = await Open(zip);
        using var underWay = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var extract = view.ExtractToTemp;
        view.ExtractToTemp = (paths, progress, cancel) =>
            extract(paths, new HeldOnceUnderWay(progress, underWay, release.WaitHandle), cancel);
        view.StillDragging = _ => true;
        string[]? dragged = null;
        view.DragOut = (_, data, _) =>
        {
            data.TryGetData<string[]>(DataFormats.FileDrop, out dragged);
            return DragDropEffects.Copy;
        };
        view.Reveal(Path.Combine(zip, "big.aleph"));
        await Pump.Loaded(view);
        var before = view.StatusText;

        try
        {
            view.OnItemDrag(null, new(MouseButtons.Left));
            await Pump.Until(() => view.StatusText.StartsWith("Extracting what is dragged, ", StringComparison.Ordinal));
            var whileExtracting = view.StatusText;
            release.Set();
            await Pump.Until(() => view.DraggingOutOfArchive.IsCompleted);
            await Pump.Until(() => view.StatusText == before);

            await Assert.That(whileExtracting).Matches(@"^Extracting what is dragged, \d+% done\. Let go, or press Esc, to stop\.$");
            await Assert.That(dragged).IsNotNull();
            await Assert.That(new FileInfo(dragged![0]).Length).IsEqualTo(4_000_000);
        }
        finally
        {
            release.Set();
            foreach (var path in dragged ?? [])
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }

    // Says how far an extraction has got, and once it has started holds it there until told to
    // go on, so it is part way when the test looks, however quick the machine.
    sealed class HeldOnceUnderWay(IProgress<int>? progress, ManualResetEventSlim underWay, WaitHandle hold) : IProgress<int>
    {
        public void Report(int value)
        {
            progress?.Report(value);
            if (value is > 0 and < 100 &&
                !underWay.IsSet)
            {
                underWay.Set();
                hold.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
    }

    // Compress to ZIP file makes the zip beside what is selected and selects it, ready to rename,
    // as Explorer has it.
    [Test]
    public async Task CompressSelectsTheNewZip()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Reveal([fixture.At("Docs"), fixture.At("notes.aleph")]);
        await Pump.Loaded(view);

        view.Compress(view.SelectedEntries());
        await Pump.Loaded(view);

        await Assert.That(Selected(view)).IsEquivalentTo(["Docs.zip"]);
        ActionLog.Reset();
    }

    // Extract All selects the folder it made.
    [Test]
    public async Task ExtractAllSelectsTheFolder()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Bundle.zip");
        await using (var archive = await ZipFile.OpenAsync(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("inside.txt");
        }

        using var view = await Open(fixture.Root);

        view.Extract([zip], false);
        await Pump.Loaded(view);

        await Assert.That(Selected(view)).IsEquivalentTo(["Bundle"]);
        ActionLog.Reset();
    }

    // A drag from the margin left of the list selects the rows it spans.
    [Test]
    public async Task MarqueeFromTheMargin()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;

        // Straight down the margin, from the second row to the fourth.
        marquee.Begin(new(-5, view.RowBounds(1).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(3).Top + 2));
        marquee.End();
        var dragged = Selected(view);

        // With Ctrl, the rows covered flip: the fourth is dropped, the fifth added.
        marquee.Begin(new(-5, view.RowBounds(3).Top + 2), add: true);
        marquee.Update(new(-5, view.RowBounds(4).Top + 2));
        marquee.End();
        var toggled = Selected(view);

        // A click in the margin, without Ctrl, clears the selection.
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.End();

        await Verify(
            new
            {
                dragged,
                toggled,
                clicked = Selected(view)
            })
            .Snapshot(
                """
                {
                  dragged: [
                    Docs,
                    Source,
                    notes.aleph
                  ],
                  toggled: [
                    Docs,
                    Source,
                    Report1.beta
                  ]
                }
                """);
    }

    // The rectangle's window is owned by the window the list is in, which disposes it as it
    // closes. A window's only tab dragged onto another closes the window it left, so the next
    // rectangle drawn in the tab moved needs a window of its own again, owned by where it is now.
    [Test]
    public async Task MarqueeAfterTheTabMovesToAnotherWindow()
    {
        using var fixture = Fixture.Create();
        var left = Host();
        using var moved = Host();
        var view = await OpenIn(left, fixture.Root);
        var marquee = view.Marquee;

        // From the margin out across the rows, so the rectangle has a width to draw.
        void Drag(int from, int to)
        {
            marquee.Begin(new(-5, view.RowBounds(from).Top + 2), add: false);
            marquee.Update(new(40, view.RowBounds(to).Top + 2));
        }

        Drag(1, 3);
        var drawnBefore = left.OwnedForms.Any(_ => _.Visible);
        marquee.End();

        moved.Controls.Add(view);
        moved.Show();
        left.Dispose();
        Drag(0, 1);
        var drawnAfter = moved.OwnedForms.Any(_ => _.Visible);
        marquee.End();

        await Assert.That(drawnBefore).IsTrue();
        await Assert.That(drawnAfter).IsTrue();
        await Assert.That(Selected(view)).IsEquivalentTo(Names(view).Take(2));
    }

    // The horizontal scrollbar runs from the left edge of the view, across the margin, as
    // Explorer's does, rather than from where the list starts. It goes with the list's own:
    // moved, it scrolls the list, and the list scrolled another way, it follows.
    [Test]
    public async Task HorizontalScrollBarRunsAcrossTheMargin()
    {
        const int horizontalScrollMessage = 0x0114;
        const int lineRight = 1;
        const int listViewScroll = 0x1014;
        using var fixture = Fixture.Create();
        using var form = Host();
        form.ClientSize = new(300, 400);
        var view = await OpenIn(form, fixture.Root);
        var list = view.Controls.OfType<ShellListView>().Single();
        var bar = view.ScrollBar;

        await Assert.That(list.Left).IsGreaterThan(0);
        await Assert.That(bar.Visible).IsTrue();
        await Assert.That(bar.Bounds).IsEqualTo(
            Rectangle.FromLTRB(0, list.Top + list.ClientSize.Height, list.Left + list.ClientSize.Width, list.Bottom));
        await Assert.That(bar.Maximum).IsEqualTo(list.HorizontalScrollBar!.Value.Maximum);
        await Assert.That(bar.LargeChange).IsEqualTo(list.HorizontalScrollBar!.Value.Page);

        // A click on its right arrow, which it tells the view about.
        NativeMethods.SendMessage(view.Handle, horizontalScrollMessage, lineRight, bar.Handle);
        var clicked = list.HorizontalScrollBar!.Value.Position;

        await Assert.That(clicked).IsGreaterThan(0);
        await Assert.That(bar.Value).IsEqualTo(clicked);

        // The list scrolled as the keyboard would.
        NativeMethods.SendMessage(list.Handle, listViewScroll, 30, IntPtr.Zero);
        var scrolled = list.HorizontalScrollBar!.Value.Position;

        await Assert.That(scrolled).IsGreaterThan(clicked);
        await Assert.That(bar.Value).IsEqualTo(scrolled);

        // Wide enough for every column, the list has no scrollbar, and so neither has the view.
        form.ClientSize = new(1500, 400);

        await Assert.That(list.HorizontalScrollBar).IsNull();
        await Assert.That(bar.Visible).IsFalse();
    }

    // Hiding a column takes it and its cells out of the list, and sorting by it falls back to
    // the name. Showing it again puts it back in its place.
    [Test]
    public async Task HideAndShowColumns()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.Root, settings);
        view.SortBy(typeColumn);

        view.SetColumnShown(typeColumn, false);
        var hidden = new
        {
            columns = view.ColumnTitles.ToList(),
            firstRow = view.FirstRowCells.ToList(),
            rows = view.Entries.Select(_ => _.Name).ToList(),
            setting = settings.HiddenColumns.ToList()
        };

        view.SetColumnShown(typeColumn, true);

        await Verify(
            new
            {
                hidden,
                shownAgain = new
                {
                    columns = view.ColumnTitles.ToList(),
                    firstRow = view.FirstRowCells.ToList(),
                    setting = settings.HiddenColumns.ToList()
                }
            })
            .Snapshot(
                """
                {
                  hidden: {
                    columns: [
                      Name,
                      Date modified,
                      Size
                    ],
                    firstRow: [
                      Archive,
                      14/03/2025 09:26am,
                      
                    ],
                    rows: [
                      Archive,
                      Docs,
                      Source,
                      notes.aleph,
                      Report1.beta,
                      report2.beta,
                      report10.beta,
                      summary.gamma
                    ],
                    setting: [
                      Type
                    ]
                  },
                  shownAgain: {
                    columns: [
                      Name,
                      Date modified,
                      Type,
                      Size
                    ],
                    firstRow: [
                      Archive,
                      14/03/2025 09:26am,
                      File folder,
                      
                    ]
                  }
                }
                """);
    }

    // Name stays ticked and cannot be unticked.
    [Test]
    public async Task HeaderMenu()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false, HiddenColumns = ["Size"]};
        using var view = await Open(fixture.Root, settings);
        using var menu = view.HeaderMenu(clickedColumn: 1);

        await Verify(
            new
            {
                columns = view.ColumnTitles.ToList(),
                menu = menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem).ToList()
            })
            .Snapshot(
                """
                {
                  columns: [
                    Name,
                    Date modified,
                    Type
                  ],
                  menu: [
                    Size column to fit,
                    Size all columns to fit,
                    ----,
                    [x] Name (disabled),
                    [x] Date modified,
                    [x] Type,
                    Size
                  ]
                }
                """);
    }

    // The code folder's repositories each say where they are in a Git column: the branch, then
    // what there is to commit. A folder that isn't one says nothing.
    [Test]
    public async Task GitColumnShowsEachRepository()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Git", "Tag", "Date modified", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(["Changed: main · 1 change", "Clean: main", "origins: ", "Plain: "], CollectionOrdering.Matching);
        await Assert.That(view.GitText).IsEqualTo("");
    }

    // Beside Git, each repository's most recent tag. One without a tag, and a folder that isn't
    // a repository, say nothing.
    [Test]
    public async Task TagColumnShowsEachRepository()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Clean"), "tag", "v1.0");
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        await Assert.That(TagCells(view))
            .IsEquivalentTo(["Changed: ", "Clean: v1.0", "origins: ", "Plain: "], CollectionOrdering.Matching);
    }

    // With Git hidden, the repositories are read all the same, for their tags.
    [Test]
    public async Task TagColumnWithGitHidden()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Clean"), "tag", "v1.0");
        var settings = InCode(code);
        settings.HiddenColumns = ["Git"];
        using var view = await Open(code.Root, settings);
        await GitRead(view);

        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Tag", "Date modified", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(TagCells(view))
            .IsEquivalentTo(["Changed: ", "Clean: v1.0", "origins: ", "Plain: "], CollectionOrdering.Matching);
    }

    // Tagged in a repository listed, the column says so, without going anywhere.
    [Test]
    public async Task TagColumnFollowsANewTag()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        CodeCommandsTests.Scratch.Git(code.At("Clean"), "tag", "v2.0");

        await Pump.Until(() => TagCells(view).Contains("Clean: v2.0"));
    }

    // Gone back to, the code folder shows what was last read of each repository at once, while
    // it reads them again, rather than nothing; then what it has read.
    [Test]
    public async Task GoneBackToTheLastGitStatesShowAtOnce()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        view.Navigate(code.At("Plain"));
        await Pump.Loaded(view);
        await File.WriteAllTextAsync(code.At("Clean", "tracked.txt"), "changed");
        var release = new ManualResetEventSlim();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            release.Wait(cancel);
            return readGit(repo, cancel);
        };
        try
        {
            view.Navigate(code.Root);
            await Pump.Loaded(view);
            var atOnce = GitCells(view);
            release.Set();
            await GitRead(view);

            await Assert.That(atOnce)
                .IsEquivalentTo(["Changed: main · 1 change", "Clean: main", "origins: ", "Plain: "], CollectionOrdering.Matching);
            await Assert.That(GitCells(view))
                .IsEquivalentTo(["Changed: main · 1 change", "Clean: main · 1 change", "origins: ", "Plain: "], CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
            FolderView.ReadGit = readGit;
            release.Dispose();
        }
    }

    // As TortoiseGit has it, a repository is read again only once something in it changes: the
    // code folder gone back to reads none of them, a change, to a file or by a commit, is read
    // without going anywhere, and Refresh reads them all whatever.
    [Test]
    public async Task RepositoriesAreReadAgainOnlyOnceTheyChange()
    {
        using var code = Repositories();
        var reads = new List<string>();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            lock (reads)
            {
                reads.Add(Path.GetFileName(repo));
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await GitRead(view);
            List<string> Reads()
            {
                lock (reads)
                {
                    var taken = reads.Order().ToList();
                    reads.Clear();
                    return taken;
                }
            }

            var first = Reads();
            view.Navigate(code.At("Plain"));
            await Pump.Loaded(view);
            view.Navigate(code.Root);
            await Pump.Loaded(view);
            await GitRead(view);
            var goneBack = Reads();

            await File.WriteAllTextAsync(code.At("Clean", "tracked.txt"), "changed");
            await Pump.Until(() => GitCells(view).Contains("Clean: main · 1 change"));
            await GitRead(view);
            var changed = Reads();

            CodeCommandsTests.Scratch.Git(code.At("Changed"), "commit", "-am", "Change");
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 1 ahead"));
            await GitRead(view);
            var committed = Reads();

            view.ReloadRows();
            await Pump.Loaded(view);
            await GitRead(view);
            var refreshed = Reads();

            await Assert.That(first).IsEquivalentTo(["Changed", "Clean"], CollectionOrdering.Matching);
            await Assert.That(goneBack).IsEmpty();
            await Assert.That(changed).IsEquivalentTo(["Clean"]);
            await Assert.That(committed).IsEquivalentTo(["Changed"]);
            await Assert.That(refreshed).IsEquivalentTo(["Changed", "Clean"], CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // A read that fails is no longer under way: Refresh reads again. Kept as under way, as it
    // was, each read asked for only waited for it to end, Refresh's too, and none was made
    // until the view went to another folder.
    [Test]
    public async Task AReadThatFailedIsNoLongerUnderWay()
    {
        using var code = Repositories();
        var failing = true;
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("The read failed.");
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await Pump.Until(() => view.GitReading.IsCompleted);
            var failure = view.GitReading.Exception;
            Volatile.Write(ref failing, false);

            view.ReloadRows();
            await Pump.Loaded(view);
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 1 change"));
            await GitRead(view);

            await Assert.That(failure).IsNotNull();
            await Assert.That(GitCells(view))
                .IsEquivalentTo(["Changed: main · 1 change", "Clean: main", "origins: ", "Plain: "], CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // What a read found is fresh unless something changed in the repository while it was read:
    // that change is then still to be read.
    [Test]
    public async Task AChangeWhileReadingIsStillToBeRead()
    {
        using var code = Repositories();
        var repo = code.At("Clean");
        GitCache.Watch(code.Root);
        var state = new GitState("main", null, 0, 0, []);

        var before = GitCache.Changes(repo);
        GitCache.Store(repo, state, null, before);
        GitCache.TryGet(repo, out _, out _, out var freshAfterRead);
        var during = GitCache.Changes(repo);
        GitCache.MarkStale([repo]);
        GitCache.Store(repo, state, null, during);
        GitCache.TryGet(repo, out _, out _, out var freshAfterChangeDuringRead);

        await Assert.That(freshAfterRead).IsTrue();
        await Assert.That(freshAfterChangeDuringRead).IsFalse();
    }

    // What git writes for itself changes no state, so is no reason to read: an object, and what
    // its fsmonitor daemon makes and deletes for every status it answers, which would otherwise
    // have each read be the reason for the next. Only the repository that has changed is read.
    [Test]
    public async Task WhatGitWritesForItselfIsNotRead()
    {
        using var code = Repositories();
        var cookies = code.At("Clean", ".git", "fsmonitor--daemon", "cookies");
        Directory.CreateDirectory(cookies);
        var reads = new List<string>();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            lock (reads)
            {
                reads.Add(Path.GetFileName(repo));
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await GitRead(view);

            // Whatever making the repositories left to be heard has been, and read.
            await Pump.For(TimeSpan.FromSeconds(1));
            await GitRead(view);
            lock (reads)
            {
                reads.Clear();
            }

            var cookie = Path.Combine(cookies, "1-1");
            await File.WriteAllTextAsync(cookie, "");
            File.Delete(cookie);
            await File.WriteAllTextAsync(code.At("Clean", ".git", "objects", "info", "object"), "");
            await File.WriteAllTextAsync(code.At("Changed", "new.txt"), "new");
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 2 changes"));
            await GitRead(view);

            List<string> read;
            lock (reads)
            {
                read = reads.ToList();
            }

            await Assert.That(read).IsEquivalentTo(["Changed"]);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // A worktree's index and HEAD are not in it but in the .git of the repository it is of, so
    // a commit in one changes no file of its own. It is read again all the same, and no longer
    // says there is a change to commit.
    [Test]
    public async Task ACommitInAWorktreeIsRead()
    {
        using var code = new CodeCommandsTests.Scratch();
        var main = code.CreateRepo("Main");
        var tree = code.At("Tree");
        CodeCommandsTests.Scratch.Git(main, "worktree", "add", tree, "-b", "tree");
        await File.WriteAllTextAsync(Path.Combine(tree, "tracked.txt"), "changed");
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        var before = GitCells(view);

        CodeCommandsTests.Scratch.Git(tree, "commit", "-am", "Change");
        await Pump.Until(() => GitCells(view).Contains("Tree: tree"));

        await Assert.That(before).Contains("Tree: tree · 1 change");
    }

    // A commit writes the index and HEAD in the repository's git folder, and none of its files.
    // Where nothing hears that folder, outside the code folder as a worktree's is whose
    // repository is there, or one cloned there with --separate-git-dir, or not in a .git, as a
    // bare repository's worktrees have it, the repository is never fresh: gone back to, it is
    // read again, and no longer says there is a change to commit.
    [Test]
    public async Task ARepositoryWhoseGitFolderIsNotHeardIsReadWhenGoneBackTo()
    {
        using var code = new CodeCommandsTests.Scratch();
        using var outside = new CodeCommandsTests.Scratch();
        var main = outside.CreateRepo("Main");
        var tree = code.At("Tree");
        CodeCommandsTests.Scratch.Git(main, "worktree", "add", tree, "-b", "tree");
        var separate = code.At("Separate");
        CodeCommandsTests.Scratch.Git(outside.Root, "clone", "--separate-git-dir", outside.At("Separate"), outside.Origin("Main"), separate);
        CodeCommandsTests.Scratch.Identify(separate);
        var bare = code.At("Bare.git");
        var work = code.At("Work");
        CodeCommandsTests.Scratch.Git(code.Root, "clone", "--bare", outside.Origin("Main"), bare);
        CodeCommandsTests.Scratch.Identify(bare);
        CodeCommandsTests.Scratch.Git(bare, "worktree", "add", work, "-b", "work");
        Directory.CreateDirectory(code.At("Plain"));
        foreach (var repo in new[] { tree, separate, work })
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        }

        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        // Whatever making the repositories left to be heard has been, and read.
        await Pump.For(TimeSpan.FromSeconds(1));
        await GitRead(view);
        var before = GitCells(view);
        foreach (var repo in new[] { tree, separate, work })
        {
            CodeCommandsTests.Scratch.Git(repo, "commit", "-am", "Change");
        }

        view.Navigate(code.At("Plain"));
        await Pump.Loaded(view);
        view.Navigate(code.Root);
        await Pump.Loaded(view);
        await GitRead(view);

        await Assert.That(before)
            .IsEquivalentTo(["Bare.git: ", "Plain: ", "Separate: main · 1 change", "Tree: tree · 1 change", "Work: work · 1 change"], CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(["Bare.git: ", "Plain: ", "Separate: main · 1 ahead", "Tree: tree", "Work: work"], CollectionOrdering.Matching);
    }

    static bool IsFresh(string repo)
    {
        GitCache.TryGet(repo, out _, out _, out var fresh);
        return fresh;
    }

    static void ReadNow(params string[] repos)
    {
        foreach (var repo in repos)
        {
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
        }
    }

    // Which of two repositories a change at path is a change to, as the cache counts their
    // changes. Not whether each is still fresh after it, which also asks whether the watch is
    // listening just then, and takes in whatever else the watch has heard meanwhile.
    static async Task<(bool, bool)> ChangeAt(string path, string first, string second, WatcherChangeTypes change = WatcherChangeTypes.Changed)
    {
        await Quiet(first, second);
        var firstBefore = GitCache.Changes(first);
        var secondBefore = GitCache.Changes(second);
        GitCache.ChangedAt(path, change);
        return (GitCache.Changes(first) != firstBefore, GitCache.Changes(second) != secondBefore);
    }

    // The watch can yet tell of what a test made just before it began, a folder's time changed
    // say, and on a busy machine, as CI's is, that comes after what the test then counts. So it
    // counts once neither repository has had a change for a moment.
    static async Task Quiet(string first, string second)
    {
        var quiet = Stopwatch.StartNew();
        var waited = Stopwatch.StartNew();
        var counts = (GitCache.Changes(first), GitCache.Changes(second));
        while (quiet.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new($"The watch went on hearing of changes to {first} and {second}.");
            }

            await Pump.For(TimeSpan.FromMilliseconds(25));
            var now = (GitCache.Changes(first), GitCache.Changes(second));
            if (now != counts)
            {
                counts = now;
                quiet.Restart();
            }
        }
    }

    // What changes in a worktree's own git folder, its index say, is a change to it and not to
    // the repository it is of, whose index is no change to the worktree. A branch, which they
    // share, is a change to both: a worktree's commit moves one, and a fetch those it is
    // ahead of and behind. Its log is noise, a change to neither.
    [Test]
    public async Task AWorktreesGitFolderIsItsOwnAndItsBranchesShared()
    {
        using var code = new CodeCommandsTests.Scratch();
        var main = code.At("Main");
        var tree = code.At("Tree");
        var own = Path.Combine(main, ".git", "worktrees", "Tree");
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(own, "commondir"), "../..");
        await File.WriteAllTextAsync(Path.Combine(tree, ".git"), $"gitdir: {own.Replace('\\', '/')}");
        GitCache.Watch(code.Root);

        var itsIndex = await ChangeAt(Path.Combine(own, "index"), main, tree);
        var theOthersIndex = await ChangeAt(Path.Combine(main, ".git", "index"), main, tree);
        var aBranch = await ChangeAt(Path.Combine(main, ".git", "refs", "heads", "tree"), main, tree);
        var itsLog = await ChangeAt(Path.Combine(own, "logs", "HEAD"), main, tree);

        await Assert.That(itsIndex).IsEqualTo((false, true));
        await Assert.That(theOthersIndex).IsEqualTo((true, false));
        await Assert.That(aBranch).IsEqualTo((true, true));
        await Assert.That(itsLog).IsEqualTo((false, false));
    }

    // A submodule's git folder is in the .git of the repository it is in. A commit there is a
    // change to the submodule, and to that repository, which has it at a commit. Its objects
    // are a change to neither.
    [Test]
    public async Task ASubmodulesGitFolderIsItsOwn()
    {
        using var code = new CodeCommandsTests.Scratch();
        var super = code.At("Super");
        var sub = Path.Combine(super, "sub");
        var own = Path.Combine(super, ".git", "modules", "sub");
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, ".git"), "gitdir: ../.git/modules/sub");
        GitCache.Watch(code.Root);

        var itsHead = await ChangeAt(Path.Combine(own, "HEAD"), super, sub);
        var itsObjects = await ChangeAt(Path.Combine(own, "objects", "3f", "9a8b"), super, sub);

        await Assert.That(itsHead).IsEqualTo((true, true));
        await Assert.That(itsObjects).IsEqualTo((false, false));
    }

    // A .git folder told of as changed is only its time, which moves as a lock comes and goes
    // for a read: no change, or each read that took a lock would be the reason for the next.
    // Made or deleted it is one. So is a worktree's .git changing, a file saying where its git
    // folder is.
    [Test]
    public async Task AGitFoldersTimeIsNoChange()
    {
        using var code = new CodeCommandsTests.Scratch();
        var main = code.CreateRepo("Main");
        var tree = code.At("Tree");
        var own = Path.Combine(main, ".git", "worktrees", "Tree");
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(own, "commondir"), "../..");
        await File.WriteAllTextAsync(Path.Combine(tree, ".git"), $"gitdir: {own.Replace('\\', '/')}");
        GitCache.Watch(code.Root);

        var itsTime = await ChangeAt(Path.Combine(main, ".git"), main, tree);
        var made = await ChangeAt(Path.Combine(main, ".git"), main, tree, WatcherChangeTypes.Created);
        var deleted = await ChangeAt(Path.Combine(main, ".git"), main, tree, WatcherChangeTypes.Deleted);
        var theWorktrees = await ChangeAt(Path.Combine(tree, ".git"), main, tree);

        await Assert.That(itsTime).IsEqualTo((false, false));
        await Assert.That(made).IsEqualTo((true, false));
        await Assert.That(deleted).IsEqualTo((true, false));
        await Assert.That(theWorktrees).IsEqualTo((false, true));
    }

    // A repository that ignores bin and obj, with nothing to commit.
    static string IgnoringOutput(CodeCommandsTests.Scratch code)
    {
        var repo = code.CreateRepo("Ignoring");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Ignore");
        CodeCommandsTests.Scratch.Git(repo, "push");
        return repo;
    }

    // A build's output is in folders git ignores, where no change is one to the repository. The
    // first change in such a folder has it looked at, and from then on those in it are not
    // read: only the repository that has changed is.
    [Test]
    public async Task ChangesInAFolderGitIgnoresAreNotRead()
    {
        using var code = Repositories();
        var bin = Path.Combine(IgnoringOutput(code), "bin");
        Directory.CreateDirectory(Path.Combine(bin, "Debug"));
        var reads = new List<string>();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            lock (reads)
            {
                reads.Add(Path.GetFileName(repo));
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await GitRead(view);
            await File.WriteAllTextAsync(Path.Combine(bin, "first.dll"), "");
            await Pump.Until(() => GitCache.Ignored(bin) == true);

            // Whatever making the repositories left to be heard has been, and read.
            await Pump.For(TimeSpan.FromSeconds(1));
            await GitRead(view);
            lock (reads)
            {
                reads.Clear();
            }

            await File.WriteAllTextAsync(Path.Combine(bin, "second.dll"), "");
            await File.WriteAllTextAsync(Path.Combine(bin, "Debug", "third.dll"), "");
            await File.WriteAllTextAsync(code.At("Changed", "new.txt"), "new");
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 2 changes"));
            await GitRead(view);

            List<string> read;
            lock (reads)
            {
                read = reads.ToList();
            }

            await Assert.That(read).IsEquivalentTo(["Changed"]);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // Which folders git ignores, as read in process: each asked of, and the folders it is in
    // down to the first ignored, as everything in that is ignored too. One ignored that holds a
    // file git tracks all the same isn't given as ignored, as a change to that file is a change,
    // nor is a folder in it that holds one, but one in it that holds none is.
    [Test]
    public async Task WhichFoldersGitIgnores()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = IgnoringOutput(code);
        var bin = Path.Combine(repo, "bin");
        Directory.CreateDirectory(Path.Combine(bin, "Release"));
        await File.WriteAllTextAsync(Path.Combine(bin, "kept.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(bin, "Release", "kept.txt"), "kept");
        CodeCommandsTests.Scratch.Git(repo, "add", "-f", "bin/kept.txt", "bin/Release/kept.txt");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Kept");
        var debug = Path.Combine(repo, "src", "Project", "obj", "Debug");
        Directory.CreateDirectory(debug);

        var found = InProcessState.Ignored(repo, [debug, bin, Path.Combine(bin, "Debug"), Path.Combine(bin, "Release")]);

        var said = found
            .OrderBy(_ => _.Key, StringComparer.Ordinal)
            .Select(_ => $"{code.Relative(_.Key)}: {_.Value}")
            .ToList();
        await Assert.That(said)
            .IsEquivalentTo(
                [
                    "Ignoring/bin: False",
                    "Ignoring/bin/Debug: True",
                    "Ignoring/bin/Release: False",
                    "Ignoring/src: False",
                    "Ignoring/src/Project: False",
                    "Ignoring/src/Project/obj: True",
                    "Ignoring/src/Project/obj/Debug: True"
                ],
                CollectionOrdering.Matching);
    }

    // In an ignored folder that holds a file git tracks all the same, bin holding bin/kept.txt,
    // which isn't ignored, a folder holding none, a build's bin\Debug, is: learned once, a change
    // in it, or further in, is no change to the repository from then on, and it isn't asked of
    // again. Given only the ignored folder, bin, as not, it was asked of again, the repository's
    // config, refs, ignore files and index read again, and the repository made stale, for each
    // change in it through a build. A change in bin itself, or in a folder in it that holds a
    // tracked file, is a change, learned of or not.
    [Test]
    public async Task AFolderInAnIgnoredOneHoldingATrackedFileIsLearnedOnce()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = IgnoringOutput(code);
        var bin = Path.Combine(repo, "bin");
        var debug = Path.Combine(bin, "Debug");
        var release = Path.Combine(bin, "Release");
        var src = Path.Combine(repo, "src");
        Directory.CreateDirectory(release);
        await File.WriteAllTextAsync(Path.Combine(bin, "kept.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(release, "kept.txt"), "kept");
        CodeCommandsTests.Scratch.Git(repo, "add", "-f", "bin/kept.txt", "bin/Release/kept.txt");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Kept");
        GitCache.Watch(code.Root);
        var asked = new List<string>();
        var askIgnored = GitCache.AskIgnored;
        GitCache.AskIgnored = (at, folders) =>
        {
            lock (asked)
            {
                asked.AddRange(folders);
            }

            return askIgnored(at, folders);
        };
        async Task<bool> ChangeIn(string path) =>
            (await ChangeAt(path, repo, repo)).Item1;

        try
        {
            ReadNow(repo);
            var first = await ChangeIn(Path.Combine(debug, "first.dll"));
            await Pump.Until(() => GitCache.Ignored(bin) != null);
            var debugLearned = GitCache.Ignored(debug);
            var later = await ChangeIn(Path.Combine(debug, "second.dll"));
            var further = await ChangeIn(Path.Combine(debug, "net11.0", "third.dll"));
            var inBin = await ChangeIn(Path.Combine(bin, "output.dll"));
            var inRelease = await ChangeIn(Path.Combine(release, "output.dll"));

            // Folders are looked at in turn, so once src has been, all asked of before it have.
            GitCache.ChangedAt(Path.Combine(src, "Thing.cs"));
            await Pump.Until(() => GitCache.Ignored(src) != null);
            var releaseLearned = GitCache.Ignored(release);
            var inReleaseLearned = await ChangeIn(Path.Combine(release, "again.dll"));
            int debugAsked;
            lock (asked)
            {
                debugAsked = asked.Count(_ => _.StartsWith(debug, StringComparison.OrdinalIgnoreCase));
            }

            await Assert.That(first).IsTrue();
            await Assert.That(debugLearned).IsTrue();
            await Assert.That(later).IsFalse();
            await Assert.That(further).IsFalse();
            await Assert.That(debugAsked).IsEqualTo(1);
            await Assert.That(inBin).IsTrue();
            await Assert.That(inRelease).IsTrue();
            await Assert.That(releaseLearned).IsFalse();
            await Assert.That(inReleaseLearned).IsTrue();
        }
        finally
        {
            GitCache.AskIgnored = askIgnored;
        }
    }

    // A repository nested in a folder the one it is in ignores, a clone in external say, is its
    // own once it is known, though the one it is in learned before then that git ignores the
    // folder: a change at its top, to its README or its own .gitignore, is a change to it, as
    // one further in always was, and none to the one it is in.
    [Test]
    public async Task ANestedRepositoryInAFolderLearnedIgnoredIsItsOwn()
    {
        using var code = new CodeCommandsTests.Scratch();
        var outer = code.CreateRepo("Outer");
        await File.WriteAllTextAsync(Path.Combine(outer, ".gitignore"), "external/\n");
        CodeCommandsTests.Scratch.Git(outer, "add", ".");
        CodeCommandsTests.Scratch.Git(outer, "commit", "-m", "Ignore");
        var inner = Path.Combine(outer, "external");
        Directory.CreateDirectory(inner);
        CodeCommandsTests.Scratch.Git(inner, "init", "--initial-branch=main");
        var readme = Path.Combine(inner, "README.md");
        await File.WriteAllTextAsync(readme, "inner");
        GitCache.Watch(code.Root);
        ReadNow(outer);
        GitCache.ChangedAt(readme);
        await Pump.Until(() => GitCache.Ignored(inner) == true);

        ReadNow(inner);
        var itsReadme = await ChangeAt(readme, outer, inner);
        var itsGitIgnore = await ChangeAt(Path.Combine(inner, ".gitignore"), outer, inner);

        await Assert.That(itsReadme).IsEqualTo((false, true));
        await Assert.That(itsGitIgnore).IsEqualTo((false, true));
    }

    // Nor is what the one it is in finds of the folder kept, asked before the nested one was
    // known, and found after: it is of git there as it no longer is.
    [Test]
    public async Task WhatIsFoundOfANestedRepositoryAsItBecomesKnownIsNotKept()
    {
        using var code = new CodeCommandsTests.Scratch();

        // Not repositories, so nothing git left of them is heard meanwhile.
        var outer = code.At("Outer");
        var inner = Path.Combine(outer, "external");
        var bin = Path.Combine(outer, "bin");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(bin);
        GitCache.Watch(code.Root);
        using var asking = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var askIgnored = GitCache.AskIgnored;
        GitCache.AskIgnored = (_, asked) =>
        {
            asking.Set();
            release.Wait();
            var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in asked)
            {
                found[folder] = true;
            }

            return found;
        };
        try
        {
            ReadNow(outer);
            GitCache.ChangedAt(Path.Combine(inner, "README.md"));
            await Pump.Until(() => asking.IsSet);
            ReadNow(inner);

            // Found once the folder asked of meanwhile is, which is asked of next.
            GitCache.ChangedAt(Path.Combine(bin, "output.dll"));
            release.Set();
            await Pump.Until(() => GitCache.Ignored(bin) == true);
            var learned = GitCache.Ignored(inner);
            var itsReadme = await ChangeAt(Path.Combine(inner, "README.md"), outer, inner);

            await Assert.That(learned).IsNull();
            await Assert.That(itsReadme).IsEqualTo((false, true));
        }
        finally
        {
            release.Set();
            GitCache.AskIgnored = askIgnored;
        }
    }

    // A change in a folder learned to be ignored leaves the repository fresh. What was learned
    // is forgotten, to be learned again, when what git ignores may be other than it was: a
    // .gitignore changed, or the index, as a file added there by force is tracked from then on.
    [Test]
    public async Task WhatIsIgnoredIsLearnedAndForgotten()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        var obj = Path.Combine(repo, "obj");
        var output = Path.Combine(obj, "Debug", "output.dll");
        var src = Path.Combine(repo, "src");
        GitCache.Watch(code.Root);
        var askIgnored = GitCache.AskIgnored;
        GitCache.AskIgnored = (_, _) => new(StringComparer.OrdinalIgnoreCase)
        {
            [obj] = true,
            [src] = false
        };
        try
        {
            ReadNow(repo);
            GitCache.ChangedAt(output);
            GitCache.ChangedAt(Path.Combine(src, "Thing.cs"));
            var freshAtFirst = IsFresh(repo);
            await Pump.Until(() => GitCache.Ignored(obj) == true);
            ReadNow(repo);
            GitCache.ChangedAt(output);
            GitCache.ChangedAt(obj);
            var freshOnceLearned = IsFresh(repo);

            // In a folder looked at, and not ignored, as much as in one that hasn't been.
            await Pump.Until(() => GitCache.Ignored(src) == false);
            GitCache.ChangedAt(Path.Combine(src, ".gitignore"));
            var afterGitIgnore = GitCache.Ignored(obj);
            GitCache.ChangedAt(output);
            await Pump.Until(() => GitCache.Ignored(obj) == true);
            GitCache.ChangedAt(Path.Combine(repo, ".git", "index"));
            var afterIndex = GitCache.Ignored(obj);

            await Assert.That(freshAtFirst).IsFalse();
            await Assert.That(freshOnceLearned).IsTrue();
            await Assert.That(afterGitIgnore).IsNull();
            await Assert.That(afterIndex).IsNull();
        }
        finally
        {
            GitCache.AskIgnored = askIgnored;
        }
    }

    // A read that finds nothing, git refusing the repository for a moment say, has it known no
    // longer, and what was learned of it is forgotten with it: while it isn't known no change
    // to its .gitignore is heard, so bin, unignored meanwhile, is a folder git doesn't ignore
    // once it is read again, and a change in it a change. Kept, changes in bin were dropped
    // until Lode started again.
    [Test]
    public async Task WhatWasLearnedIsForgottenWithARepositoryReadAsNone()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        var bin = Path.Combine(repo, "bin");
        var output = Path.Combine(bin, "output.dll");
        GitCache.Watch(code.Root);
        var ignored = true;
        var askIgnored = GitCache.AskIgnored;
        GitCache.AskIgnored = (_, asked) =>
        {
            var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in asked)
            {
                found[folder] = ignored;
            }

            return found;
        };
        try
        {
            ReadNow(repo);
            GitCache.ChangedAt(output);
            await Pump.Until(() => GitCache.Ignored(bin) == true);

            GitCache.Store(repo, null, null, GitCache.Changes(repo));
            ignored = false;
            ReadNow(repo);
            var learned = GitCache.Ignored(bin);
            var inBin = await ChangeAt(output, repo, repo);
            var fresh = IsFresh(repo);

            await Assert.That(learned).IsNull();
            await Assert.That(inBin).IsEqualTo((true, true));
            await Assert.That(fresh).IsFalse();
        }
        finally
        {
            GitCache.AskIgnored = askIgnored;
        }
    }

    // Too many changes at once for the watch to keep track of, twice over, every repository is
    // stale at once, but the views are told once, when that has settled, not as each comes.
    [Test]
    public async Task TooManyChangesAreToldOfOnceSettled()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        GitCache.Watch(code.Root);
        var told = new List<string>();
        void OnStale(IReadOnlyCollection<string> stale)
        {
            lock (told)
            {
                told.AddRange(stale);
            }
        }

        int Told()
        {
            lock (told)
            {
                return told.Count(_ => _ == repo);
            }
        }

        GitCache.Stale += OnStale;
        try
        {
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.AllChanged(code.Root);
            GitCache.AllChanged(code.Root);
            var toldAtOnce = Told();
            GitCache.TryGet(repo, out _, out _, out var fresh);
            await Pump.Until(() => Told() > 0);
            await Pump.For(TimeSpan.FromSeconds(1));

            await Assert.That(toldAtOnce).IsEqualTo(0);
            await Assert.That(fresh).IsFalse();
            await Assert.That(Told()).IsEqualTo(1);
        }
        finally
        {
            GitCache.Stale -= OnStale;
        }
    }

    // Only the code folder is watched, and while it is Windows lets nobody rename a folder it is
    // in. Let go of, Options having another or none, nothing of Lode's stops that.
    [Test]
    public async Task TheCodeFolderIsWatchedUntilLetGoOf()
    {
        using var code = Repositories();
        var holder = code.At("Holder");
        var folder = Path.Combine(holder, "Code");
        var renamed = code.At("Renamed");
        Directory.CreateDirectory(folder);

        GitCache.Watch(folder);
        var watching = GitCache.IsWatching(folder);
        var renamedWhileWatched = TryRename(holder, renamed);
        GitCache.LetGo(folder);
        var watchingOnceLetGo = GitCache.IsWatching(folder);
        var renamedOnceLetGo = TryRename(holder, renamed);

        await Assert.That(watching).IsTrue();
        await Assert.That(renamedWhileWatched).IsFalse();
        await Assert.That(watchingOnceLetGo).IsFalse();
        await Assert.That(renamedOnceLetGo).IsTrue();

        static bool TryRename(string from, string to)
        {
            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // A code folder that isn't there, renamed or deleted while Lode wasn't running, is not
    // watched, so nothing of it is kept fresh. Nothing fails for it.
    [Test]
    public async Task ACodeFolderThatIsNotThereIsNotWatched()
    {
        using var code = Repositories();
        var folder = code.At("NotThere");
        var repo = Path.Combine(folder, "Repo");

        GitCache.Watch(folder);
        GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
        GitCache.TryGet(repo, out _, out _, out var fresh);

        await Assert.That(GitCache.IsWatching(folder)).IsFalse();
        await Assert.That(fresh).IsFalse();
    }

    // Too many changes in one code folder say nothing of another's repositories.
    [Test]
    public async Task TooManyChangesInOneCodeFolderLeaveAnotherFresh()
    {
        using var code = Repositories();
        using var other = Repositories();
        var repo = code.At("Plain");
        GitCache.Watch(code.Root);
        GitCache.Watch(other.Root);
        GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));

        GitCache.AllChanged(other.Root);
        GitCache.TryGet(repo, out _, out _, out var fresh);

        await Assert.That(fresh).IsTrue();
    }

    // A repository whose files go on changing while reading it finds nothing new, a build's
    // output say, is held: it counts as fresh, so nothing reads it. A change of git's own in
    // it, a commit say, ends that, and its views are told once that has settled.
    [Test]
    public async Task HeldWhileNothingNewIsFoundUntilGitItselfChanges()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        var output = Path.Combine(repo, "bin", "output.dll");
        GitCache.Watch(code.Root);
        var told = new List<string>();
        void OnStale(IReadOnlyCollection<string> stale)
        {
            lock (told)
            {
                told.AddRange(stale);
            }
        }

        GitCache.Stale += OnStale;
        try
        {
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.ChangedAt(output);
            GitCache.TryGet(repo, out _, out _, out var freshOnceChanged);
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.ChangedAt(output);
            GitCache.TryGet(repo, out _, out _, out var freshWhileHeld);
            GitCache.ChangedAt(Path.Combine(repo, ".git", "index"));
            GitCache.TryGet(repo, out _, out _, out var freshOnceGitChanged);
            await Pump.Until(
                () =>
                {
                    lock (told)
                    {
                        return told.Contains(repo);
                    }
                });

            await Assert.That(freshOnceChanged).IsFalse();
            await Assert.That(freshWhileHeld).IsTrue();
            await Assert.That(freshOnceGitChanged).IsFalse();
        }
        finally
        {
            GitCache.Stale -= OnStale;
        }
    }

    // By what the column says: nothing first, then by branch, then by what there is to commit.
    [Test]
    public async Task SortByGit()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        view.SortBy(FolderView.gitColumn);

        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["origins", "Plain", "Clean", "Changed"], CollectionOrdering.Matching);
    }

    // Sorted by what the Git column says, what it says of each is worked out once for the sort,
    // rather than for both items of every comparison.
    [Test]
    public async Task SortedByGitWhatEachSaysIsWorkedOutOnce()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        var made = new List<string>();
        FolderView.MakingSortKey = _ =>
        {
            lock (made)
            {
                made.Add(_.Name);
            }
        };
        try
        {
            view.SortBy(FolderView.gitColumn);

            await Assert.That(made.Order(StringComparer.Ordinal))
                .IsEquivalentTo(["Changed", "Clean", "Plain", "origins"], CollectionOrdering.Matching);
            await Assert.That(view.Entries.Select(_ => _.Name))
                .IsEquivalentTo(["origins", "Plain", "Clean", "Changed"], CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.MakingSortKey = null;
        }
    }

    // Sorted by Git, a large folder is sorted again off the UI thread when a state changes, as
    // it is when the column is clicked, rather than on it each time.
    [Test]
    public async Task SortedByGitALargeFolderIsSortedAgainOffTheUIThread()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        var uiThread = Environment.CurrentManagedThreadId;
        var sortedHere = 0;
        var sortedElsewhere = 0;
        var from = FolderView.SortOffThreadFrom;
        FolderView.SortOffThreadFrom = 0;
        FolderView.Sorting = (_, _) =>
        {
            if (Environment.CurrentManagedThreadId == uiThread)
            {
                sortedHere++;
                return;
            }

            Interlocked.Increment(ref sortedElsewhere);
        };
        try
        {
            view.SortBy(FolderView.gitColumn);
            await Pump.Until(() => Names(view).SequenceEqual(["origins", "Plain", "Clean", "Changed"]));
            var beforeTheChange = Volatile.Read(ref sortedElsewhere);

            // Clean then has a change, as Changed has, which leaves them by name.
            await File.WriteAllTextAsync(code.At("Clean", "tracked.txt"), "changed");
            await Pump.Until(() => Names(view).SequenceEqual(["origins", "Plain", "Changed", "Clean"]));
            await GitRead(view);

            await Assert.That(sortedHere).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref sortedElsewhere)).IsGreaterThan(beforeTheChange);
            await Assert.That(GitCells(view)).Contains("Clean: main · 1 change");
        }
        finally
        {
            FolderView.Sorting = null;
            FolderView.SortOffThreadFrom = from;
        }
    }

    // The column only shows once the folder has been read and its repositories found, so the
    // sort it was left with is taken up then.
    [Test]
    public async Task AFolderSortedByGitIsSoWhenOpened()
    {
        using var code = Repositories();
        var settings = InCode(code);
        settings.SetSort(code.Root, "Git", false);
        using var view = await Open(code.Root, settings);
        await GitRead(view);

        await Assert.That(view.Sort).IsEqualTo((FolderView.gitColumn, false));
        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["Changed", "Clean", "Plain", "origins"], CollectionOrdering.Matching);
    }

    // Numbers in a tag as numbers, so v1.10 comes after v1.9. Those without a tag first.
    [Test]
    public async Task SortByTag()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Clean"), "tag", "v1.10");
        CodeCommandsTests.Scratch.Git(code.At("Changed"), "tag", "v1.9");
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        view.SortBy(FolderView.tagColumn);

        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["origins", "Plain", "Changed", "Clean"], CollectionOrdering.Matching);
    }

    // As for Git, the sort the folder was left with is taken up once its repositories are found.
    [Test]
    public async Task AFolderSortedByTagIsSoWhenOpened()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Clean"), "tag", "v1.10");
        CodeCommandsTests.Scratch.Git(code.At("Changed"), "tag", "v1.9");
        var settings = InCode(code);
        settings.SetSort(code.Root, "Tag", false);
        using var view = await Open(code.Root, settings);
        await GitRead(view);

        await Assert.That(view.Sort).IsEqualTo((FolderView.tagColumn, false));
        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["Clean", "Changed", "Plain", "origins"], CollectionOrdering.Matching);
    }

    // Hidden from the header's menu, it stays so, but the status bar still says.
    [Test]
    public async Task GitColumnHidden()
    {
        using var code = Repositories();
        var settings = InCode(code);
        settings.HiddenColumns = ["Git"];
        using var view = await Open(code.Root, settings);
        await GitRead(view);
        using var menu = view.HeaderMenu(clickedColumn: 0);

        await Assert.That(view.ColumnTitles).DoesNotContain("Git");
        await Assert.That(menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem)).Contains("Git");
    }

    // Anywhere in a repository, the status bar says where the repository is.
    [Test]
    public async Task StatusSaysWhereTheRepositoryIs()
    {
        using var code = Repositories();
        var src = code.At("Changed", "src");
        Directory.CreateDirectory(src);
        using var view = await Open(src, InCode(code));
        await GitRead(view);

        await Assert.That(view.GitText).IsEqualTo("main · 1 change");
    }

    // In a repository whose origin is on GitHub, the status bar links to its page there. One
    // whose origin is a folder links nowhere.
    [Test]
    public async Task StatusLinksToTheSite()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Changed"), "remote", "set-url", "origin", "git@github.com:octo/repo.git");
        using var view = await Open(code.At("Changed"), InCode(code));
        await GitRead(view);
        var onGitHub = view.GitSite;

        view.Navigate(code.At("Clean"));
        await Pump.Loaded(view);
        await GitRead(view);

        await Assert.That(onGitHub).IsEqualTo(new("GitHub", "https://github.com/octo/repo"));
        await Assert.That(view.GitSite).IsNull();
    }

    // In a folder of repositories, the code folder say, a repository selected on its own is what
    // the status bar is about: its state and its site. Nothing selected, two, or a folder that is
    // no repository, it is about the one the folder is in, here none.
    [Test]
    public async Task StatusFollowsARepositorySelectedOnItsOwn()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Changed"), "remote", "set-url", "origin", "git@github.com:octo/repo.git");
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        var nothingSelected = (view.GitText, view.GitRepo);

        async Task<(string Text, string? Repo, RemoteSite? Site)> Selecting(params string[] names)
        {
            view.Reveal([.. names.Select(_ => code.At(_))]);
            await Pump.Until(() => Selected(view).Order().SequenceEqual(names.Order()));
            return (view.GitText, view.GitRepo, view.GitSite);
        }

        var changed = await Selecting("Changed");
        var clean = await Selecting("Clean");
        var plain = await Selecting("Plain");
        var both = await Selecting("Changed", "Clean");

        await Assert.That(nothingSelected).IsEqualTo(("", (string?) null));
        await Assert.That(changed).IsEqualTo(("main · 1 change", code.At("Changed"), new RemoteSite("GitHub", "https://github.com/octo/repo")));
        await Assert.That(clean).IsEqualTo(("main", code.At("Clean"), (RemoteSite?) null));
        await Assert.That(plain).IsEqualTo(("", (string?) null, (RemoteSite?) null));
        await Assert.That(both).IsEqualTo(("", (string?) null, (RemoteSite?) null));
    }

    // In a repository, a file selected leaves the status bar about the repository.
    [Test]
    public async Task StatusStaysOnTheRepositoryForAFileSelected()
    {
        using var code = Repositories();
        using var view = await Open(code.At("Changed"), InCode(code));
        await GitRead(view);

        view.Reveal(code.At("Changed", "tracked.txt"));
        await Pump.Until(() => Selected(view).Count == 1);

        await Assert.That(view.GitText).IsEqualTo("main · 1 change");
        await Assert.That(view.GitRepo).IsEqualTo(code.At("Changed"));
    }

    // In a repository, each file's name is marked with what has become of it, as a letter, and
    // each folder's with how many changes it holds, the Git column hidden. One unchanged has
    // none. Shown from the header's menu, the column says it instead, there in every view, and
    // kept.
    [Test]
    public async Task NamesMarkedWithEachChange()
    {
        using var code = Changes();
        var settings = InCode(code);
        using var view = await Open(code.At("Repo"), settings);
        await GitRead(view);

        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Date modified", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(Marks(view))
            .IsEquivalentTo(
                [
                    "New: U",
                    "src: 2",
                    "Unchanged: ",
                    "added.txt: A",
                    "loose.txt: U",
                    "same.txt: ",
                    "tracked.txt: M"
                ],
                CollectionOrdering.Matching);
        await Assert.That(view.GitText).IsEqualTo("main · 6 changes · 1 ahead");

        view.SetColumnShown(FolderView.gitColumn, true);

        await Assert.That(settings.GitColumnInRepository).IsTrue();
        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Git", "Date modified", "Type", "Size"], CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(
                [
                    "New: Untracked",
                    "src: 2 changes",
                    "Unchanged: ",
                    "added.txt: Added",
                    "loose.txt: Untracked",
                    "same.txt: ",
                    "tracked.txt: Modified"
                ],
                CollectionOrdering.Matching);
        await Assert.That(Marks(view).All(_ => _.EndsWith(": ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(settings.HiddenColumns).IsEmpty();
    }

    // Each name in the colour for what has become of it, a folder holding changes as Modified,
    // and one unchanged as the list's text.
    [Test]
    public async Task NamesColouredForEachChange()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);
        var unchanged = NameColor(view, "same.txt");

        await Assert.That(NameColor(view, "tracked.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(NameColor(view, "loose.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Untracked));
        await Assert.That(NameColor(view, "added.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Added));
        await Assert.That(NameColor(view, "src")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(unchanged).IsNotEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(NameColor(view, "Unchanged")).IsEqualTo(unchanged);
    }

    // With the Git column shown, each cell in the colour for what has become of it, a folder
    // holding changes as Modified, and one unchanged in the gray of every other detail.
    [Test]
    public async Task GitColumnColoursEachChange()
    {
        using var code = Changes();
        var settings = InCode(code);
        settings.GitColumnInRepository = true;
        using var view = await Open(code.At("Repo"), settings);
        await GitRead(view);

        await Assert.That(GitColor(view, "tracked.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(GitColor(view, "loose.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Untracked));
        await Assert.That(GitColor(view, "added.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Added));
        await Assert.That(GitColor(view, "src")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(GitColor(view, "same.txt")).IsEqualTo(ExplorerStyle.SecondaryText);
    }

    // Deeper in, a file is found by its path from the top of the repository.
    [Test]
    public async Task MarksInAFolderOfTheRepository()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo", "src"), InCode(code));
        await GitRead(view);

        await Assert.That(Marks(view))
            .IsEquivalentTo(["Deep: 1", "changed.cs: M", "same.cs: "], CollectionOrdering.Matching);
    }

    // A search in a repository marks what it finds in the folders below with what has become of
    // it.
    [Test]
    public async Task MarksInASearch()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        view.Filter = "*.cs";
        await Pump.Loaded(view);

        await Assert.That(view.ColumnTitles.Take(2))
            .IsEquivalentTo(["Name", "Folder"], CollectionOrdering.Matching);
        await Assert.That(Marks(view))
            .IsEquivalentTo(["changed.cs: M", "same.cs: ", "deeper.cs: M"], CollectionOrdering.Matching);
    }

    // Sorted by Git in a repository, what has changed comes together, by what has become of it.
    [Test]
    public async Task SortFilesByGit()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        view.SortBy(FolderView.gitColumn);

        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["Unchanged", "src", "New", "same.txt", "added.txt", "tracked.txt", "loose.txt"], CollectionOrdering.Matching);
    }

    // A repository in the code folder with a file changed, one staged as new, one untracked, a
    // folder of them, a folder with changes deeper in, and a file and a folder unchanged.
    static CodeCommandsTests.Scratch Changes()
    {
        var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "same.txt");
        Write(repo, "Unchanged", "kept.txt");
        Write(repo, "src", "changed.cs");
        Write(repo, "src", "same.cs");
        Write(repo, "src", "Deep", "deeper.cs");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "More");

        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, "src", "changed.cs"), "changed");
        File.WriteAllText(Path.Combine(repo, "src", "Deep", "deeper.cs"), "changed");
        Write(repo, "added.txt");
        CodeCommandsTests.Scratch.Git(repo, "add", "added.txt");
        Write(repo, "loose.txt");
        Write(repo, "New", "one.txt");
        return code;

        static void Write(string repo, params string[] parts)
        {
            var path = Path.Combine([repo, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, parts[^1]);
        }
    }

    // The colour of a row's Git cell, the second column.
    static Color GitColor(FolderView view, string name)
    {
        var index = view.Entries.ToList().FindIndex(_ => _.Name == name);
        return view.CellColor(index, 1);
    }

    static Color NameColor(FolderView view, string name)
    {
        var index = view.Entries.ToList().FindIndex(_ => _.Name == name);
        return view.CellColor(index, 0);
    }

    // Outside the code folder, as the git actions aren't offered there, nothing is read.
    [Test]
    public async Task NoGitOutsideTheCodeFolder()
    {
        using var code = Repositories();
        using var view = await Open(code.Root);
        await GitRead(view);

        await Assert.That(view.ColumnTitles).DoesNotContain("Git");

        view.Navigate(code.At("Changed"));
        await Pump.Loaded(view);
        await GitRead(view);

        await Assert.That(view.GitText).IsEqualTo("");
    }

    // A clean repository, one with a file changed, and a folder that isn't a repository.
    static CodeCommandsTests.Scratch Repositories()
    {
        var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Clean");
        var changed = code.CreateRepo("Changed");
        File.WriteAllText(Path.Combine(changed, "tracked.txt"), "changed");
        Directory.CreateDirectory(code.At("Plain"));
        return code;
    }

    static Settings InCode(CodeCommandsTests.Scratch code) =>
        new()
        {
            ShowHidden = false,
            CodeDirectory = code.Root
        };

    static async Task GitRead(FolderView view)
    {
        await Pump.Until(() => view.GitReading.IsCompleted);
        await view.GitReading;
    }

    // Each row's name, and the mark after it.
    static List<string> Marks(FolderView view) =>
        view.Entries
            .Select((entry, index) => $"{entry.Name}: {view.MarkOf(index)}")
            .ToList();

    // Each row's name, and what its Git cell says.
    static List<string> GitCells(FolderView view) =>
        view.Entries
            .Select((entry, index) => $"{entry.Name}: {view.RowCells(index)[1]}")
            .ToList();

    // Each row's name, and what its Tag cell says, wherever that column is.
    static List<string> TagCells(FolderView view)
    {
        var shown = view.ColumnTitles.ToList().IndexOf("Tag");
        return view.Entries
            .Select((entry, index) => $"{entry.Name}: {view.RowCells(index)[shown]}")
            .ToList();
    }

    static string DescribeMenuItem(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        var text = item.Text!;
        if (item is ToolStripMenuItem {Checked: true})
        {
            text = $"[x] {text}";
        }

        if (!item.Enabled)
        {
            text += " (disabled)";
        }

        return text;
    }

    // A folder Windows marks hidden and system is left out, even with hidden items shown,
    // until system files are shown too.
    [Test]
    public async Task SystemFilesWhenShown()
    {
        using var fixture = Fixture.Create();
        var protectedFolder = Directory.CreateDirectory(fixture.At("Protected"));
        protectedFolder.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        var settings = new Settings {ShowHidden = true};
        using var view = await Open(fixture.Root, settings);
        var withoutSystem = view.Entries.Select(_ => _.Name).ToList();

        settings.ShowSystem = true;
        view.Reload();
        await Pump.Loaded(view);

        await Verify(
            new
            {
                withoutSystem,
                withSystem = view.Entries.Select(_ => _.Name).ToList()
            })
            .Snapshot(
                """
                {
                  withoutSystem: [
                    Archive,
                    Cache,
                    Docs,
                    Source,
                    hidden.aleph,
                    notes.aleph,
                    Report1.beta,
                    report2.beta,
                    report10.beta,
                    summary.gamma
                  ],
                  withSystem: [
                    Archive,
                    Cache,
                    Docs,
                    Protected,
                    Source,
                    hidden.aleph,
                    notes.aleph,
                    Report1.beta,
                    report2.beta,
                    report10.beta,
                    summary.gamma
                  ]
                }
                """);
    }

    static List<string> Selected(FolderView view) =>
        view.SelectedEntries().Select(_ => _.Name).ToList();

    static async Task<FolderView> Open(string path, Settings? settings = null)
    {
        // Hidden items off unless a test asks: most tests are about everything else.
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces);
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static object Describe(FolderView view, Fixture fixture) =>
        new
        {
            path = fixture.Relative(view.CurrentPath),
            status = view.StatusText,
            filter = view.Filter,
            rows = view.Entries.Select(_ => Row(_, view.CurrentPath)),
            selected = view.SelectedEntries().Select(_ => _.Name)
        };

    // Where an item a search found in a subfolder is, from the folder open.
    static string Row(FileEntry entry, string folder)
    {
        var kind = entry.IsDirectory ? "folder" : "file  ";
        var hidden = entry.IsHidden ? "  (hidden)" : "";
        var promoted = entry.Promoted ? "  (promoted)" : "";
        var size = entry.IsDirectory ? "" : $"  {entry.Size:N0} bytes";
        var parent = Path.GetDirectoryName(entry.FullPath)!;
        var where = "";
        if (!string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
        {
            where = $"  in {Path.GetRelativePath(folder, parent)}";
        }

        return $"{kind}  {entry.Name}{size}{hidden}{promoted}{where}";
    }
}
