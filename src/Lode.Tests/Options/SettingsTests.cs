public class SettingsTests
{
    // Renamed, moved or deleted while Lode wasn't running, the code folder is missed as Lode
    // starts, which then says so. Not one on a share, which would wait on the network.
    [Test]
    public async Task ACodeFolderNoLongerThereIsMissing()
    {
        using var fixture = Fixture.Create();
        var gone = fixture.At("Gone");

        await Assert.That(new Settings().MissingCodeDirectory()).IsNull();
        await Assert.That(new Settings {CodeDirectory = fixture.Root}.MissingCodeDirectory()).IsNull();
        await Assert.That(new Settings {CodeDirectory = gone}.MissingCodeDirectory()).IsEqualTo(gone);
        await Assert.That(new Settings {CodeDirectory = @"\\NotAServer\Share\Code"}.MissingCodeDirectory()).IsNull();
    }

    // Which pane shows for each repository, where the two are tabs, however its path is written;
    // Changes, which shows unless History is noted, isn't kept.
    [Test]
    public async Task PaneTabForEachRepository()
    {
        var settings = new Settings();
        await Assert.That(settings.PaneTabOf(@"D:\Code\Lode")).IsEqualTo(GitPane.Changes);

        settings.SetPaneTab(@"D:\Code\Lode", GitPane.History);
        settings.SetPaneTab(@"D:\Code\Other", GitPane.History);
        settings.SetPaneTab(@"d:\code\lode\", GitPane.History);

        await Assert.That(settings.PaneTabs.Count).IsEqualTo(2);
        await Assert.That(settings.PaneTabs[^1]).IsEqualTo(new(@"d:\code\lode\", GitPane.History));
        await Assert.That(settings.PaneTabOf(@"D:\Code\Lode")).IsEqualTo(GitPane.History);

        settings.SetPaneTab(@"D:\Code\Other", GitPane.Changes);
        await Assert.That(settings.PaneTabOf(@"D:\Code\Other")).IsEqualTo(GitPane.Changes);
        await Assert.That(settings.PaneTabs.Count).IsEqualTo(1);

        for (var index = 0; index < 600; index++)
        {
            settings.SetPaneTab($@"D:\Code\Repo{index}", GitPane.History);
        }

        await Assert.That(settings.PaneTabs.Count).IsEqualTo(500);
        await Assert.That(settings.PaneTabOf(@"D:\Code\Repo599")).IsEqualTo(GitPane.History);
        await Assert.That(settings.PaneTabOf(@"D:\Code\Lode")).IsEqualTo(GitPane.Changes);
    }

    // Which folders show in Large icons, however a path is written; Details, which every other
    // folder shows, isn't kept. The longest ago chosen go first past 500.
    [Test]
    public async Task LargeIconsForEachFolder()
    {
        var settings = new Settings();
        await Assert.That(settings.ShowsLargeIcons(@"D:\Pictures")).IsFalse();

        settings.SetLargeIcons(@"D:\Pictures", true);
        settings.SetLargeIcons(@"D:\Videos", true);
        settings.SetLargeIcons(@"d:\pictures\", true);

        await Assert.That(settings.LargeIconFolders).IsEquivalentTo([@"D:\Videos", @"d:\pictures\"], CollectionOrdering.Matching);
        await Assert.That(settings.ShowsLargeIcons(@"D:\Pictures")).IsTrue();

        settings.SetLargeIcons(@"D:\Videos", false);
        await Assert.That(settings.ShowsLargeIcons(@"D:\Videos")).IsFalse();
        await Assert.That(settings.LargeIconFolders.Count).IsEqualTo(1);

        for (var index = 0; index < 600; index++)
        {
            settings.SetLargeIcons($@"D:\Pictures\Album{index}", true);
        }

        await Assert.That(settings.LargeIconFolders.Count).IsEqualTo(500);
        await Assert.That(settings.ShowsLargeIcons(@"D:\Pictures\Album599")).IsTrue();
        await Assert.That(settings.ShowsLargeIcons(@"D:\Pictures")).IsFalse();
    }

    [Test]
    public async Task SaveWritesJson()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings", "settings.json");
        var settings = Settings.Load(path);
        settings.ShowHidden = true;
        settings.Left = 10;
        settings.Top = 20;
        settings.Width = 1200;
        settings.Height = 800;
        settings.TreeWidth = 250;
        settings.Tabs = [@"C:\Code", "This PC"];
        settings.TabsLastShown = [new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), new(2026, 11, 19, 17, 30, 0, TimeSpan.Zero)];
        settings.ActiveTab = 1;
        settings.RestartWindows =
        [
            new()
            {
                Left = 30,
                Top = 40,
                Width = 1000,
                Height = 700,
                Maximized = true,
                TreeWidth = 200,
                Tabs = [@"D:\Work"],
                TabsLastShown = [new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero)]
            }
        ];
        settings.ColumnWidths["Name"] = 420;
        settings.SetSort(@"C:\Code", "Date", ascending: false);
        settings.SetLargeIcons(@"C:\Code\Pictures", true);
        settings.GitHubEnterpriseHosts = ["github.contoso.com"];

        settings.Save();

        await VerifyFile(path)
            .Snapshot(
                """
                {
                  "ShowHidden": true,
                  "ShowSystem": false,
                  "Theme": "System",
                  "ShowRecent": true,
                  "ExpandToOpenFolder": true,
                  "Actions": {},
                  "HiddenColumns": [],
                  "GitColumnInRepository": false,
                  "ColumnWidths": {
                    "Name": 420
                  },
                  "FolderSorts": [
                    {
                      "Folder": "C:\\Code",
                      "Column": "Date",
                      "Ascending": false
                    }
                  ],
                  "LargeIconFolders": [
                    "C:\\Code\\Pictures"
                  ],
                  "PaneTabs": [],
                  "PaneWidth": 0,
                  "PaneSplit": 0,
                  "StripLabels": "Vertical",
                  "DiffPlacement": "Tab",
                  "DiffSplit": false,
                  "DiffHideWhitespace": false,
                  "AzureDevOpsOrganizations": [],
                  "GitHubEnterpriseHosts": [
                    "github.contoso.com"
                  ],
                  "CollapsedSections": [],
                  "PinnedFolders": [],
                  "RecentFolders": [],
                  "JumpListRemoved": [],
                  "DefaultDirectory": null,
                  "TextEditor": null,
                  "CodeDirectory": null,
                  "PurgePermanently": false,
                  "PurgeKeeps": [
                    ".env",
                    ".env.*",
                    ".vs/",
                    ".idea/",
                    "*.user"
                  ],
                  "FetchInBackground": true,
                  "FetchMinutes": 60,
                  "PromotedFiles": [],
                  "FindRegex": false,
                  "FindCaseSensitive": false,
                  "FindWholeWord": false,
                  "FindPreserveCase": false,
                  "FindContext": true,
                  "FindContextLines": 2,
                  "FindHidden": true,
                  "FindIgnored": false,
                  "SearchIgnored": false,
                  "FindHistory": [],
                  "WhereHistory": [],
                  "ReplaceHistory": [],
                  "CloseUnusedTabs": false,
                  "UnusedTabHours": 24,
                  "LastUpdateCheck": null,
                  "AvailableUpdate": null,
                  "Left": 10,
                  "Top": 20,
                  "Width": 1200,
                  "Height": 800,
                  "Maximized": false,
                  "TreeWidth": 250,
                  "Tabs": [
                    "C:\\Code",
                    "This PC"
                  ],
                  "TabsLastShown": [
                    "2026-11-19T09:00:00+00:00",
                    "2026-11-19T17:30:00+00:00"
                  ],
                  "ActiveTab": 1,
                  "RestartWindows": [
                    {
                      "Left": 30,
                      "Top": 40,
                      "Width": 1000,
                      "Height": 700,
                      "Maximized": true,
                      "TreeWidth": 200,
                      "Tabs": [
                        "D:\\Work"
                      ],
                      "TabsLastShown": [
                        "2026-11-19T12:00:00+00:00"
                      ],
                      "ActiveTab": 0
                    }
                  ]
                }
                """);
    }

    [Test]
    public async Task LoadReadsWhatSaveWrote()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var settings = Settings.Load(path);
        settings.Maximized = true;
        settings.Tabs = [@"D:\Work"];
        settings.RestartWindows =
        [
            new()
            {
                Width = 1000,
                Height = 700,
                Tabs = [@"C:\Code", "This PC"],
                ActiveTab = 1
            }
        ];
        settings.ColumnWidths["Type"] = 140;
        settings.SetSort(@"D:\Work", "Size", ascending: true);
        settings.GitHubEnterpriseHosts = ["github.contoso.com"];
        settings.Save();

        await Verify(Settings.Load(path))
            .Snapshot(
                """
                {
                  ShowHidden: true,
                  ShowSystem: false,
                  ShowRecent: true,
                  ExpandToOpenFolder: true,
                  GitColumnInRepository: false,
                  ColumnWidths: {
                    Type: 140
                  },
                  FolderSorts: [
                    {
                      Folder: D:\Work,
                      Column: Size,
                      Ascending: true
                    }
                  ],
                  DiffSplit: false,
                  DiffHideWhitespace: false,
                  GitHubEnterpriseHosts: [
                    github.contoso.com
                  ],
                  PurgePermanently: false,
                  PurgeKeeps: [
                    .env,
                    .env.*,
                    .vs/,
                    .idea/,
                    *.user
                  ],
                  FetchInBackground: true,
                  FetchMinutes: 60,
                  FindRegex: false,
                  FindCaseSensitive: false,
                  FindWholeWord: false,
                  FindPreserveCase: false,
                  FindContext: true,
                  FindContextLines: 2,
                  FindHidden: true,
                  FindIgnored: false,
                  SearchIgnored: false,
                  CloseUnusedTabs: false,
                  UnusedTabHours: 24,
                  Maximized: true,
                  Tabs: [
                    D:\Work
                  ],
                  RestartWindows: [
                    {
                      Width: 1000,
                      Height: 700,
                      Maximized: false,
                      Tabs: [
                        C:\Code,
                        This PC
                      ],
                      ActiveTab: 1
                    }
                  ]
                }
                """);
    }

    // What Git purge keeps loads as saved, emptied too, rather than as it is at first; settings
    // saved before Lode kept anything have it as it is at first.
    [Test]
    public async Task WhatPurgeKeepsLoadsAsSaved()
    {
        using var fixture = Fixture.Create();
        var changed = fixture.At("changed.json");
        var settings = Settings.Load(changed);
        settings.PurgeKeeps = [".env", "secrets/"];
        settings.Save();
        var emptied = fixture.At("emptied.json");
        settings = Settings.Load(emptied);
        settings.PurgeKeeps = [];
        settings.Save();
        var older = fixture.At("older.json");
        await File.WriteAllTextAsync(older, """{ "PurgePermanently": true }""");

        await Assert.That(Settings.Load(emptied).PurgeKeeps).IsEmpty();
        await Verify(
            new
            {
                changed = Settings.Load(changed).PurgeKeeps,
                older = Settings.Load(older).PurgeKeeps
            })
            .Snapshot(
                """
                {
                  changed: [
                    .env,
                    secrets/
                  ],
                  older: [
                    .env,
                    .env.*,
                    .vs/,
                    .idea/,
                    *.user
                  ]
                }
                """);
    }

    // Git was one action, a submenu of pull, push, discard and purge. Settings saved then have
    // each of those where Git was, nested under Git, so the menu and the bar are as they were.
    [Test]
    public async Task GitPlacementCarriesOverToItsActions()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, """{ "Actions": { "git": { "Button": true, "Menu": false } } }""");

        var settings = Settings.Load(path);

        await Verify(
            new
            {
                gitKept = settings.Actions.ContainsKey("git"),
                actions = LodeActions.GitIds.Select(_ => new
                {
                    id = _,
                    button = settings.ShowsAsButton(_),
                    menu = settings.ShowsInMenu(_),
                    buttonNested = settings.NestsButton(_),
                    menuNested = settings.NestsInMenu(_)
                })
            })
            .Snapshot(
                """
                {
                  gitKept: false,
                  actions: [
                    {
                      id: gitFetch,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitPull,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitSyncFork,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitSwitchBranch,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitSwitchToDefault,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitUpdateFromDefault,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitMerge,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitMergeInto,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitSquashMerge,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitRebase,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitPush,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitTag,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitDiscard,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    },
                    {
                      id: gitPurge,
                      button: true,
                      menu: false,
                      buttonNested: true,
                      menuNested: true
                    }
                  ]
                }
                """);
    }

    // The Button actions page and the bar's right-click menu change where an action shows on
    // the bar, and the Menu actions page where it shows in the right-click menus, each leaving
    // the other as it was.
    [Test]
    public async Task PlacingInOnePlaceLeavesTheOther()
    {
        var settings = new Settings();
        settings.SetPlacement("cut", button: true, menu: true, buttonNested: true, menuNested: true);
        object Cut() =>
            new
            {
                button = settings.ShowsAsButton("cut"),
                buttonNested = settings.NestsButton("cut"),
                menu = settings.ShowsInMenu("cut"),
                menuNested = settings.NestsInMenu("cut")
            };

        settings.SetMenuPlacement("cut", menu: false, nested: false);
        var menuPlaced = Cut();
        settings.SetButtonPlacement("cut", button: false, nested: false);

        await Verify(
            new
            {
                menuPlaced,
                buttonPlaced = Cut()
            })
            .Snapshot(
                """
                {
                  menuPlaced: {
                    button: true,
                    buttonNested: true,
                    menu: false,
                    menuNested: false
                  },
                  buttonPlaced: {
                    button: false,
                    buttonNested: false,
                    menu: false,
                    menuNested: false
                  }
                }
                """);
    }

    [Test]
    public async Task CorruptFileFallsBackToDefaults()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, "{ not json");

        await Verify(Settings.Load(path))
            .Snapshot(
                """
                {
                  ShowHidden: true,
                  ShowSystem: false,
                  ShowRecent: true,
                  ExpandToOpenFolder: true,
                  GitColumnInRepository: false,
                  DiffSplit: false,
                  DiffHideWhitespace: false,
                  PurgePermanently: false,
                  PurgeKeeps: [
                    .env,
                    .env.*,
                    .vs/,
                    .idea/,
                    *.user
                  ],
                  FetchInBackground: true,
                  FetchMinutes: 60,
                  FindRegex: false,
                  FindCaseSensitive: false,
                  FindWholeWord: false,
                  FindPreserveCase: false,
                  FindContext: true,
                  FindContextLines: 2,
                  FindHidden: true,
                  FindIgnored: false,
                  SearchIgnored: false,
                  CloseUnusedTabs: false,
                  UnusedTabHours: 24,
                  Maximized: false
                }
                """);
    }

    /// <summary>
    /// The first save would otherwise write the defaults over it, and every tab and option in it
    /// was gone without a word.
    /// </summary>
    [Test]
    public async Task CorruptFileIsKeptAsideAndSaid()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var settings = Settings.Load(path);
        settings.ShowHidden = true;
        settings.Save();

        var kept = Settings.UnreadablePath(path);
        await Assert.That(await File.ReadAllTextAsync(kept)).IsEqualTo("{ not json");
        await Assert.That(Settings.Load(path).ShowHidden).IsTrue();
        await Assert.That(settings.LoadProblem).Contains(kept);
        await Assert.That(settings.LoadBug).IsNull();
    }

    // Two copies of Lode on one file, as the one run as administrator and the one that isn't
    // are: each saves only what it has changed since it read or last wrote the file, so a theme
    // changed in one, and a code folder and pins in the other, survive each other's saves, and
    // the tabs saved after both, which change on their own every few seconds.
    [Test]
    public async Task TwoCopiesKeepEachOthersChanges()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var first = Settings.Load(path);
        first.TextEditor = @"C:\Tools\editor.exe";
        first.Save();
        var one = Settings.Load(path);
        var other = Settings.Load(path);

        one.Theme = Theme.Dark;
        one.Save();
        other.CodeDirectory = @"D:\Code";
        other.PinnedFolders = [@"D:\Code\Lode"];
        other.Save();
        one.Tabs = [@"C:\Work"];
        one.Save();
        other.ShowSystem = true;
        other.Save();

        var saved = Settings.Load(path);
        await Verify(
            new
            {
                saved.Theme,
                saved.TextEditor,
                saved.CodeDirectory,
                saved.PinnedFolders,
                saved.Tabs,
                saved.ShowSystem
            })
            .Snapshot(
                """
                {
                  Theme: Dark,
                  TextEditor: C:\Tools\editor.exe,
                  CodeDirectory: D:\Code,
                  PinnedFolders: [
                    D:\Code\Lode
                  ],
                  Tabs: [
                    C:\Work
                  ],
                  ShowSystem: true
                }
                """);
    }

    // Copies saving at once take turns, so none fails on another's half written file, and what
    // each changed is there once all have saved.
    [Test]
    public async Task SavesAtOnceAllGetThrough()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        Settings.Load(path).Save();
        var copies = Enumerable.Range(0, 6).Select(_ => Settings.Load(path)).ToList();
        using var start = new Barrier(copies.Count);

        await Task.WhenAll(
            copies.Select(
                (copy, index) => Task.Run(
                    () =>
                    {
                        start.SignalAndWait();
                        for (var save = 1; save <= 20; save++)
                        {
                            Change(copy, index, save);
                            copy.Save();
                        }
                    })));

        var saved = Settings.Load(path);
        await Assert.That(new[] { saved.PaneWidth, saved.FindContextLines, saved.UnusedTabHours, saved.Width, saved.Height, saved.TreeWidth })
            .IsEquivalentTo([20, 20, 20, 20, 20, 20], CollectionOrdering.Matching);
        await Assert.That(Directory.EnumerateFiles(fixture.Root, "*.writing")).IsEmpty();

        // Each copy a setting of its own.
        static void Change(Settings copy, int index, int value)
        {
            switch (index)
            {
                case 0:
                    copy.PaneWidth = value;
                    break;
                case 1:
                    copy.FindContextLines = value;
                    break;
                case 2:
                    copy.UnusedTabHours = value;
                    break;
                case 3:
                    copy.Width = value;
                    break;
                case 4:
                    copy.Height = value;
                    break;
                default:
                    copy.TreeWidth = value;
                    break;
            }
        }
    }

    // The copy run as administrator keeps its window's place and tabs apart, starting from the
    // other's, so neither opens with the other's tabs next time, whichever saved last, and so
    // are the windows it closed for an update with. Its options are the other's, and what
    // either changes of them the other has the next time it starts.
    [Test]
    public async Task TheAdministratorsWindowIsItsOwn()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var normal = Settings.Load(path);
        normal.Tabs = [@"C:\Work"];
        normal.Width = 1200;
        normal.TreeWidth = 250;
        normal.Save();

        var administrator = Settings.Load(path, administrator: true);
        var startedWith = administrator.Tabs.ToList();
        administrator.Tabs = [@"C:\Windows\System32"];
        administrator.Width = 900;
        administrator.TreeWidth = 300;
        administrator.Theme = Theme.Dark;
        administrator.RestartWindows = [new() { Tabs = [@"C:\Windows"] }];
        administrator.Save();
        normal.Tabs = [@"C:\Work", @"D:\Code"];
        normal.Save();

        var normalAgain = Settings.Load(path);
        var administratorAgain = Settings.Load(path, administrator: true);
        await Verify(
            new
            {
                startedWith,
                normal = new
                {
                    normalAgain.Tabs,
                    normalAgain.Width,
                    normalAgain.TreeWidth,
                    normalAgain.Theme,
                    restart = normalAgain.RestartWindows.Select(_ => _.Tabs)
                },
                administrator = new
                {
                    administratorAgain.Tabs,
                    administratorAgain.Width,
                    administratorAgain.TreeWidth,
                    administratorAgain.Theme,
                    restart = administratorAgain.RestartWindows.Select(_ => _.Tabs)
                },
                kept = Path.GetFileName(Settings.WindowPath(path))
            })
            .Snapshot(
                """
                {
                  startedWith: [
                    C:\Work
                  ],
                  normal: {
                    Tabs: [
                      C:\Work,
                      D:\Code
                    ],
                    Width: 1200,
                    TreeWidth: 250,
                    Theme: Dark,
                    restart: []
                  },
                  administrator: {
                    Tabs: [
                      C:\Windows\System32
                    ],
                    Width: 900,
                    TreeWidth: 300,
                    Theme: Dark,
                    restart: [
                      [
                        C:\Windows
                      ]
                    ]
                  },
                  kept: settings.administrator.json
                }
                """);
    }

    // An older Lode kept the tree's width in the pixels of its monitor, as SplitterDistance: in
    // the settings, in the windows an update brings back and in the administrator's own window
    // file. Each is read, for the window to open as wide, and none written again once the
    // window has saved its width at 100% scale.
    [Test]
    public async Task AnOlderLodesTreeWidthIsReadAndNotWrittenAgain()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, """{ "SplitterDistance": 375, "RestartWindows": [ { "SplitterDistance": 300 } ] }""");
        await File.WriteAllTextAsync(Settings.WindowPath(path), """{ "SplitterDistance": 450 }""");

        var normal = Settings.Load(path);
        var administrator = Settings.Load(path, administrator: true);
        var read = (normal.SplitterDistance, normal.RestartWindows.Single().SplitterDistance, administrator.SplitterDistance);
        normal.TreeWidth = 250;
        normal.SplitterDistance = null;
        normal.RestartWindows = [];
        normal.Save();
        administrator.TreeWidth = 300;
        administrator.SplitterDistance = null;
        administrator.Save();

        await Assert.That(read).IsEqualTo((375, 300, 450));
        await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("SplitterDistance");
        await Assert.That(await File.ReadAllTextAsync(Settings.WindowPath(path))).DoesNotContain("SplitterDistance");
        await Assert.That((Settings.Load(path).TreeWidth, Settings.Load(path, administrator: true).TreeWidth)).IsEqualTo((250, 300));
    }

    // Settings saved by a newer Lode, with a place for diffs, a theme, labels for the strip and
    // a pane this one doesn't know, as going back a version leaves them: those are their
    // defaults, and everything else is read, rather than the whole file set aside over them.
    [Test]
    public async Task ValuesANewerLodeAddedAreTheirDefaults()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "Theme": "Sepia",
              "StripLabels": "Upright",
              "DiffPlacement": "Sidebar",
              "PaneTabs": [
                { "Repo": "D:\\Code\\Lode", "Tab": "History" },
                { "Repo": "D:\\Code\\Other", "Tab": "Blame" }
              ],
              "CodeDirectory": "D:\\Code",
              "PinnedFolders": [ "D:\\Code\\Lode" ],
              "Actions": { "cut": { "Button": true, "Menu": false } },
              "Tabs": [ "C:\\Work" ]
            }
            """);

        var settings = Settings.Load(path);

        await Assert.That(settings.LoadProblem).IsNull();
        // As text, as Verify leaves out a value that is its type's default.
        await Verify(
            new
            {
                Theme = settings.Theme.ToString(),
                StripLabels = settings.StripLabels.ToString(),
                DiffPlacement = settings.DiffPlacement.ToString(),
                PaneTabs = settings.PaneTabs.Select(_ => new
                {
                    _.Repo,
                    Tab = _.Tab.ToString()
                }),
                settings.CodeDirectory,
                settings.PinnedFolders,
                cutIsAButton = settings.ShowsAsButton("cut"),
                settings.Tabs
            })
            .Snapshot(
                """
                {
                  Theme: System,
                  StripLabels: Vertical,
                  DiffPlacement: Tab,
                  PaneTabs: [
                    {
                      Repo: D:\Code\Lode,
                      Tab: History
                    },
                    {
                      Repo: D:\Code\Other,
                      Tab: Changes
                    }
                  ],
                  CodeDirectory: D:\Code,
                  PinnedFolders: [
                    D:\Code\Lode
                  ],
                  cutIsAButton: true,
                  Tabs: [
                    C:\Work
                  ]
                }
                """);
    }

    // An enum that may be null is null for a value it doesn't know, as for null; one in a list
    // is its default, the rest of the list read, whatever stood in its place. Each is written
    // by its name, as before.
    [Test]
    public async Task UnknownValuesInAListOrThatMayBeNull()
    {
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new LenientEnums()
            }
        };

        await Assert.That(JsonSerializer.Deserialize<Theme?>("\"Sepia\"", options)).IsNull();
        await Assert.That(JsonSerializer.Deserialize<Theme?>("null", options)).IsNull();
        await Assert.That(JsonSerializer.Deserialize<Theme?>("\"Dark\"", options)).IsEqualTo(Theme.Dark);
        await Assert.That(JsonSerializer.Deserialize<List<GitPane>>("""["History", "Blame", 7, "1", { "Tab": ["History"] }, "changes"]""", options))
            .IsEquivalentTo([GitPane.History, GitPane.Changes, GitPane.Changes, GitPane.History, GitPane.Changes, GitPane.Changes], CollectionOrdering.Matching);
        await Assert.That(JsonSerializer.Serialize<Theme?[]>([Theme.Dark, null], options)).IsEqualTo("""["Dark",null]""");
        await Assert.That(JsonSerializer.Serialize(new PaneTab(@"D:\Code", GitPane.History), options)).IsEqualTo("""{"Repo":"D:\\Code","Tab":"History"}""");
    }

    // NTFS logs a rename at once but writes a file's data when it gets round to it, so the
    // settings moved into place unflushed could be all zeros after the power went. Each save is
    // on the disk, whole, before it is put in place. Others' saves go through the flush too,
    // so only this test's file is noted.
    [Test]
    public async Task ASaveIsOnTheDiskBeforeItIsInPlace()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var settings = Settings.Load(path);
        settings.Tabs = [@"C:\Work"];
        var flushed = new List<(string File, long Length, bool InPlace)>();
        var flushToDisk = Settings.FlushToDisk;
        Settings.FlushToDisk = stream =>
        {
            flushToDisk(stream);
            if (stream.Name.StartsWith(fixture.Root, StringComparison.OrdinalIgnoreCase))
            {
                lock (flushed)
                {
                    flushed.Add((Path.GetFileName(stream.Name), stream.Length, File.Exists(path)));
                }
            }
        };
        try
        {
            settings.Save();
        }
        finally
        {
            Settings.FlushToDisk = flushToDisk;
        }

        await Assert.That(flushed).IsEquivalentTo([($"settings.json.{Environment.ProcessId}.writing", new FileInfo(path).Length, false)]);
    }

    // A file left as long as it was and all zeros, the power gone before its data was written,
    // can't be read, so the settings saved before it are, which each save keeps: the tabs a
    // few seconds older, rather than none at all and every option back to its default. The
    // file of zeros is kept aside, and the user told.
    [Test]
    public async Task AFileOfZerosLoadsTheSettingsSavedBeforeIt()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var settings = Settings.Load(path);
        settings.CodeDirectory = @"D:\Code";
        settings.Tabs = [@"C:\Work"];
        settings.Save();
        settings.Tabs = [@"C:\Work", @"D:\Code"];
        settings.Save();
        await File.WriteAllBytesAsync(path, new byte[new FileInfo(path).Length]);

        var loaded = Settings.Load(path);

        await Assert.That(loaded.Tabs).IsEquivalentTo([@"C:\Work"]);
        await Assert.That(loaded.CodeDirectory).IsEqualTo(@"D:\Code");
        await Assert.That(loaded.LoadProblem).Contains(Settings.BackupPath(path));
        await Assert.That(loaded.LoadBug).IsNull();
        await Assert.That(new FileInfo(Settings.UnreadablePath(path)).Length).IsGreaterThan(0);
    }

    // A repository link opened with Lode not running and its settings corrupt: the code folder
    // the link is looked for in is read as the start will read it, from the settings saved
    // before, or none, and the file left for the start, which sets it aside and says why, once,
    // as any start does. Read for the link by a load of its own, the file was set aside there,
    // without a word, and the start then found none, and began on the defaults, saying nothing
    // either. No box is put up meanwhile: one in a test fails it.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ALinkLeavesACorruptFileToTheStart(bool savedBefore)
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        if (savedBefore)
        {
            var settings = Settings.Load(path);
            settings.CodeDirectory = @"D:\Code";
            settings.Tabs = [@"C:\Work"];
            settings.Save();
            settings.Tabs = [@"C:\Work", @"D:\Code"];
            settings.Save();
        }

        await File.WriteAllTextAsync(path, "{ not json");

        var codeFolder = Settings.ReadQuietly(path)?.CodeDirectory;
        string? leftAsItWas = null;
        if (File.Exists(path))
        {
            leftAsItWas = await File.ReadAllTextAsync(path);
        }

        var keptAsideBeforeTheStart = File.Exists(Settings.UnreadablePath(path));
        var start = Settings.Load(path);

        await Assert.That(codeFolder).IsEqualTo(savedBefore ? @"D:\Code" : null);
        await Assert.That(leftAsItWas).IsEqualTo("{ not json");
        await Assert.That(keptAsideBeforeTheStart).IsFalse();
        await Assert.That(start.LoadProblem).Contains(Settings.UnreadablePath(path));
        await Assert.That(start.LoadBug).IsNull();
        await Assert.That(await File.ReadAllTextAsync(Settings.UnreadablePath(path))).IsEqualTo("{ not json");
        if (savedBefore)
        {
            await Assert.That(start.LoadProblem).Contains(Settings.BackupPath(path));
            await Assert.That(start.Tabs).IsEquivalentTo([@"C:\Work"]);
            await Assert.That(start.CodeDirectory).IsEqualTo(@"D:\Code");
        }
        else
        {
            await Assert.That(start.LoadProblem).Contains("the defaults");
        }
    }

    /// <summary>
    /// Locked by another program, say, and quite possibly fine: not saved over with the defaults.
    /// </summary>
    [Test]
    public async Task FileThatCannotBeOpenedIsNotSavedOver()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, """{ "ShowHidden": true }""");

        Settings settings;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = Settings.Load(path);
        }

        settings.Save();

        await Assert.That(settings.LoadProblem).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("""{ "ShowHidden": true }""");
    }
}
