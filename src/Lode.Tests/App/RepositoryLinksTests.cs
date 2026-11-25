public class RepositoryLinksTests
{
    const string gitHubDesktop = """
                                 "C:\Users\Someone\AppData\Local\GitHubDesktop\app-3.6.11\GitHubDesktop.exe" --protocol-launcher "%1"
                                 """;

    // GitHub Desktop registers its links in the user's classes, as Electron apps do.
    static void SeedGitHubDesktop(RegistryScratch scratch)
    {
        scratch.Set("x-github-client", null, "URL:x-github-client");
        scratch.Set("x-github-client", "URL Protocol", "");
        scratch.Set(@"x-github-client\shell\open\command", null, gitHubDesktop);
    }

    // Lode's command replaces GitHub Desktop's, which is kept to put back, and to hand on links
    // for repositories not in the code folder.
    [Test]
    public async Task TakeThenGiveBackGitHubDesktopsLinks()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.DumpAll();
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;

        links.Take(handler);
        var taken = scratch.DumpAll();
        var state = new
        {
            taken = links.IsTaken(handler),
            here = links.IsTakenHere(handler),
            registered = links.IsRegistered(handler),
            forward = links.ForwardCommand(handler)
        };
        links.GiveBack(handler);

        await Assert.That(JsonSerializer.Serialize(scratch.DumpAll())).IsEqualTo(JsonSerializer.Serialize(before));
        await Verify(
            new
            {
                taken,
                state
            })
            .ScrubLinesWithReplace(_ => _.Replace(scratch.Root, "{Software}"))
            .Snapshot(
                """
                {
                  taken: {
                    Classes\: {
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
                        shell\: {
                          open\: {
                            command\: {
                              (default): "C:\Tools\lode.exe" "%1",
                              LodeCommand: "C:\Tools\lode.exe" "%1",
                              LodeForwardCommand: "C:\Apps\GitHub Desktop.exe" "%1",
                              LodePreviousCommand: "C:\Users\Someone\AppData\Local\GitHubDesktop\app-3.6.11\GitHubDesktop.exe" --protocol-launcher "%1"
                            }
                          }
                        },
                        TakenBy: Lode,
                        URL Protocol: 
                      }
                    },
                    Lode\: {
                      Capabilities\: {
                        ApplicationDescription: Opens repositories from your code folder when a code site's button would hand them to a desktop app.,
                        ApplicationIcon: "C:\Tools\lode.exe",0,
                        ApplicationName: Lode,
                        URLAssociations\: {
                          x-github-client: Lode.RepositoryLink
                        }
                      }
                    },
                    RegisteredApplications\: {
                      Lode: {Software}\Lode\Capabilities
                    }
                  },
                  state: {
                    taken: true,
                    here: true,
                    registered: true,
                    forward: "C:\Apps\GitHub Desktop.exe" "%1"
                  }
                }
                """);
    }

    // Visual Studio's links are only in the machine's classes. Take adds the scheme to the user's,
    // and GiveBack takes it all away again.
    [Test]
    public async Task TakeThenGiveBackVisualStudiosLinks()
    {
        using var scratch = new RegistryScratch();
        var links = scratch.Links();
        var handler = RepositoryLinks.VisualStudio;

        links.Take(handler);
        var taken = scratch.Dump();
        var forward = links.ForwardCommand(handler);
        links.GiveBack(handler);

        await Verify(
            new
            {
                taken,
                forward,
                givenBack = scratch.Dump()
            })
            .Snapshot(
                """
                {
                  taken: {
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
                    }
                  },
                  forward: "C:\Apps\Visual Studio.exe" "%1"
                }
                """);
    }

    // A second Take, by another copy of Lode, points the links at it and still keeps what the
    // first one displaced.
    [Test]
    public async Task TakingAgainKeepsWhatWasDisplaced()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.Dump();
        var handler = RepositoryLinks.GitHubDesktop;
        scratch.Links().Take(handler);
        var other = scratch.Links(
            """
            "D:\Other\Lode.exe"
            """);

        var hereBefore = scratch.Links().IsTakenHere(handler);
        other.Take(handler);
        var hereAfter = scratch.Links().IsTakenHere(handler);
        var forward = other.ForwardCommand(handler);
        other.GiveBack(handler);

        await Assert.That(hereBefore).IsTrue();
        await Assert.That(hereAfter).IsFalse();
        await Assert.That(forward).IsEqualTo("\"C:\\Apps\\GitHub Desktop.exe\" \"%1\"");
        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(before));
    }

    // What GitHub Desktop writes each time it starts, once it has updated: its own command, over
    // whatever had its links, and nothing else.
    const string gitHubDesktopUpdated = """
                                        "C:\Users\Someone\AppData\Local\GitHubDesktop\app-3.7.0\GitHubDesktop.exe" --protocol-launcher "%1"
                                        """;

    static void StartGitHubDesktop(RegistryScratch scratch) =>
        scratch.Set(@"x-github-client\shell\open\command", null, gitHubDesktopUpdated);

    // GitHub Desktop takes its links back when it starts, leaving what Lode wrote beside its
    // command: they are no longer Lode's, here or in another copy, and links Lode can't open go
    // to GitHub Desktop's command as it is now.
    [Test]
    public async Task TakenBackByTheApp()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);

        StartGitHubDesktop(scratch);

        await Assert.That(links.IsTaken(handler)).IsFalse();
        await Assert.That(links.IsTakenHere(handler)).IsFalse();
        await Assert.That(links.IsTakenBack(handler)).IsTrue();
        await Assert.That(links.ForwardCommand(handler)).IsEqualTo("\"C:\\Apps\\GitHub Desktop.exe\" \"%1\"");
    }

    // Giving back links the app took back leaves its command, not the older one Take kept, and
    // takes away only what Lode left.
    [Test]
    public async Task GiveBackAfterTheAppTookThemBack()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        StartGitHubDesktop(scratch);

        links.GiveBack(handler);

        using var expected = new RegistryScratch();
        SeedGitHubDesktop(expected);
        StartGitHubDesktop(expected);
        await Assert.That(links.IsTakenBack(handler)).IsFalse();
        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(expected.Dump()));
    }

    // Taking links again after the app took them back keeps the app's command as it is now, for
    // GiveBack to put back.
    [Test]
    public async Task TakeAgainAfterTheAppTookThemBack()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        StartGitHubDesktop(scratch);

        links.Take(handler);
        var here = links.IsTakenHere(handler);
        links.GiveBack(handler);

        using var expected = new RegistryScratch();
        SeedGitHubDesktop(expected);
        StartGitHubDesktop(expected);
        await Assert.That(here).IsTrue();
        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(expected.Dump()));
    }

    // An app writing its command into the scheme key Take made has made the key its own: giving
    // back leaves the key and the app's command.
    [Test]
    public async Task GiveBackLeavesAKeyAnAppWroteInto()
    {
        using var scratch = new RegistryScratch();
        var links = scratch.Links();
        var handler = RepositoryLinks.VisualStudio;
        links.Take(handler);
        scratch.Set(@"git-client\shell\open\command", null, "\"C:\\Apps\\Visual Studio.exe\" \"%1\"");

        links.GiveBack(handler);

        await Verify(scratch.Dump())
            .Snapshot(
                """
                {
                  git-client\: {
                    (default): URL:git-client,
                    shell\: {
                      open\: {
                        command\: {
                          (default): "C:\Apps\Visual Studio.exe" "%1"
                        }
                      }
                    },
                    URL Protocol: 
                  }
                }
                """);
    }

    // Links a Lode took before it noted the command it wrote are this copy's while the command
    // is this copy's.
    [Test]
    public async Task TakenByAnOlderLode()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        using (var commandKey = Registry.CurrentUser.OpenSubKey($@"{scratch.ClassesPath}\x-github-client\shell\open\command", writable: true)!)
        {
            commandKey.DeleteValue("LodeCommand");
        }

        var here = links.IsTakenHere(handler);
        StartGitHubDesktop(scratch);

        await Assert.That(here).IsTrue();
        await Assert.That(links.IsTakenBack(handler)).IsTrue();
    }

    // With Lode chosen in Default apps, GitHub Desktop writing its command back changes nothing:
    // Windows asks the choice first. A link Lode can't open goes to GitHub Desktop's command as it
    // wrote it.
    [Test]
    public async Task ChosenInDefaultApps()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        scratch.Choose(handler.Scheme, "Lode.RepositoryLink");

        StartGitHubDesktop(scratch);

        await Assert.That(links.IsChosen(handler)).IsTrue();
        await Assert.That(links.IsTaken(handler)).IsTrue();
        await Assert.That(links.IsTakenHere(handler)).IsTrue();
        await Assert.That(links.IsTakenBack(handler)).IsFalse();
        await Assert.That(links.OtherChosenInWindows(handler)).IsNull();
        await Assert.That(links.ForwardCommand(handler)).IsEqualTo(gitHubDesktopUpdated);
    }

    // Giving back links Lode is chosen for takes away the choice, so Windows goes back to the
    // scheme's command, GitHub Desktop's, and takes away Lode's registration.
    [Test]
    public async Task GiveBackWhenChosen()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        scratch.Choose(handler.Scheme, "Lode.RepositoryLink");
        StartGitHubDesktop(scratch);

        links.GiveBack(handler);

        using var expected = new RegistryScratch();
        SeedGitHubDesktop(expected);
        StartGitHubDesktop(expected);
        var givenBack = scratch.DumpAll();
        // Where Windows keeps its choices, which it always has.
        givenBack.Remove(@"Associations\");
        await Assert.That(links.ChosenInWindows(handler)).IsNull();
        await Assert.That(links.IsRegistered(handler)).IsFalse();
        await Assert.That(JsonSerializer.Serialize(givenBack)).IsEqualTo(JsonSerializer.Serialize(expected.DumpAll()));
    }

    // Taking links Lode is chosen for, once the app took them back, keeps the app's command
    // again, for GiveBack to put back.
    [Test]
    public async Task TakeWhenChosenAndTakenBack()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        scratch.Choose(handler.Scheme, "Lode.RepositoryLink");
        StartGitHubDesktop(scratch);

        links.Take(handler);

        await Assert.That(scratch.Raw(@"x-github-client\shell\open\command", "LodePreviousCommand").Value).IsEqualTo(gitHubDesktopUpdated);
        await Assert.That(links.ForwardCommand(handler)).IsEqualTo(gitHubDesktopUpdated);
    }

    // Each kind of link is registered on its own: giving one back leaves Lode registered for the
    // other.
    [Test]
    public async Task RegisteredForEachKindOfLink()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        links.Take(RepositoryLinks.GitHubDesktop);
        links.Take(RepositoryLinks.VisualStudio);

        links.GiveBack(RepositoryLinks.GitHubDesktop);

        var all = scratch.DumpAll();
        all.Remove(@"Classes\");
        var applications = (Dictionary<string, object>) all[@"RegisteredApplications\"];
        all.Remove(@"RegisteredApplications\");
        await Assert.That(links.IsRegistered(RepositoryLinks.GitHubDesktop)).IsFalse();
        await Assert.That(links.IsRegistered(RepositoryLinks.VisualStudio)).IsTrue();
        await Assert.That((string) applications["Lode"]).EndsWith(@"\Lode\Capabilities");
        await Verify(all)
            .Snapshot(
                """
                {
                  Lode\: {
                    Capabilities\: {
                      ApplicationDescription: Opens repositories from your code folder when a code site's button would hand them to a desktop app.,
                      ApplicationIcon: "C:\Tools\lode.exe",0,
                      ApplicationName: Lode,
                      URLAssociations\: {
                        git-client: Lode.RepositoryLink
                      }
                    }
                  }
                }
                """);
    }

    // Another app chosen in Default apps is told apart from Lode.
    [Test]
    public async Task AnotherAppChosen()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;
        links.Take(handler);
        scratch.Choose(handler.Scheme, "Other.Link");

        await Assert.That(links.IsChosen(handler)).IsFalse();
        await Assert.That(links.OtherChosenInWindows(handler)).IsEqualTo("Other.Link");
        // Registered nowhere: only its ProgId names it.
        await Assert.That(links.OtherChosenAppName(handler)).IsEqualTo("Other.Link");
    }

    // An app chosen in Default apps is named as its registration names it, not by its ProgId.
    [Test]
    public async Task AnotherAppChosenIsNamed()
    {
        using var scratch = new RegistryScratch();
        var links = scratch.Links();
        var handler = RepositoryLinks.VisualStudio;
        scratch.RegisterApp("VisualStudio.e6eba60c", "git-client", "VisualStudio.git-client.e6eba60c", "Microsoft Visual Studio");
        scratch.RegisterApp("VisualStudio.06f432b0", "git-client", "VisualStudio.git-client.06f432b0", null);
        links.Take(handler);
        scratch.Choose(handler.Scheme, "VisualStudio.git-client.e6eba60c");

        var named = links.OtherChosenAppName(handler);
        // A registration without a name: the name it is registered under.
        scratch.Choose(handler.Scheme, "VisualStudio.git-client.06f432b0");
        var unnamed = links.OtherChosenAppName(handler);

        await Assert.That(named).IsEqualTo("Microsoft Visual Studio");
        await Assert.That(unnamed).IsEqualTo("VisualStudio.06f432b0");
    }

    // Giving back links Lode never took leaves them alone.
    [Test]
    public async Task GiveBackLeavesAnotherAppsLinksAlone()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.Dump();

        scratch.Links().GiveBack(RepositoryLinks.GitHubDesktop);

        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(before));
    }

    [Test]
    public async Task HandlerOf()
    {
        await Assert.That(RepositoryLinks.HandlerOf("x-github-client://openRepo/https://github.com/owner/repo")).IsEqualTo(RepositoryLinks.GitHubDesktop);
        await Assert.That(RepositoryLinks.HandlerOf("GIT-CLIENT://clone?repo=x")).IsEqualTo(RepositoryLinks.VisualStudio);
        await Assert.That(RepositoryLinks.HandlerOf(@"D:\Code")).IsNull();
        await Assert.That(RepositoryLinks.HandlerOf("x-github-desktop-auth://oauth")).IsNull();
    }
}
