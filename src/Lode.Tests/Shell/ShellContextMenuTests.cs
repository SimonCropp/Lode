/// <summary>
/// The entries Lode leaves off the shell's menu. What else is on it depends on the
/// software installed, so only the absences are checked. The text checks assume English
/// Windows; elsewhere they pass without proving anything.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ShellContextMenuTests
{
    // What a verb starts works in the folder of the item it was run on, as from Explorer, so a
    // script's relative paths are its folder's, not those of Lode's own, System32.
    [Test]
    public async Task AVerbStartsInTheItemsFolder()
    {
        using var fixture = Fixture.Create();
        var script = fixture.At("Docs", "where.cmd");
        var written = fixture.At("where.txt");
        await File.WriteAllTextAsync(script, $"@echo %CD%> \"{written}\"");

        ShellContextMenu.InvokeVerb(IntPtr.Zero, [script], "open");
        await Pump.Until(() => File.Exists(written) && new FileInfo(written).Length > 0);

        await Assert.That((await File.ReadAllTextAsync(written)).Trim()).IsEqualTo(fixture.At("Docs"));
    }

    [Test]
    public Task Folder() =>
        VerifyAbsences(_ => _.At("Docs"));

    [Test]
    public Task TextFile() =>
        VerifyAbsences(
            _ =>
            {
                var path = _.At("notes.txt");
                File.WriteAllText(path, "notes");
                return path;
            });

    // Remove from Quick access, on a Recent folder's menu, takes it out of Quick access as Unpin
    // does a pinned one, so Quick access is read again after it, for the tree to show it gone,
    // as it is after Pin and Unpin. Not after anything else. The menu is a stand-in's, so
    // nothing changes the real Quick access.
    [Test]
    public async Task QuickAccessIsReadAgainAfterItsVerbs()
    {
        var readings = 0;
        var places = Places.Remembering(
            new(),
            () =>
            {
                Interlocked.Increment(ref readings);
                return ([], []);
            });
        await Pump.Until(() => Volatile.Read(ref readings) == 1);
        var after = new Dictionary<string, bool>();
        ShellContextMenu.QuickAccessChanged += places.Reread;
        try
        {
            foreach (var verb in new[] {"removefromhome", "pintohome", "unpinfromhome", "open"})
            {
                var before = Volatile.Read(ref readings);
                Choose(verb);
                // Long enough for the reading to have started, were it going to.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (Volatile.Read(ref readings) == before &&
                       DateTime.UtcNow < deadline)
                {
                    await Pump.For(TimeSpan.FromMilliseconds(50));
                }

                after[verb] = Volatile.Read(ref readings) > before;
            }
        }
        finally
        {
            ShellContextMenu.QuickAccessChanged -= places.Reread;
        }

        await Verify(after)
            .Snapshot(
                """
                {
                  open: false,
                  pintohome: true,
                  removefromhome: true,
                  unpinfromhome: true
                }
                """);

        // Chooses the entry from a stand-in's menu, which runs it.
        static void Choose(string verb)
        {
            using var standIn = StandInMenu.Start(verb);
            using var menu = new ShellContextMenu();
            menu.Track = (_, _) => standIn.Command(verb);
            menu.Show(standIn.Menu, IntPtr.Zero, Point.Empty, [], customFirst: false, 0, _ => false, null);
            if (standIn.Invoked is not [_])
            {
                throw new($"{verb} wasn't run");
            }
        }
    }

    // Windows adds removable and mapped drives to Send to, which are not in the SendTo folder.
    // However many there are, Send to is still known by what it lists from there.
    [Test]
    public async Task SendToKnownWithDrives()
    {
        var sendTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop (create shortcut)",
            "Documents",
            "Mail recipient"
        };
        List<string> entries =
        [
            "Desktop (create shortcut)",
            "Documents",
            "Mail recipient",
            "",
            "USB Drive (E:)",
            "DVD RW Drive (D:)",
            @"Work (\\server) (Z:)"
        ];

        await Assert.That(ShellContextMenu.Lists(entries, sendTo)).IsTrue();
        await Assert.That(ShellContextMenu.Lists(["&Open", "Print", "Edit"], sendTo)).IsFalse();
        await Assert.That(ShellContextMenu.Lists(["USB Drive (E:)", "Pictures (F:)"], sendTo)).IsFalse();
    }

    // The shell offers Rename only to a host that says it renames, so the tree's classic menu
    // leaves it off a drive and a Pinned or Recent entry, which the tree can't rename, rather
    // than offer one that would go nowhere.
    [Test]
    public async Task RenameOnlyWhereTheHostRenames()
    {
        using var fixture = Fixture.Create();
        bool HasRename(bool canRename) =>
            ShellContextMenu.Describe(IntPtr.Zero, [fixture.At("Docs")], canRename)
                .Any(_ => string.Equals(_.Verb, "rename", StringComparison.OrdinalIgnoreCase));

        await Assert.That(HasRename(true)).IsTrue();
        await Assert.That(HasRename(false)).IsFalse();
    }

    // Items from more than one folder, as a search finds, are each named to their folder by their
    // own name alone, so the shell is asked for nothing for them, rather than for whatever the
    // first one's folder has by the others' names.
    [Test]
    public async Task ItemsFromDifferentFoldersAreRefused()
    {
        using var fixture = Fixture.Create();
        var together = ShellContextMenu.CreateDataObject(IntPtr.Zero, [fixture.At("notes.aleph"), fixture.At("Docs")]);
        var apart = ShellContextMenu.CreateDataObject(IntPtr.Zero, [fixture.At("notes.aleph"), fixture.At("Docs", "guide.aleph")]);
        if (together != IntPtr.Zero)
        {
            Marshal.Release(together);
        }

        await Assert.That(together).IsNotEqualTo(IntPtr.Zero);
        await Assert.That(apart).IsEqualTo(IntPtr.Zero);
    }

    // Their properties are still shown, though: by the shell's properties for several items,
    // which takes them each named in full, in what a selection is handed over in.
    [Test]
    [WindowThread]
    public async Task PropertiesTakeItemsFromDifferentFolders()
    {
        using var fixture = Fixture.Create();
        string[] paths = [fixture.At("notes.aleph"), fixture.At("Docs", "guide.aleph")];

        var pointer = ShellContextMenu.CreateDataObjectAcrossFolders(paths);
        string[]? files = null;
        if (pointer != IntPtr.Zero)
        {
            var dataObject = new DataObject(Marshal.GetObjectForIUnknown(pointer));
            dataObject.TryGetData(DataFormats.FileDrop, out files);
            Marshal.Release(pointer);
        }

        await Assert.That(files).IsNotNull();
        await Assert.That(files!).IsEquivalentTo(paths);
    }

    // An entry with a submenu of 8 to 15 entries, or 24 to 31, reads from GetMenuState as a
    // separator, which tidying would delete, along with a real separator just above it. Only
    // separators at the ends or side by side go.
    [Test]
    public async Task TidyingSeparatorsKeepsSubmenus()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, separator, UIntPtr.Zero, null);
            AppendMenu(menu, text, 1, "Open");
            AppendMenu(menu, popup, Submenu(8), "TortoiseGit");
            AppendMenu(menu, separator, UIntPtr.Zero, null);
            AppendMenu(menu, popup, Submenu(12), "7-Zip");
            AppendMenu(menu, popup, Submenu(24), "Tools");
            AppendMenu(menu, separator, UIntPtr.Zero, null);
            AppendMenu(menu, separator, UIntPtr.Zero, null);
            AppendMenu(menu, text, 2, "Properties");
            AppendMenu(menu, separator, UIntPtr.Zero, null);

            ShellContextMenu.TidySeparators(menu);

            var entries = new List<string>();
            for (var position = 0; position < GetMenuItemCount(menu); position++)
            {
                var name = new StringBuilder(64);
                GetMenuString(menu, (uint)position, name, name.Capacity, byPosition);
                entries.Add(name.Length == 0 ? "----" : name.ToString());
            }

            await Assert.That(entries).IsEquivalentTo(
                ["Open", "TortoiseGit", "----", "7-Zip", "Tools", "----", "Properties"],
                CollectionOrdering.Matching);
        }
        finally
        {
            DestroyMenu(menu);
        }

        static UIntPtr Submenu(int count)
        {
            var submenu = CreatePopupMenu();
            for (var index = 0; index < count; index++)
            {
                AppendMenu(submenu, text, (UIntPtr)(100 + index), $"Entry {index}");
            }

            return (UIntPtr)(ulong)submenu;
        }
    }

    const uint text = 0;
    const uint separator = 0x800;
    const uint popup = 0x10;
    const uint byPosition = 0x400;

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")]
    static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    // Takes the caller's name so each test gets its own fixture folder and they can run side by side.
    static async Task VerifyAbsences(Func<Fixture, string> pathFor, [CallerMemberName] string member = "")
    {
        using var fixture = Fixture.Create(member: member);
        var entries = ShellContextMenu.Describe(IntPtr.Zero, [pathFor(fixture)]);
        var texts = entries.Select(_ => _.Text.Replace("&", "")).ToList();
        var sendTo = texts.Contains("Send to");

        try
        {
            await Verify(
                new
                {
                    sendTo,
                    includeInLibrary = texts.Contains("Include in library"),
                    restorePreviousVersions = entries.Any(_ => _.Verb == "PreviousVersions"),
                    editInNotepad = entries.Any(_ => _.Verb == "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"),
                    doubledSeparator = texts.Zip(texts.Skip(1)).Any(_ => _.First.Length == 0 && _.Second.Length == 0)
                });
        }
        catch (Exception exception) when (sendTo)
        {
            // Temporary: CI keeps Send to on a text file's menu where a local run removes it.
            // Says what the submenu held and what it was compared with.
            throw new(SendToDiagnostics(entries), exception);
        }
    }

    static string SendToDiagnostics(List<ShellContextMenu.MenuEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Send to was left on the menu.");
        var submenu = entries.First(_ => _.Text.Replace("&", "") == "Send to").Submenu;
        builder.AppendLine($"Submenu, {submenu.Count} entries:");
        foreach (var entry in submenu)
        {
            builder.AppendLine($"  '{entry.Text}' verb={entry.Verb ?? "(none)"}");
        }

        AppendFolder(builder, "SendTo folder", Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        AppendFolder(
            builder,
            "Libraries folder",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Libraries"));
        return builder.ToString();
    }

    static void AppendFolder(StringBuilder builder, string label, string folder)
    {
        if (!Directory.Exists(folder))
        {
            builder.AppendLine($"{label} '{folder}': missing");
            return;
        }

        builder.AppendLine($"{label} '{folder}':");
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            var hidden = File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
            builder.AppendLine($"  '{Path.GetFileName(path)}' displayName='{ShellIcons.Describe(path).DisplayName}' hidden={hidden}");
        }
    }
}
