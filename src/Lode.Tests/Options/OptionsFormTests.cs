[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class OptionsFormTests
{
    [Test]
    public async Task ReplaceThenRestore()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        form.ShowPage("File Explorer");
        var before = Describe(form);

        Click(form, "replaceExplorer");
        var replaced = Describe(form);
        Click(form, "restoreExplorer");

        await Verify(
            new
            {
                before,
                replaced,
                restored = Describe(form),
                registry = scratch.Dump()
            })
            .Snapshot(
                """
                {
                  before: {
                    status: Currently File Explorer opens them.,
                    replaceEnabled: true,
                    restoreEnabled: false
                  },
                  replaced: {
                    status: Currently Lode opens them.,
                    replaceEnabled: false,
                    restoreEnabled: true
                  },
                  restored: {
                    status: Currently File Explorer opens them.,
                    replaceEnabled: true,
                    restoreEnabled: false
                  }
                }
                """);
    }

    // Each kind of repository link is taken over, and reverted, on its own.
    [Test]
    public async Task TakeThenRevertRepositoryLinks()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        form.ShowPage("Links");
        var before = DescribeLinks(form);

        Click(form, "takeLinks.gitHubDesktop");
        var gitHubDesktop = DescribeLinks(form);
        Click(form, "takeLinks.visualStudio");
        var both = DescribeLinks(form);
        var registry = scratch.Dump();
        Click(form, "revertLinks.gitHubDesktop");
        Click(form, "revertLinks.visualStudio");

        await Verify(
            new
            {
                before,
                gitHubDesktop,
                both,
                registry,
                reverted = DescribeLinks(form),
                registryReverted = scratch.Dump()
            })
            .Snapshot(
                """
                {
                  before: [
                    {
                      status: GitHub's Open with GitHub Desktop: GitHub Desktop opens them.,
                      takeEnabled: true,
                      revertEnabled: false,
                      chooseEnabled: false
                    },
                    {
                      status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Visual Studio opens them.,
                      takeEnabled: true,
                      revertEnabled: false,
                      chooseEnabled: false
                    }
                  ],
                  gitHubDesktop: [
                    {
                      status: GitHub's Open with GitHub Desktop: Lode opens them. GitHub Desktop takes them back each time it starts, unless Lode is chosen for x-github-client links in Windows' Default apps.,
                      takeEnabled: false,
                      revertEnabled: true,
                      chooseEnabled: true
                    },
                    {
                      status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Visual Studio opens them.,
                      takeEnabled: true,
                      revertEnabled: false,
                      chooseEnabled: false
                    }
                  ],
                  both: [
                    {
                      status: GitHub's Open with GitHub Desktop: Lode opens them. GitHub Desktop takes them back each time it starts, unless Lode is chosen for x-github-client links in Windows' Default apps.,
                      takeEnabled: false,
                      revertEnabled: true,
                      chooseEnabled: true
                    },
                    {
                      status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Lode opens them.,
                      takeEnabled: false,
                      revertEnabled: true,
                      chooseEnabled: true
                    }
                  ],
                  registry: {
                    git-client\: {
                      (default): URL:git-client,
                      CreatedBy: Lode,
                      shell\: {
                        open\: {
                          command\: {
                            (default): "C:\Tools\lode.exe" "%1",
                            LodeCommand: "C:\Tools\lode.exe" "%1",
                            LodeForwardCommand: "C:\Apps\Visual Studio.exe" "%1"
                          }
                        }
                      },
                      TakenBy: Lode,
                      URL Protocol: 
                    },
                    Lode.RepositoryLink\: {
                      (default): Lode repository link,
                      shell\: {
                        open\: {
                          command\: {
                            (default): "C:\Tools\lode.exe" "%1"
                          }
                        }
                      }
                    },
                    x-github-client\: {
                      (default): URL:x-github-client,
                      CreatedBy: Lode,
                      shell\: {
                        open\: {
                          command\: {
                            (default): "C:\Tools\lode.exe" "%1",
                            LodeCommand: "C:\Tools\lode.exe" "%1",
                            LodeForwardCommand: "C:\Apps\GitHub Desktop.exe" "%1"
                          }
                        }
                      },
                      TakenBy: Lode,
                      URL Protocol: 
                    }
                  },
                  reverted: [
                    {
                      status: GitHub's Open with GitHub Desktop: GitHub Desktop opens them.,
                      takeEnabled: true,
                      revertEnabled: false,
                      chooseEnabled: false
                    },
                    {
                      status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Visual Studio opens them.,
                      takeEnabled: true,
                      revertEnabled: false,
                      chooseEnabled: false
                    }
                  ]
                }
                """);
    }

    // GitHub Desktop took its links back as it started: it opens them, not another copy of Lode,
    // and they can be opened in Lode again.
    [Test]
    public async Task RepositoryLinksTakenBack()
    {
        using var scratch = new RegistryScratch();
        scratch.Links().Take(RepositoryLinks.GitHubDesktop);
        scratch.Set(@"x-github-client\shell\open\command", null, "\"C:\\Apps\\GitHub Desktop.exe\" --protocol-launcher \"%1\"");
        using var form = Show(scratch.Replacement());
        form.ShowPage("Links");

        await Verify(DescribeLinks(form))
            .Snapshot(
                """
                [
                  {
                    status: GitHub's Open with GitHub Desktop: GitHub Desktop opens them. It takes them back from Lode each time it starts, unless Lode is chosen for x-github-client links in Windows' Default apps.,
                    takeEnabled: true,
                    revertEnabled: true,
                    chooseEnabled: true
                  },
                  {
                    status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Visual Studio opens them.,
                    takeEnabled: true,
                    revertEnabled: false,
                    chooseEnabled: false
                  }
                ]
                """);
    }

    // Lode chosen for GitHub Desktop's links in Default apps: they stay with Lode, whatever
    // GitHub Desktop writes as it starts, and there is nothing more to choose. Visual Studio's,
    // taken but not chosen, can still be chosen, on Lode's page of Default apps.
    [Test]
    public async Task RepositoryLinksChosenInDefaultApps()
    {
        using var scratch = new RegistryScratch();
        var links = scratch.Links();
        links.Take(RepositoryLinks.GitHubDesktop);
        links.Take(RepositoryLinks.VisualStudio);
        scratch.Choose("x-github-client", "Lode.RepositoryLink");
        scratch.Set(@"x-github-client\shell\open\command", null, "\"C:\\Apps\\GitHub Desktop.exe\" --protocol-launcher \"%1\"");
        using var form = Show(scratch.Replacement());
        form.ShowPage("Links");
        var shown = DescribeLinks(form);

        Click(form, "chooseLinks.visualStudio");

        await Assert.That(form.SettingsOpened).IsEquivalentTo(["ms-settings:defaultapps?registeredAppUser=Lode"]);
        await Verify(shown)
            .Snapshot(
                """
                [
                  {
                    status: GitHub's Open with GitHub Desktop: Lode opens them. Lode is chosen for x-github-client links in Windows' Default apps, so they stay with it.,
                    takeEnabled: false,
                    revertEnabled: true,
                    chooseEnabled: false
                  },
                  {
                    status: GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Lode opens them.,
                    takeEnabled: false,
                    revertEnabled: true,
                    chooseEnabled: true
                  }
                ]
                """);
    }

    // Visual Studio chosen for its links in Default apps comes ahead of Lode's command, and is
    // named as Visual Studio registers itself.
    [Test]
    public async Task RepositoryLinksWithAnotherAppChosen()
    {
        using var scratch = new RegistryScratch();
        scratch.RegisterApp("VisualStudio.e6eba60c", "git-client", "VisualStudio.git-client.e6eba60c", "Microsoft Visual Studio");
        scratch.Links().Take(RepositoryLinks.VisualStudio);
        scratch.Choose("git-client", "VisualStudio.git-client.e6eba60c");
        using var form = Show(scratch.Replacement());
        form.ShowPage("Links");

        await Assert.That(form.Controls.Find("linkStatus.visualStudio", true)[0].Text)
            .IsEqualTo("GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio: Lode opens them. But Windows' Default apps has Microsoft Visual Studio chosen for git-client links, which comes first, so they still open there.");
    }

    static object DescribeLinks(Form form) =>
        RepositoryLinks.All.Select(_ => new
            {
                status = form.Controls.Find($"linkStatus.{_.Id}", true)[0].Text,
                takeEnabled = form.Controls.Find($"takeLinks.{_.Id}", true)[0].Enabled,
                revertEnabled = form.Controls.Find($"revertLinks.{_.Id}", true)[0].Enabled,
                chooseEnabled = form.Controls.Find($"chooseLinks.{_.Id}", true)[0].Enabled
            })
            .ToList();

    // Folders point at a copy of Lode installed somewhere else. Replace stays available, to
    // point them at this copy instead.
    [Test]
    public async Task AnotherCopyIsRegistered()
    {
        using var scratch = new RegistryScratch();
        scratch.Replacement(
            """
            "D:\Old\Lode.exe"
            """).Replace();
        using var form = Show(scratch.Replacement());
        form.ShowPage("File Explorer");
        var before = Describe(form);

        Click(form, "replaceExplorer");

        await Verify(
            new
            {
                before,
                replaced = Describe(form)
            })
            .Snapshot(
                """
                {
                  before: {
                    status: Currently another copy of Lode opens them: "D:\Old\Lode.exe",
                    replaceEnabled: true,
                    restoreEnabled: true
                  },
                  replaced: {
                    status: Currently Lode opens them.,
                    replaceEnabled: false,
                    restoreEnabled: true
                  }
                }
                """);
    }

    // Folders point at this copy, but from a Lode that did not yet add the explore verb.
    // Replace is available again, to add it.
    [Test]
    public async Task AnOlderReplaceIsIncomplete()
    {
        using var scratch = new RegistryScratch();
        scratch.OlderReplace(scratch.Replacement());
        using var form = Show(scratch.Replacement());
        form.ShowPage("File Explorer");
        var before = Describe(form);

        Click(form, "replaceExplorer");

        await Verify(
            new
            {
                before,
                replaced = Describe(form)
            })
            .Snapshot(
                """
                {
                  before: {
                    status: Currently Lode opens them, except from apps that ask for File Explorer itself, such as GitHub Desktop. Replace again to cover those.,
                    replaceEnabled: true,
                    restoreEnabled: true
                  },
                  replaced: {
                    status: Currently Lode opens them.,
                    replaceEnabled: false,
                    restoreEnabled: true
                  }
                }
                """);
    }

    // Each action has a tick for the actions bar, on the Button actions page, and one for the
    // right-click menus, on the Menu actions page.
    [Test]
    public async Task TickingAnActionChangesWhereItShows()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = new
        {
            openButton = settings.ShowsAsButton("open"),
            cutMenu = settings.ShowsInMenu("cut")
        };

        ((CheckBox) form.Controls.Find("button.open", true)[0]).Checked = true;
        ((CheckBox) form.Controls.Find("menu.cut", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = new
                {
                    openButton = settings.ShowsAsButton("open"),
                    cutMenu = settings.ShowsInMenu("cut"),
                    // Cut's button tick is untouched.
                    cutButton = settings.ShowsAsButton("cut")
                },
                changes
            })
            .Snapshot(
                """
                {
                  before: {
                    openButton: false,
                    cutMenu: true
                  },
                  after: {
                    openButton: true,
                    cutMenu: false,
                    cutButton: true
                  },
                  changes: [
                    Actions,
                    Actions
                  ]
                }
                """);
    }

    // A Nested box nests the action under its parent there. It is greyed out while the action
    // doesn't show there, keeping what it says, and only an action with a parent has one.
    [Test]
    public async Task TickingNestedNestsTheAction()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        CheckBox Box(string name) => (CheckBox) form.Controls.Find(name, true)[0];
        var before = new
        {
            cutButtonNested = settings.NestsButton("cut"),
            terminalMenuNested = settings.NestsInMenu("terminal"),
            openButtonNestedEnabled = Box("buttonNested.open").Enabled
        };

        Box("buttonNested.cut").Checked = true;
        Box("menuNested.terminal").Checked = false;
        Box("button.cut").Checked = false;

        await Verify(
            new
            {
                before,
                after = new
                {
                    cutButtonNested = settings.NestsButton("cut"),
                    cutButtonNestedEnabled = Box("buttonNested.cut").Enabled,
                    terminalMenuNested = settings.NestsInMenu("terminal"),
                    // Terminal (admin)'s nesting is untouched.
                    terminalAdminMenuNested = settings.NestsInMenu("terminalAdmin")
                },
                newHasNestedBoxes = form.Controls.Find("buttonNested.new", true).Length > 0
            })
            .Snapshot(
                """
                {
                  before: {
                    cutButtonNested: false,
                    terminalMenuNested: true,
                    openButtonNestedEnabled: false
                  },
                  after: {
                    cutButtonNested: true,
                    cutButtonNestedEnabled: false,
                    terminalMenuNested: false,
                    terminalAdminMenuNested: true
                  },
                  newHasNestedBoxes: false
                }
                """);
    }

    // Reset on the Button actions page puts every action on the bar back as it was at first, its
    // ticks with it, leaving the right-click menus as they are, and Reset on the Menu actions
    // page does the same for the menus. Each is greyed out while there is nothing to put back,
    // and a placement put back is forgotten, as one never changed is.
    [Test]
    public async Task ResetPutsTheActionsBack()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        CheckBox Box(string name) => (CheckBox) form.Controls.Find(name, true)[0];
        Button Reset(string name) => (Button) form.Controls.Find(name, true)[0];
        object State() =>
            new
            {
                buttonReset = Reset("buttonReset").Enabled,
                menuReset = Reset("menuReset").Enabled,
                openButton = Box("button.open").Checked,
                cutButtonNested = Box("buttonNested.cut").Checked,
                cutMenu = Box("menu.cut").Checked,
                changed = settings.Actions.Keys.Order().ToList()
            };
        var opened = State();
        Box("button.open").Checked = true;
        Box("buttonNested.cut").Checked = true;
        Box("menu.cut").Checked = false;
        var ticked = State();
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;

        form.ShowPage("Button actions");
        Reset("buttonReset").PerformClick();
        var buttonsReset = State();
        form.ShowPage("Menu actions");
        Reset("menuReset").PerformClick();

        await Verify(
            new
            {
                opened,
                ticked,
                buttonsReset,
                menusReset = State(),
                changes
            })
            .Snapshot(
                """
                {
                  opened: {
                    buttonReset: false,
                    menuReset: false,
                    openButton: false,
                    cutButtonNested: false,
                    cutMenu: true
                  },
                  ticked: {
                    buttonReset: true,
                    menuReset: true,
                    openButton: true,
                    cutButtonNested: true,
                    cutMenu: false,
                    changed: [
                      cut,
                      open
                    ]
                  },
                  buttonsReset: {
                    buttonReset: false,
                    menuReset: true,
                    openButton: false,
                    cutButtonNested: false,
                    cutMenu: false,
                    changed: [
                      cut
                    ]
                  },
                  menusReset: {
                    buttonReset: false,
                    menuReset: false,
                    openButton: false,
                    cutButtonNested: false,
                    cutMenu: true
                  },
                  changes: [
                    Actions,
                    Actions
                  ]
                }
                """);
    }

    // The actions scroll under their column headings, which stay put above them, each over its
    // column.
    [Test]
    public async Task ActionHeadingsStayAsTheActionsScroll()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        form.ShowPage("Button actions");
        static IEnumerable<Control> All(Control control) =>
            control.Controls.Cast<Control>().SelectMany(_ => All(_).Prepend(_));
        static Rectangle OnScreen(Control control) =>
            control.RectangleToScreen(control.ClientRectangle);
        var heading = All(form).Single(_ => _ is Label {Text: "Show", Visible: true});
        var cut = form.Controls.Find("button.cut", true)[0];
        var rows = (ScrollableControl) cut.Parent!.Parent!;
        var headingBefore = OnScreen(heading);
        var cutBefore = OnScreen(cut);

        rows.AutoScrollPosition = new(0, rows.DisplayRectangle.Height);
        Application.DoEvents();

        await Assert.That(OnScreen(cut).Top).IsLessThan(cutBefore.Top);
        await Assert.That(OnScreen(heading)).IsEqualTo(headingBefore);
        await Assert.That(headingBefore.Bottom).IsLessThanOrEqualTo(OnScreen(rows).Top);
        // Centred over its column, as the boxes in it are.
        var headingMiddle = headingBefore.Left + headingBefore.Width / 2;
        var cutMiddle = cutBefore.Left + cutBefore.Width / 2;
        await Assert.That(Math.Abs(headingMiddle - cutMiddle)).IsLessThanOrEqualTo(1);
    }

    // Only with a code folder set. Excluding asks first, then hands over the folder and says
    // how it went; the real exclusion never runs in a test.
    [Test]
    public async Task ExcludeCodeFolderFromDefender()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.ShowPage("Code");
        var button = (Button) form.Controls.Find("excludeFromDefender", true)[0];
        var enabledWithoutFolder = button.Enabled;

        form.BrowseForCodeDirectory = () => @"D:\Code";
        form.ConfirmCodeDirectory = _ => true;
        ((Button) form.Controls.Find("browseCodeDirectory", true)[0]).PerformClick();
        var enabledWithFolder = button.Enabled;

        var events = new List<string>();
        var answer = false;
        form.ConfirmDefenderExclusion = folder =>
        {
            events.Add($"asked about {folder}");
            return answer;
        };
        form.ExcludeFromDefender = folder =>
        {
            events.Add($"excluded {folder}");
            return Task.FromResult(new DefenderExclusion.Outcome(true, false, [@"D:\Code\Lode", @"D:\Code\Lode\bin"], null));
        };
        form.ReportDefenderExclusion = (folder, outcome) =>
            events.Add($"reported {folder}: removed {string.Join(", ", outcome.Removed)}");

        // Turned down: nothing is excluded.
        button.PerformClick();
        Application.DoEvents();
        answer = true;
        button.PerformClick();
        Application.DoEvents();

        await Verify(
            new
            {
                enabledWithoutFolder,
                enabledWithFolder,
                events
            })
            .Snapshot(
                """
                {
                  enabledWithoutFolder: false,
                  enabledWithFolder: true,
                  events: [
                    asked about D:\Code,
                    asked about D:\Code,
                    excluded D:\Code,
                    reported D:\Code: removed D:\Code\Lode, D:\Code\Lode\bin
                  ]
                }
                """);
    }

    // Off, then on again, each asking first and only once said yes to, the buttons following
    // how the service is after each.
    [Test]
    public async Task TurnWindowsSearchOffThenOn()
    {
        using var scratch = new RegistryScratch();
        var service = new WindowsSearchStub();
        using var form = Show(scratch.Replacement(), windowsSearch: service);
        form.ShowPage("Search");
        var before = DescribeWindowsSearch(form);

        var events = new List<string>();
        var answer = false;
        form.ConfirmWindowsSearch = on =>
        {
            events.Add($"asked to turn {(on ? "on" : "off")}");
            return answer;
        };
        form.ChangeWindowsSearch = on =>
        {
            events.Add($"turned {(on ? "on" : "off")}");
            service.State = on
                ? new(WindowsSearchService.Startup.DelayedAutomatic, true)
                : new(WindowsSearchService.Startup.Disabled, false);
            return Task.FromResult(new Elevated.Outcome(true, false, [], null));
        };
        form.ReportWindowsSearch = (on, outcome) =>
            events.Add($"reported turning {(on ? "on" : "off")}: {outcome.Succeeded}");

        // Turned down: nothing changes.
        Click(form, "turnOffWindowsSearch");
        answer = true;
        Click(form, "turnOffWindowsSearch");
        var off = DescribeWindowsSearch(form);
        Click(form, "turnOnWindowsSearch");

        await Verify(
            new
            {
                before,
                off,
                on = DescribeWindowsSearch(form),
                events
            })
            .Snapshot(
                """
                {
                  before: {
                    status: Windows Search is running, and starts shortly after Windows does.,
                    turnOffEnabled: true,
                    turnOnEnabled: false
                  },
                  off: {
                    status: Windows Search is off.,
                    turnOffEnabled: false,
                    turnOnEnabled: true
                  },
                  on: {
                    status: Windows Search is running, and starts shortly after Windows does.,
                    turnOffEnabled: true,
                    turnOnEnabled: false
                  },
                  events: [
                    asked to turn off,
                    asked to turn off,
                    turned off,
                    reported turning off: True,
                    asked to turn on,
                    turned on,
                    reported turning on: True
                  ]
                }
                """);
    }

    static object DescribeWindowsSearch(Form form) =>
        new
        {
            status = form.Controls.Find("windowsSearchStatus", true)[0].Text,
            turnOffEnabled = form.Controls.Find("turnOffWindowsSearch", true)[0].Enabled,
            turnOnEnabled = form.Controls.Find("turnOnWindowsSearch", true)[0].Enabled
        };

    [Test]
    public async Task UntickingShowRecentChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = settings.ShowRecent;

        ((CheckBox) form.Controls.Find("showRecent", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ShowRecent,
                changes
            })
            .Snapshot(
                """
                {
                  before: true,
                  after: false,
                  changes: [
                    Places
                  ]
                }
                """);
    }

    // On by default, unlike Explorer's Expand to open folder.
    [Test]
    public async Task UntickingExpandToOpenFolderChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = settings.ExpandToOpenFolder;

        ((CheckBox) form.Controls.Find("expandToOpenFolder", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ExpandToOpenFolder,
                changes
            })
            .Snapshot(
                """
                {
                  before: true,
                  after: false,
                  changes: [
                    TreeSelection
                  ]
                }
                """);
    }

    // Off by default, so purged files go to the Recycle Bin.
    [Test]
    public async Task TickingPurgePermanentlyChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var before = settings.PurgePermanently;

        ((CheckBox) form.Controls.Find("purgePermanently", true)[0]).Checked = true;

        await Verify(
            new
            {
                before,
                after = settings.PurgePermanently
            })
            .Snapshot(
                """
                {
                  before: false,
                  after: true
                }
                """);
    }

    // What Git purge keeps shows as set, a space between patterns, and changes as each key is
    // typed, however many spaces are between them; emptied, purge keeps nothing.
    [Test]
    public async Task TypingWhatPurgeKeepsChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var box = (TextBox) form.Controls.Find("purgeKeeps", true)[0];
        var shown = box.Text;

        box.Text = "  .env   secrets/ *.db ";
        var typed = settings.PurgeKeeps.ToList();
        box.Text = "";

        await Assert.That(shown).IsEqualTo(".env .env.* .vs/ .idea/ *.user");
        await Assert.That(typed).IsEquivalentTo([".env", "secrets/", "*.db"], CollectionOrdering.Matching);
        await Assert.That(settings.PurgeKeeps).IsEmpty();
    }

    // Off by default, with the hours greyed out until it is ticked.
    [Test]
    public async Task TickingCloseUnusedTabsChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var tick = (CheckBox) form.Controls.Find("closeUnusedTabs", true)[0];
        var hours = (NumericUpDown) form.Controls.Find("unusedTabHours", true)[0];
        var before = new
        {
            settings.CloseUnusedTabs,
            settings.UnusedTabHours,
            hoursEnabled = hours.Enabled
        };

        tick.Checked = true;
        hours.Value = 8;

        await Verify(
            new
            {
                before,
                after = new
                {
                    settings.CloseUnusedTabs,
                    settings.UnusedTabHours,
                    hoursEnabled = hours.Enabled
                }
            })
            .Snapshot(
                """
                {
                  before: {
                    CloseUnusedTabs: false,
                    UnusedTabHours: 24,
                    hoursEnabled: false
                  },
                  after: {
                    CloseUnusedTabs: true,
                    UnusedTabHours: 8,
                    hoursEnabled: true
                  }
                }
                """);
    }

    // On by default, hourly, as GitHub Desktop fetches; unticked, the minutes are greyed out,
    // and the background fetch, which reads the settings at each tick, stops.
    [Test]
    public async Task UntickingFetchInBackgroundChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var tick = (CheckBox) form.Controls.Find("fetchInBackground", true)[0];
        var minutes = (NumericUpDown) form.Controls.Find("fetchMinutes", true)[0];
        var before = new
        {
            settings.FetchInBackground,
            settings.FetchMinutes,
            minutesEnabled = minutes.Enabled
        };

        minutes.Value = 15;
        tick.Checked = false;

        await Verify(
            new
            {
                before,
                after = new
                {
                    settings.FetchInBackground,
                    settings.FetchMinutes,
                    minutesEnabled = minutes.Enabled
                }
            })
            .Snapshot(
                """
                {
                  before: {
                    FetchInBackground: true,
                    FetchMinutes: 60,
                    minutesEnabled: true
                  },
                  after: {
                    FetchInBackground: false,
                    FetchMinutes: 15,
                    minutesEnabled: false
                  }
                }
                """);
    }

    // Same as Windows by default. Picking another is kept for the next start, which is when it
    // takes effect.
    [Test]
    public async Task PickingAThemeChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var theme = (ComboBox) form.Controls.Find("theme", true)[0];
        var before = new
        {
            settings.Theme,
            shown = theme.SelectedItem
        };

        theme.SelectedIndex = (int) Theme.Dark;

        await Verify(
            new
            {
                before,
                after = settings.Theme
            })
            .Snapshot(
                """
                {
                  before: {
                    shown: Same as Windows
                  },
                  after: Dark
                }
                """);
    }

    // The setting changes as each key is typed, one pattern a line, blank lines left out, but
    // the windows read their folders again only once, as Options closes.
    [Test]
    public async Task TypingPromotedFilesChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var box = (TextBox) form.Controls.Find("promotedFiles", true)[0];

        box.Text = "*.slnx";
        box.Text = "*.slnx\r\n\r\n  Directory.*.props  ";
        var changesWhileTyping = changes.Count;
        form.Close();

        await Assert.That(changesWhileTyping).IsEqualTo(0);
        await Verify(
            new
            {
                patterns = settings.PromotedFiles,
                changes
            })
            .Snapshot(
                """
                {
                  patterns: [
                    *.slnx,
                    Directory.*.props
                  ],
                  changes: [
                    Listing
                  ]
                }
                """);
    }

    // Opened again, the box has the patterns, and closing without a change reads nothing again.
    [Test]
    public async Task PromotedFilesShowWhatIsSet()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings {PromotedFiles = ["*.slnx", "Directory.*.props"]};
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;

        var text = form.Controls.Find("promotedFiles", true)[0].Text;
        form.Close();

        await Assert.That(text).IsEqualTo($"*.slnx{Environment.NewLine}Directory.*.props");
        await Assert.That(changes).IsEmpty();
    }

    // Saved as the dialog closes, not only when the last window does.
    [Test]
    public async Task ClosingSavesTheSettings()
    {
        using var scratch = new RegistryScratch();
        var path = Path.Combine(Path.GetTempPath(), "LodeTests", $"settings.{Guid.NewGuid():N}.json");
        try
        {
            var settings = Settings.Load(path);
            using var form = Show(scratch.Replacement(), settings);
            ((CheckBox) form.Controls.Find("showHidden", true)[0]).Checked = false;
            var savedWhileOpen = File.Exists(path);

            form.Close();

            await Verify(
                new
                {
                    savedWhileOpen,
                    reloaded = Settings.Load(path).ShowHidden
                })
                .Snapshot(
                    """
                    {
                      savedWhileOpen: false,
                      reloaded: false
                    }
                    """);
        }
        finally
        {
            File.Delete(path);
            File.Delete(Settings.BackupPath(path));
        }
    }

    [Test]
    public async Task UntickingShowHiddenChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = settings.ShowHidden;

        ((CheckBox) form.Controls.Find("showHidden", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ShowHidden,
                changes
            })
            .Snapshot(
                """
                {
                  before: true,
                  after: false,
                  changes: [
                    Listing
                  ]
                }
                """);
    }

#if DEBUG
    // Pixels depend on the Windows version, theme and fonts of the machine that captured them,
    // so, like MainFormTests, these only run in Debug builds. A capture of each page; File
    // Explorer's in both states.
    [Test]
    public Task GeneralPage() =>
        VerifyPage(
            "General",
            new()
            {
                DefaultDirectory = @"D:\Documents",
                TextEditor = editors[0].Path
            });

    [Test]
    public Task ViewPage() =>
        VerifyPage("View", new());

    [Test]
    public Task ButtonActionsPage() =>
        VerifyPage("Button actions", new());

    [Test]
    public Task MenuActionsPage() =>
        VerifyPage("Menu actions", new());

    [Test]
    public Task CodePage() =>
        VerifyPage("Code", new() {CodeDirectory = @"C:\Code"});

    [Test]
    public Task PromotedFilesPage() =>
        VerifyPage("Promoted files", new() {PromotedFiles = ["*.slnx", "Directory.*.props"]});

    [Test]
    public Task SearchPage() =>
        VerifyPage("Search", new());

    [Test]
    public Task ExplorerInCharge() =>
        VerifyPage("File Explorer", new());

    [Test]
    public Task LodeInCharge() =>
        VerifyPage("File Explorer", new(), replace: true);

    [Test]
    public Task LinksPage() =>
        VerifyPage("Links", new());

    [Test]
    public Task UpdatesPage() =>
        VerifyPage(
            "Updates",
            new(),
            updates: TestUpdates.Installed(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));

    static async Task VerifyPage(string page, Settings settings, bool replace = false, Updates? updates = null)
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        if (replace)
        {
            replacement.Replace();
        }

        using var form = Show(replacement, settings, updates);
        form.ShowPage(page);
        form.Refresh();
        Application.DoEvents();
        await Verify(form);
    }
#endif

    // Picking a page in the list shows it and hides the rest; Ctrl+Tab and Ctrl+Shift+Tab go
    // round them.
    [Test]
    public async Task PickingAPageShowsIt()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        var list = (PageList) form.Controls.Find("pages", true)[0];
        var opened = Shown(form);

        // The last but one, so going on twice comes round to the first.
        list.SelectedIndex = list.Count - 2;
        var picked = Shown(form);
        form.Press(Keys.Control | Keys.Tab);
        var next = Shown(form);
        form.Press(Keys.Control | Keys.Tab);
        var wrapped = Shown(form);
        form.Press(Keys.Control | Keys.Shift | Keys.Tab);

        await Verify(
            new
            {
                pages = Enumerable.Range(0, list.Count).Select(list.PageText),
                opened,
                picked,
                next,
                wrapped,
                back = Shown(form)
            })
            .Snapshot(
                """
                {
                  pages: [
                    General,
                    View,
                    Button actions,
                    Menu actions,
                    Search,
                    Code,
                    Promoted files,
                    File Explorer,
                    Links,
                    Updates
                  ],
                  opened: General,
                  picked: Links,
                  next: Updates,
                  wrapped: General,
                  back: Updates
                }
                """);
    }

    // What shows: the code folder box is on the Code page, the tick for hidden items on View.
    static string Shown(Form form)
    {
        if (form.Controls.Find("updateStatus", true)[0].Visible)
        {
            return "Updates";
        }

        if (form.Controls.Find("codeDirectory", true)[0].Visible)
        {
            return "Code";
        }

        if (form.Controls.Find("linkStatus.gitHubDesktop", true)[0].Visible)
        {
            return "Links";
        }

        if (form.Controls.Find("explorerStatus", true)[0].Visible)
        {
            return "File Explorer";
        }

        if (form.Controls.Find("showHidden", true)[0].Visible)
        {
            return "View";
        }

        if (form.Controls.Find("button.cut", true)[0].Visible)
        {
            return "Button actions";
        }

        if (form.Controls.Find("menu.cut", true)[0].Visible)
        {
            return "Menu actions";
        }

        if (form.Controls.Find("textEditor", true)[0].Visible)
        {
            return "General";
        }

        return "none";
    }

    [Test]
    public async Task ChoosingAnEditorChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var before = Editors(form);

        EditorBox(form).SelectedIndex = 1;

        await Verify(
            new
            {
                before,
                setting = settings.TextEditor
            })
            .Snapshot(
                """
                {
                  before: [
                    Visual Studio Code   <- selected,
                    Notepad,
                    Browse…
                  ],
                  setting: C:\Windows\notepad.exe
                }
                """);
    }

    [Test]
    public async Task BrowsingAddsTheEditor()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.BrowseForEditor = () => @"C:\Tools\MyEditor.exe";

        SelectBrowse(form);

        await Verify(
            new
            {
                after = Editors(form),
                setting = settings.TextEditor
            })
            .Snapshot(
                """
                {
                  after: [
                    Visual Studio Code,
                    Notepad,
                    MyEditor   <- selected,
                    Browse…
                  ],
                  setting: C:\Tools\MyEditor.exe
                }
                """);
    }

    [Test]
    public async Task CancellingBrowseKeepsTheEditor()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings {TextEditor = editors[1].Path};
        using var form = Show(scratch.Replacement(), settings);
        form.BrowseForEditor = () => null;

        SelectBrowse(form);

        await Verify(
            new
            {
                after = Editors(form),
                setting = settings.TextEditor
            })
            .Snapshot(
                """
                {
                  after: [
                    Visual Studio Code,
                    Notepad   <- selected,
                    Browse…
                  ],
                  setting: C:\Windows\notepad.exe
                }
                """);
    }

    [Test]
    public async Task ChoosingAndClearingTheCodeFolder()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.ShowPage("Code");
        form.BrowseForCodeDirectory = () => @"C:\Code";

        // Asked first, as Lode watches the folder: not used when the answer is no.
        var asked = new List<string>();
        var answer = false;
        form.ConfirmCodeDirectory = folder =>
        {
            asked.Add(folder);
            return answer;
        };
        Click(form, "browseCodeDirectory");
        var declined = settings.CodeDirectory;
        answer = true;
        Click(form, "browseCodeDirectory");

        // The one it already is isn't asked about again.
        Click(form, "browseCodeDirectory");
        var chosen = new
        {
            setting = settings.CodeDirectory,
            shown = form.Controls.Find("codeDirectory", true)[0].Text,
            clearEnabled = form.Controls.Find("clearCodeDirectory", true)[0].Enabled
        };
        Click(form, "clearCodeDirectory");

        await Verify(
            new
            {
                asked,
                declined,
                chosen,
                cleared = new
                {
                    setting = settings.CodeDirectory,
                    clearEnabled = form.Controls.Find("clearCodeDirectory", true)[0].Enabled
                }
            })
            .Snapshot(
                """
                {
                  asked: [
                    C:\Code,
                    C:\Code
                  ],
                  chosen: {
                    setting: C:\Code,
                    shown: C:\Code,
                    clearEnabled: true
                  },
                  cleared: {
                    clearEnabled: false
                  }
                }
                """);
    }

    // What the Updates page says, as things stand.
    [Test]
    public async Task UpdateStatus()
    {
        var failed = TestUpdates.Installed(
            new(),
            _ => throw new HttpRequestException("No such host is known. (api.nuget.org:443)"));
        await failed.Check();

        await Verify(
            new
            {
                build = OptionsForm.UpdateStatus(TestUpdates.Build()),
                notCheckedYet = OptionsForm.UpdateStatus(TestUpdates.Installed(new())),
                upToDate = OptionsForm.UpdateStatus(TestUpdates.Installed(new() {LastUpdateCheck = TestUpdates.Now.AddHours(-3)})),
                available = OptionsForm.UpdateStatus(TestUpdates.Installed(new() {AvailableUpdate = "0.4.0"})),
                failed = OptionsForm.UpdateStatus(failed)
            })
            .Snapshot(
                """
                {
                  build: This is Lode 0.3.0, run from a build rather than installed as a dotnet tool, so it has nothing to update.,
                  notCheckedYet: This is Lode 0.3.0. It hasn't checked for a newer version yet.,
                  upToDate: Lode 0.3.0 is the newest version. Last checked 11/15/2026 6:30 AM.,
                  available: Lode 0.4.0 is available. This is 0.3.0.,
                  failed: Couldn't check for a newer version: No such host is known. (api.nuget.org:443)
                }
                """);
    }

    // Check now asks straight away, and nothing can be clicked again until NuGet answers.
    [Test]
    public async Task CheckingNow()
    {
        using var scratch = new RegistryScratch();
        var answer = new TaskCompletionSource<NuGetVersion?>();
        var updates = TestUpdates.Installed(new(), _ => answer.Task);
        using var form = Show(scratch.Replacement(), updates: updates);
        form.ShowPage("Updates");
        var before = DescribeUpdates(form);

        Click(form, "checkForUpdates");
        var checking = DescribeUpdates(form);
        answer.SetResult(NuGetVersion.Parse("0.4.0"));
        await Pump.Until(() => !updates.Checking);
        Application.DoEvents();

        await Verify(
            new
            {
                before,
                checking,
                answered = DescribeUpdates(form)
            })
            .Snapshot(
                """
                {
                  before: {
                    status: This is Lode 0.3.0. It hasn't checked for a newer version yet.,
                    updateEnabled: false,
                    checkEnabled: true
                  },
                  checking: {
                    status: Checking for a newer version…,
                    updateEnabled: false,
                    checkEnabled: false
                  },
                  answered: {
                    status: Lode 0.4.0 is available. This is 0.3.0.,
                    updateEnabled: true,
                    checkEnabled: true
                  }
                }
                """);
    }

    // Update starts the update and closes Options, for the window to exit Lode, which the update
    // waits for.
    [Test]
    public async Task UpdatingClosesOptions()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(
            scratch.Replacement(),
            updates: TestUpdates.Installed(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));
        form.ShowPage("Updates");
        var before = DescribeUpdates(form);

        Click(form, "applyUpdate");

        await Verify(
            new
            {
                before,
                form.UpdatesStarted,
                form.UpdateStarted,
                closed = !form.Visible
            })
            .Snapshot(
                """
                {
                  before: {
                    status: Lode 0.4.0 is available. This is 0.3.0.,
                    updateEnabled: true,
                    checkEnabled: true
                  },
                  UpdatesStarted: 1,
                  UpdateStarted: true,
                  closed: true
                }
                """);
    }

    // Updating has Lode exit, which would stop an action running part way, so it waits for the
    // actions to finish.
    [Test]
    public async Task UpdateWaitsForActionsRunning()
    {
        using var scratch = new RegistryScratch();
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        try
        {
            var running = ActionLog.Run("Git pull", @"C:\Code\Lode", () => finish.Task);
            using var form = Show(
                scratch.Replacement(),
                updates: TestUpdates.Installed(
                    new()
                    {
                        AvailableUpdate = "0.4.0"
                    }));
            form.ShowPage("Updates");
            var whileRunning = DescribeUpdates(form);

            finish.SetResult(new(true, "Done", ""));
            await running;
            Application.DoEvents();

            await Verify(
                new
                {
                    whileRunning,
                    finished = DescribeUpdates(form)
                })
                .Snapshot(
                    """
                    {
                      whileRunning: {
                        status: Lode 0.4.0 is available. This is 0.3.0. It can be installed once the actions running have finished.,
                        updateEnabled: false,
                        checkEnabled: true
                      },
                      finished: {
                        status: Lode 0.4.0 is available. This is 0.3.0.,
                        updateEnabled: true,
                        checkEnabled: true
                      }
                    }
                    """);
        }
        finally
        {
            ActionLog.Reset();
        }
    }

    // On a screen too short for it, a small one or one scaled up, the dialog is no taller than
    // the screen and the pages scroll, so Close is still on it. Each page shows from its top.
    // An actions page fits the height left, its table scrolling in it, rather than the page
    // scrolling around a table that scrolls.
    [Test]
    public async Task FitsWithinAShortScreen()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        form.ShowPage("Button actions");
        var fullHeight = form.Height;
        var area = new Rectangle(form.Left, form.Top, 2000, fullHeight - 150);

        form.FitWithin(area);
        Application.DoEvents();
        var close = form.AcceptButton as Control;
        var scrolling = new List<string>();
        foreach (var page in new[] { "General", "View", "Button actions", "Menu actions", "Search", "Code", "Promoted files", "File Explorer", "Links", "Updates" })
        {
            form.ShowPage(page);
            Application.DoEvents();
            if (form.PagesScroll)
            {
                scrolling.Add(page);
            }
        }

        await Assert.That(form.Height).IsLessThanOrEqualTo(area.Height);
        await Assert.That(scrolling).IsNotEmpty();
        await Assert.That(scrolling).DoesNotContain("Button actions");
        await Assert.That(scrolling).DoesNotContain("Menu actions");
        await Assert.That(close!.Visible).IsTrue();
        await Assert.That(form.RectangleToClient(close.RectangleToScreen(close.ClientRectangle)).Bottom).IsLessThanOrEqualTo(form.ClientSize.Height);
    }

    // On a screen set above 100%, the dialog is scaled up once it is built, as this one is for
    // 125%. It is as wide whichever page shows, the actions pages before they have been shown
    // as much as after.
    [Test]
    public async Task ScaledUpKeepsItsWidthAcrossPages()
    {
        using var scratch = new RegistryScratch();
        // Shown as the app shows it, rather than with every control made first as Show does,
        // which lays out the pages not shown yet, as nothing in the app does until they show.
        using var form = new QuietForm(scratch.Replacement(), new(), TestUpdates.Build(), new(), 1.25f)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        Application.DoEvents();
        var opened = form.Width;

        form.ShowPage("Button actions");
        Application.DoEvents();
        var buttonActions = form.Width;
        form.ShowPage("Menu actions");
        Application.DoEvents();
        var menuActions = form.Width;
        form.ShowPage("General");
        Application.DoEvents();

        await Assert.That(buttonActions).IsEqualTo(opened);
        await Assert.That(menuActions).IsEqualTo(opened);
        await Assert.That(form.Width).IsEqualTo(opened);
    }

    // A build has nothing to update, and doesn't ask.
    [Test]
    public async Task ABuildHasNothingToUpdate()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(
            scratch.Replacement(),
            updates: TestUpdates.Build(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));
        form.ShowPage("Updates");

        await Verify(DescribeUpdates(form))
            .Snapshot(
                """
                {
                  status: This is Lode 0.3.0, run from a build rather than installed as a dotnet tool, so it has nothing to update.,
                  updateEnabled: false,
                  checkEnabled: false
                }
                """);
    }

    static object DescribeUpdates(Form form) =>
        new
        {
            status = form.Controls.Find("updateStatus", true)[0].Text,
            updateEnabled = form.Controls.Find("applyUpdate", true)[0].Enabled,
            checkEnabled = form.Controls.Find("checkForUpdates", true)[0].Enabled
        };

    static readonly TextEditors.Editor[] editors =
    [
        new("Visual Studio Code", @"C:\Programs\VS Code\Code.exe"),
        new("Notepad", @"C:\Windows\notepad.exe")
    ];

    static ComboBox EditorBox(Form form) =>
        (ComboBox) form.Controls.Find("textEditor", true)[0];

    static void SelectBrowse(Form form)
    {
        var box = EditorBox(form);
        box.SelectedIndex = box.Items.Count - 1;
    }

    // The list as it reads, the selected entry marked.
    static List<string> Editors(Form form)
    {
        var box = EditorBox(form);
        return box.Items
            .Cast<object>()
            .Select((item, index) => index == box.SelectedIndex ? $"{item}   <- selected" : item.ToString()!)
            .ToList();
    }

    // Shown, off screen and without taking the focus: buttons only click, and forms only draw,
    // once shown.
    static QuietForm Show(ExplorerReplacement replacement, Settings? settings = null, Updates? updates = null, WindowsSearchStub? windowsSearch = null)
    {
        var form = new QuietForm(replacement, settings ?? new(), updates ?? TestUpdates.Build(), windowsSearch ?? new())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        Application.DoEvents();

        // One pass of messages doesn't always get every nested panel created and laid out, and a
        // capture then misses whole rows. Make sure all of it exists, then lay out and draw.
        CreateHandles(form);
        form.PerformLayout();
        form.Refresh();
        Application.DoEvents();
        return form;
    }

    static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            CreateHandles(child);
        }
    }

    static void Click(Form form, string name)
    {
        ((Button) form.Controls.Find(name, true)[0]).PerformClick();
        Application.DoEvents();
    }

    static object Describe(Form form) =>
        new
        {
            status = form.Controls.Find("explorerStatus", true)[0].Text,
            replaceEnabled = form.Controls.Find("replaceExplorer", true)[0].Enabled,
            restoreEnabled = form.Controls.Find("restoreExplorer", true)[0].Enabled
        };

    // Windows Search as a test says it is, never as this machine has it.
    sealed class WindowsSearchStub
    {
        public WindowsSearchService.State State { get; set; } = new(WindowsSearchService.Startup.DelayedAutomatic, true);

        public WindowsSearchService.State Read() => State;
    }

    sealed class QuietForm : OptionsForm
    {
        /// <param name="scale">As Windows scales it on a screen set to that, 1.25 for 125%: once
        /// built, as the tests run unscaled.</param>
        public QuietForm(ExplorerReplacement replacement, Settings settings, Updates updates, WindowsSearchStub windowsSearch, float scale = 1) :
            base(replacement, settings, editors, updates, RegistryScratch.LinksFor(replacement.ClassesPath, replacement.Command), windowsSearch.Read)
        {
            // Never the real update, which would update Lode once the tests end.
            StartUpdate = () => UpdatesStarted++;
            // Never the real Settings.
            OpenSettings = SettingsOpened.Add;
            // Designed for less than the screen has, so scaled up by the difference.
            if (scale != 1)
            {
                AutoScaleDimensions = new(96F / scale, 96F / scale);
            }
        }

        public int UpdatesStarted { get; private set; }

        public List<string> SettingsOpened { get; } = [];

        protected override bool ShowWithoutActivation => true;

        // As the keys would: the modifiers of a real key press come from the keyboard.
        public void Press(Keys keys)
        {
            var message = new Message();
            ProcessCmdKey(ref message, keys);
            Application.DoEvents();
        }
    }
}
