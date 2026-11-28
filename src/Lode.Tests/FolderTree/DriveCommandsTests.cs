/// <summary>
/// What a drive's right-click menu offers beyond a folder's, in the tree and in This PC's list:
/// Eject, Disconnect, and Rename, for its label. Whatever would ask Windows is swapped, so no
/// real drive is ever ejected, disconnected or renamed, and a letter no drive has stands in for
/// one, of whichever kind the test says.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class DriveCommandsTests
{
    const string drive = @"Q:\";

    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    // As Explorer's menu has them: Eject for a removable or optical drive, Disconnect for one
    // mapped to a share, and Rename for one of the machine's own disks, the same in the tree as
    // in This PC's list. Pinned, a drive is Quick access's entry, which isn't renamed, but still
    // ejects. A folder on a drive, two drives selected together and a share have none of them,
    // and a share isn't asked what kind of drive it is.
    [Test]
    public async Task EachKindOfDriveOffersWhatExplorersMenuDoes()
    {
        using var tree = CreateTree(pinned: [drive]);
        using var view = new FolderView(new(), noPlaces);
        _ = view.Handle;
        var node = DriveNode(tree);
        var pinned = tree.Nodes[0].Nodes[0];
        var folder = new TreeNode
        {
            Tag = drive + "Photos"
        };
        node.Nodes.Add(folder);
        var entry = FolderView.DriveEntry(drive, () => (0, 0));
        var another = FolderView.DriveEntry(@"R:\", () => (0, 0));
        var lines = new List<string>();
        foreach (var type in new[] { DriveType.Removable, DriveType.CDRom, DriveType.Network, DriveType.Fixed, DriveType.Ram, DriveType.Unknown })
        {
            tree.Drives.TypeOf = _ => type;
            view.Drives.TypeOf = _ => type;
            lines.Add($"{type}: tree {Offered(tree.NodeCommands(drive, node: node))}; list {Offered(view.ItemCommands([entry]))}");
        }

        tree.Drives.TypeOf = _ => DriveType.Removable;
        view.Drives.TypeOf = _ => DriveType.Removable;
        lines.Add($"pinned: {Offered(tree.NodeCommands(drive, node: pinned))}");
        lines.Add($"folder on it: {Offered(tree.NodeCommands(drive + "Photos", node: folder))}");
        lines.Add($"two selected: {Offered(view.ItemCommands([entry, another]))}");
        tree.Drives.TypeOf = path => throw new InvalidOperationException($"Asked what kind of drive {path} is");
        lines.Add($"share: {Offered(tree.NodeCommands(@"\\server\share", node: node))}");

        await Verify(lines)
            .Snapshot(
                """
                [
                  Removable: tree Rename, Eject; list Rename, Eject,
                  CDRom: tree Rename (disabled), Eject; list Rename (disabled), Eject,
                  Network: tree Rename (disabled), Disconnect; list Rename (disabled), Disconnect,
                  Fixed: tree Rename; list Rename,
                  Ram: tree Rename (disabled); list Rename (disabled),
                  Unknown: tree Rename (disabled); list Rename (disabled),
                  pinned: Rename (disabled), Eject,
                  folder on it: Rename,
                  two selected: Rename (disabled),
                  share: Rename (disabled)
                ]
                """);
    }

    // Eject and Disconnect are the shell's own eject and disconnect verbs on the drive, run on a
    // thread of their own, set up for the shell as the UI thread is, so Windows can take its
    // time letting go of the drive, or asking about the files open on it, without any window
    // waiting: the menu's entry returns at once, before the verb is done.
    [Test]
    public async Task EjectAndDisconnectRunTheShellsVerbsOffTheUIThread()
    {
        using var tree = CreateTree();
        using var view = new FolderView(new(), noPlaces);
        _ = view.Handle;
        var node = DriveNode(tree);
        var entry = FolderView.DriveEntry(drive, () => (0, 0));
        var uiThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        var ran = new List<(string Root, string Verb, bool OnUIThread, ApartmentState Apartment)>();
        void Run(IntPtr owner, string root, string verb)
        {
            lock (ran)
            {
                ran.Add((root, verb, Environment.CurrentManagedThreadId == uiThread, Thread.CurrentThread.GetApartmentState()));
            }

            // Not for ever, should the UI thread wait on it after all, so the test fails rather
            // than holding the run.
            release.Wait(TimeSpan.FromSeconds(30));
        }

        int Ran()
        {
            lock (ran)
            {
                return ran.Count;
            }
        }

        tree.Drives.RunVerb = Run;
        view.Drives.RunVerb = Run;
        tree.Drives.TypeOf = _ => DriveType.Removable;
        Command(tree.NodeCommands(drive, node: node), "Eject").Action();
        await Pump.Until(() => Ran() == 1);
        var returnedBeforeDone = !release.IsSet;
        release.Set();
        tree.Drives.TypeOf = _ => DriveType.Network;
        Command(tree.NodeCommands(drive, node: node), "Disconnect").Action();
        view.Drives.TypeOf = _ => DriveType.CDRom;
        Command(view.ItemCommands([entry]), "Eject").Action();
        await Pump.Until(() => Ran() == 3);
        view.Drives.TypeOf = _ => DriveType.Network;
        Command(view.ItemCommands([entry]), "Disconnect").Action();
        await Pump.Until(() => Ran() == 4);

        await Assert.That(returnedBeforeDone).IsTrue();
        await Assert.That(ran.Select(_ => (_.Verb, _.Root)).Order())
            .IsEquivalentTo(
                new List<(string, string)> { ("disconnect", drive), ("disconnect", drive), ("eject", drive), ("eject", drive) },
                CollectionOrdering.Matching);
        await Assert.That(ran.All(_ => !_.OnUIThread && _.Apartment == ApartmentState.STA)).IsTrue();
    }

    // Rename in the tree edits the drive's label, not the name the tree shows it by, with its
    // letter, and gives the drive the label typed, off the UI thread, as the tree's own label
    // editing has it. Every window is then told, to show the drive by it, and the tree shows
    // the drive as it did meanwhile.
    [Test]
    public async Task RenamingADriveInTheTreeGivesItTheLabelTyped()
    {
        using var form = new Form
        {
            ClientSize = new(300, 400),
            StartPosition = FormStartPosition.Manual,
            Location = new(-2000, -2000),
            ShowInTaskbar = false
        };
        var tree = CreateTree();
        tree.Dock = DockStyle.Fill;
        form.Controls.Add(tree);
        form.Show();
        var node = DriveNode(tree);
        var shown = node.Text;
        var uiThread = Environment.CurrentManagedThreadId;
        var written = new List<(string Root, string Label, bool OnUIThread)>();
        tree.Drives.TypeOf = _ => DriveType.Fixed;
        tree.Drives.ReadVolume = _ => ("Old label", "NTFS");
        tree.Drives.WriteLabel = (root, label) =>
        {
            lock (written)
            {
                written.Add((root, label, Environment.CurrentManagedThreadId == uiThread));
            }

            return 0;
        };
        var renamed = 0;
        void Count() => renamed++;
        DriveCommands.Renamed += Count;
        try
        {
            Command(tree.NodeCommands(drive, node: node), "Rename").Action();
            var editing = (node.IsEditing, node.Text);
            var edit = NativeMethods.SendMessage(tree.Handle, treeViewGetEditControl, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.SendMessage(edit, setText, IntPtr.Zero, "New label");
            node.EndEdit(false);
            await Pump.Until(() => renamed == 1);
            await Pump.Until(() => node.Text == shown);

            await Assert.That(editing).IsEqualTo((true, "Old label"));
            await Assert.That(written).IsEquivalentTo(new List<(string, string, bool)> { (drive, "New label", false) });
        }
        finally
        {
            DriveCommands.Renamed -= Count;
        }
    }

    // In This PC's list too, a drive selected on its own has its label edited, and given what
    // was typed. An optical drive's isn't edited at all.
    [Test]
    public async Task RenamingADriveInThisPCGivesItTheLabelTyped()
    {
        using var view = new FolderView(new(), noPlaces);
        _ = view.Handle;
        view.Navigate(Locations.ThisPC);
        await Pump.Loaded(view);
        // Whichever drive this machine lists first, none of which is asked anything.
        var root = view.Entries[0].FullPath;
        var written = new List<(string Root, string Label)>();
        view.Drives.TypeOf = _ => DriveType.CDRom;
        view.Drives.ReadVolume = _ => ("Old label", "NTFS");
        view.Drives.WriteLabel = (root, label) =>
        {
            lock (written)
            {
                written.Add((root, label));
            }

            return 0;
        };
        var renamed = 0;
        void Count() => renamed++;
        DriveCommands.Renamed += Count;
        try
        {
            view.List.SelectedIndices.Add(0);
            var optical = new LabelEditEventArgs(0);
            view.OnBeforeLabelEdit(null, optical);
            view.Drives.TypeOf = _ => DriveType.Fixed;
            var local = new LabelEditEventArgs(0);
            view.OnBeforeLabelEdit(null, local);
            view.OnAfterLabelEdit(null, new(0, "New label"));
            await Pump.Until(() => renamed == 1);

            await Assert.That(optical.CancelEdit).IsTrue();
            await Assert.That(local.CancelEdit).IsFalse();
            await Assert.That(written).IsEquivalentTo(new List<(string, string)> { (root, "New label") });
        }
        finally
        {
            DriveCommands.Renamed -= Count;
        }
    }

    // A label Windows wouldn't take is refused with why: one too long, or with characters its
    // file system doesn't allow, before Windows is asked, and otherwise what Windows refused it
    // for. Refused for want of an administrator, it asks to rename it as one, and does, behind
    // Windows' prompt, unless that is turned down. A drive that can't be read says so, and the
    // label it has already changes nothing.
    [Test]
    public async Task ARefusedRenameSaysWhy()
    {
        using var owner = new Control();
        var log = new List<string>();
        var answer = false;
        var outcome = new Elevated.Outcome(true, false, [], null);
        var error = 0;
        var fileSystem = "NTFS";
        Exception? unreadable = null;
        var drives = new DriveCommands
        {
            ReadVolume = _ => unreadable == null ? ("Backup", fileSystem) : throw unreadable,
            WriteLabel = (root, label) =>
            {
                lock (log)
                {
                    log.Add($"  written {root} {label}");
                }

                return error;
            },
            WriteLabelAsAdministrator = (root, label) =>
            {
                log.Add($"  written as administrator {root} {label}");
                return Task.FromResult(outcome);
            },
            Ask = (_, text) =>
            {
                log.Add($"  asked: {text}");
                return answer;
            },
            Tell = (_, text) => log.Add($"  told: {text}")
        };

        async Task Rename(string description, string label)
        {
            log.Add(description);
            var done = await drives.Rename(owner, drive, label);
            log.Add($"  renamed: {done}");
        }

        await Rename("NTFS, 33 characters", new('a', 33));
        await Rename("NTFS, 32 characters", new('a', 32));
        fileSystem = "FAT32";
        await Rename("FAT32, a dot", "MY.DRIVE");
        await Rename("FAT32, 12 characters", new('a', 12));
        fileSystem = "exFAT";
        await Rename("exFAT, 12 characters", new('a', 12));
        fileSystem = "NTFS";
        error = 19;
        await Rename("write-protected", "Photos");
        error = 5;
        await Rename("needs an administrator, declined", "Photos");
        answer = true;
        await Rename("needs an administrator, renamed as one", "Photos");
        outcome = new(false, true, [], null);
        await Rename("needs an administrator, prompt turned down", "Photos");
        outcome = new(false, false, [], "Access to the path 'Q:\\' is denied.");
        await Rename("needs an administrator, refused as one", "Photos");
        error = 0;
        await Rename("the label it has", "  Backup ");
        unreadable = new IOException("The device is not ready.");
        await Rename("not ready", "Photos");

        await Verify(log)
            .Snapshot(
                """
                [
                  NTFS, 33 characters,
                    told: Q: is formatted as NTFS, so its label can be at most 32 characters long.,
                    renamed: False,
                  NTFS, 32 characters,
                    written Q:\ aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,
                    renamed: True,
                  FAT32, a dot,
                    told: Q: is formatted as FAT32, so its label can be at most 11 characters long, and can't contain any of these characters:  * ? / \ | . , ; : + = [ ] < > ",
                    renamed: False,
                  FAT32, 12 characters,
                    told: Q: is formatted as FAT32, so its label can be at most 11 characters long, and can't contain any of these characters:  * ? / \ | . , ; : + = [ ] < > ",
                    renamed: False,
                  exFAT, 12 characters,
                    told: Q: is formatted as exFAT, so its label can be at most 11 characters long.,
                    renamed: False,
                  write-protected,
                    written Q:\ Photos,
                    told: Q: couldn't be renamed. The media is write protected.,
                    renamed: False,
                  needs an administrator, declined,
                    written Q:\ Photos,
                    asked: Windows needs administrator permission to rename Q:. Rename it as administrator?,
                    renamed: False,
                  needs an administrator, renamed as one,
                    written Q:\ Photos,
                    asked: Windows needs administrator permission to rename Q:. Rename it as administrator?,
                    written as administrator Q:\ Photos,
                    renamed: True,
                  needs an administrator, prompt turned down,
                    written Q:\ Photos,
                    asked: Windows needs administrator permission to rename Q:. Rename it as administrator?,
                    written as administrator Q:\ Photos,
                    renamed: False,
                  needs an administrator, refused as one,
                    written Q:\ Photos,
                    asked: Windows needs administrator permission to rename Q:. Rename it as administrator?,
                    written as administrator Q:\ Photos,
                    told: Q: couldn't be renamed. Access to the path 'Q:\' is denied.,
                    renamed: False,
                  the label it has,
                    renamed: False,
                  not ready,
                    told: Q: can't be renamed. The device is not ready.,
                    renamed: False
                ]
                """);
    }

    // A quote in the label is doubled, so it can't end the string early.
    [Test]
    public Task TheScriptThatRenamesAsAdministrator() =>
        Verify(DriveCommands.LabelScript(drive, "Bob's", @"C:\Temp\result.txt"))
            .Snapshot(
                """
                $ErrorActionPreference = 'Stop'
                $result = 'C:\Temp\result.txt'
                try {
                    (New-Object System.IO.DriveInfo 'Q:\').VolumeLabel = 'Bob''s'
                    exit 0
                }
                catch {
                    $exception = $_.Exception
                    if ($exception.InnerException) {
                        $exception = $exception.InnerException
                    }
                    Set-Content -LiteralPath $result -Value ('error: ' + $exception.Message) -Encoding UTF8
                    exit 1
                }
                """);

    // TVM_GETEDITCONTROL and WM_SETTEXT: typing a label into the box the tree edits it in.
    const int treeViewGetEditControl = 0x110F;
    const int setText = 0x000C;

    // This PC holds the one drive, which no drive on the machine is, and is open.
    static FolderTree CreateTree(string[]? pinned = null)
    {
        var tree = new FolderTree(
            new()
            {
                ShowHidden = false
            },
            new(pinned ?? [], [], ShowComputer: true, _ => "", [drive]))
        {
            Size = new(300, 400)
        };
        _ = tree.Handle;
        return tree;
    }

    static TreeNode DriveNode(FolderTree tree) =>
        tree.Nodes.Cast<TreeNode>().Last().Nodes[0];

    static QuickMenu.Command Command(IEnumerable<QuickMenu.Command?> commands, string text) =>
        commands.Single(_ => _?.Text == text)!;

    // Rename, Eject and Disconnect, as the menu has them.
    static string Offered(IEnumerable<QuickMenu.Command?> commands) =>
        string.Join(
            ", ",
            commands
                .Where(_ => _ is {Text: "Rename" or "Eject" or "Disconnect"})
                .Select(_ => _!.Enabled ? _.Text : $"{_.Text} (disabled)"));
}
