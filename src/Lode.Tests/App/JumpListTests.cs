public class JumpListTests
{
    static readonly string[] pinned = [@"C:\Users\Simon\Desktop", @"D:\Code", @"D:\Other\src", @"\\server\video"];
    static readonly string[] frequent = [@"D:\Code\Lode\src", @"D:\Downloads", @"\\server\video\Films"];

    // Pinned, then Frequent, each called what a tab would call it, with the two src folders
    // told apart across the sections.
    [Test]
    public Task Plan() =>
        Verify(JumpList.Plan(pinned, frequent, [], slots: 10))
            .Snapshot(
                """
                [
                  {
                    Name: Pinned,
                    Items: [
                      {
                        Title: Desktop,
                        Folder: C:\Users\Simon\Desktop
                      },
                      {
                        Title: Code,
                        Folder: D:\Code
                      },
                      {
                        Title: Other\src,
                        Folder: D:\Other\src
                      },
                      {
                        Title: video (\\server),
                        Folder: \\server\video
                      }
                    ]
                  },
                  {
                    Name: Frequent,
                    Items: [
                      {
                        Title: Lode\src,
                        Folder: D:\Code\Lode\src
                      },
                      {
                        Title: Downloads,
                        Folder: D:\Downloads
                      },
                      {
                        Title: Films (\\server),
                        Folder: \\server\video\Films
                      }
                    ]
                  }
                ]
                """);

    // The pinned ones take the first slots, and Frequent gets what is left. With none left, it
    // isn't there.
    [Test]
    public Task PlanFillsTheSlotsPinnedFirst() =>
        Verify(
            new
            {
                five = JumpList.Plan(pinned, frequent, [], slots: 5),
                three = JumpList.Plan(pinned, frequent, [], slots: 3)
            })
            .Snapshot(
                """
                {
                  five: [
                    {
                      Name: Pinned,
                      Items: [
                        {
                          Title: Desktop,
                          Folder: C:\Users\Simon\Desktop
                        },
                        {
                          Title: Code,
                          Folder: D:\Code
                        },
                        {
                          Title: Other\src,
                          Folder: D:\Other\src
                        },
                        {
                          Title: video (\\server),
                          Folder: \\server\video
                        }
                      ]
                    },
                    {
                      Name: Frequent,
                      Items: [
                        {
                          Title: Lode\src,
                          Folder: D:\Code\Lode\src
                        }
                      ]
                    }
                  ],
                  three: [
                    {
                      Name: Pinned,
                      Items: [
                        {
                          Title: Desktop,
                          Folder: C:\Users\Simon\Desktop
                        },
                        {
                          Title: Code,
                          Folder: D:\Code
                        },
                        {
                          Title: src,
                          Folder: D:\Other\src
                        }
                      ]
                    }
                  ]
                }
                """);

    // A folder removed from the list leaves its slot to the next one, and a section with all
    // of its folders removed isn't there. Removed ones are matched ignoring case.
    [Test]
    public Task PlanLeavesOutRemovedFolders() =>
        Verify(
            new
            {
                some = JumpList.Plan(pinned, frequent, [@"d:\code", @"D:\Downloads"], slots: 5),
                allPinned = JumpList.Plan(pinned, frequent, pinned, slots: 10)
            })
            .Snapshot(
                """
                {
                  some: [
                    {
                      Name: Pinned,
                      Items: [
                        {
                          Title: Desktop,
                          Folder: C:\Users\Simon\Desktop
                        },
                        {
                          Title: Other\src,
                          Folder: D:\Other\src
                        },
                        {
                          Title: video (\\server),
                          Folder: \\server\video
                        }
                      ]
                    },
                    {
                      Name: Frequent,
                      Items: [
                        {
                          Title: Lode\src,
                          Folder: D:\Code\Lode\src
                        },
                        {
                          Title: Films (\\server),
                          Folder: \\server\video\Films
                        }
                      ]
                    }
                  ],
                  allPinned: [
                    {
                      Name: Frequent,
                      Items: [
                        {
                          Title: src,
                          Folder: D:\Code\Lode\src
                        },
                        {
                          Title: Downloads,
                          Folder: D:\Downloads
                        },
                        {
                          Title: Films (\\server),
                          Folder: \\server\video\Films
                        }
                      ]
                    }
                  ]
                }
                """);

    // Removed folders are kept while Quick access has them, and forgotten once it hasn't.
    [Test]
    public Task StillRemoved() =>
        Verify(
            new
            {
                newlyRemoved = JumpList.StillRemoved([], [@"D:\Code", @"D:\Downloads"], pinned, frequent),
                removedBeforeAndSince = JumpList.StillRemoved([@"D:\Code"], [@"d:\code", @"D:\Downloads"], pinned, frequent),
                leftQuickAccess = JumpList.StillRemoved([@"D:\Code", @"D:\Gone"], [@"D:\Unpinned"], pinned, frequent)
            })
            .Snapshot(
                """
                {
                  newlyRemoved: [
                    D:\Code,
                    D:\Downloads
                  ],
                  removedBeforeAndSince: [
                    D:\Code,
                    D:\Downloads
                  ],
                  leftQuickAccess: [
                    D:\Code
                  ]
                }
                """);

    // A drive's root keeps its backslash: unescaped, it would quote the closing quote.
    [Test]
    public async Task Argument()
    {
        await Assert.That(JumpList.Argument(@"D:\Code")).IsEqualTo("\"D:\\Code\"");
        await Assert.That(JumpList.Argument(@"D:\")).IsEqualTo("\"D:\\\\\"");
        await Assert.That(JumpList.Argument(@"\\server\video")).IsEqualTo("\"\\\\server\\video\"");
    }

    // What the command line hands back is the folder.
    [Test]
    public async Task ArgumentReadsBackAsTheFolder()
    {
        foreach (var folder in (string[]) [@"D:\Code", @"D:\", @"C:\Program Files", @"\\server\video"])
        {
            await Assert.That(CommandLineToArguments(JumpList.Argument(folder))).IsEquivalentTo([folder]);
        }
    }

    [Test]
    public async Task ParseIconLocation()
    {
        var imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
        await Assert.That(JumpList.ParseIconLocation(@"%SystemRoot%\system32\imageres.dll,-183")).IsEqualTo((imageres, -183));
        await Assert.That(JumpList.ParseIconLocation($"{imageres},3")).IsEqualTo((imageres, 3));
        await Assert.That(JumpList.ParseIconLocation(imageres)).IsEqualTo((imageres, 0));
        await Assert.That(JumpList.ParseIconLocation(@"C:\Nowhere\icons.dll,-3")).IsNull();
    }

    // Desktop has an icon of its own, which a plain folder doesn't, and each is in a file on
    // this machine.
    [Test]
    public async Task KnownFolderHasItsOwnIcon()
    {
        using var fixture = Fixture.Create();
        var desktop = JumpList.IconLocation(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var plain = JumpList.IconLocation(fixture.Root);

        await Assert.That(desktop).IsNotNull();
        await Assert.That(plain).IsNotNull();
        await Assert.That(desktop).IsNotEqualTo(plain);
        await Assert.That(File.Exists(desktop!.Value.File)).IsTrue();
        await Assert.That(File.Exists(plain!.Value.File)).IsTrue();
    }

    // Asked without going to the network, so a server that isn't there costs nothing.
    [Test]
    public async Task NetworkIconsAreWindowsOwn()
    {
        await Assert.That(JumpList.IconLocation(@"\\nowhere-lode-test")).IsEqualTo(TaskbarIdentity.StockIcon(TaskbarIdentity.StockServer));
        await Assert.That(JumpList.IconLocation(@"\\nowhere-lode-test\video")).IsEqualTo(TaskbarIdentity.StockIcon(TaskbarIdentity.StockServerShare));
        await Assert.That(JumpList.IconLocation(@"\\nowhere-lode-test\video\Films")).IsEqualTo(TaskbarIdentity.StockIcon(TaskbarIdentity.StockFolder));
    }

    // The list is written to the taskbar, under an id of the tests' own, which is then taken
    // off again. A section is refused only when Windows is set not to show recent items in
    // jump lists.
    [Test]
    [WindowThread]
    public async Task WritesAListTheTaskbarTakes()
    {
        using var fixture = Fixture.Create();
        const string appId = "Lode.Tests.JumpList";
        var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            JumpList.Started? started = null;
            var results = JumpList.Write(
                appId,
                (program, null),
                _ =>
                {
                    started = _;
                    return JumpList.Plan([fixture.Root], [Path.GetTempPath()], _.Removed, _.Slots);
                });

            await Assert.That(started!.Slots).IsGreaterThan(0);
            await Assert.That(started.Removed).IsEmpty();
            await Assert.That(results.Select(_ => _.Category)).IsEquivalentTo(["Pinned", "Frequent"]);
            await Assert.That(results.All(_ => _.Result is 0 or accessDenied)).IsTrue();
        }
        finally
        {
            JumpList.Delete(appId);
        }
    }

    const int accessDenied = unchecked((int)0x80070005);

    static string[] CommandLineToArguments(string arguments)
    {
        // As a program sees its command line, after its own path.
        var pointer = CommandLineToArgvW($"lode.exe {arguments}", out var count);
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
