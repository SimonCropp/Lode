public class FileOperationsTests
{
    // Several folders open as tabs of one Windows Terminal window.
    [Test]
    public async Task TerminalArguments()
    {
        await Assert.That(FileOperations.TerminalArguments([new(@"C:\Code\Lode", [])])).IsEquivalentTo(["-d", @"C:\Code\Lode"], CollectionOrdering.Matching);
        await Assert.That(FileOperations.TerminalArguments([new(@"C:\Code\Lode", []), new(@"C:\Code\Two Words", [])]))
            .IsEquivalentTo(["-d", @"C:\Code\Lode", ";", "new-tab", "-d", @"C:\Code\Two Words"], CollectionOrdering.Matching);
    }

    // Windows Terminal would take a semicolon in a folder's name, or a command's, as the start
    // of another tab.
    [Test]
    public async Task TerminalArgumentsEscapeSemicolons() =>
        await Assert.That(FileOperations.TerminalArguments([new(@"C:\Code\A;B", [@"C:\Tools;\run.exe", "-x"])]))
            .IsEquivalentTo(["-d", @"C:\Code\A\;B", @"C:\Tools\;\run.exe", "-x"], CollectionOrdering.Matching);

    // Without Windows Terminal, a command prompt reads the line itself, where an & in a folder's
    // name would end the cd there and run the rest as a command.
    [Test]
    public async Task PromptArguments()
    {
        await Assert.That(FileOperations.PromptArguments(new(@"C:\Code\Lode", []))).IsEqualTo(@"/k cd /d C:\Code\Lode");
        await Assert.That(FileOperations.PromptArguments(new(@"D:\Code\R&D", [@"C:\Tools\agent.cmd"]))).IsEqualTo("""/k cd /d "D:\Code\R&D" && C:\Tools\agent.cmd""");
        await Assert.That(FileOperations.PromptArguments(new(@"D:\Two Words", [@"C:\Program Files\agent.exe", "-x"]))).IsEqualTo("""/k cd /d "D:\Two Words" && "C:\Program Files\agent.exe" -x""");
    }

    // What cmd makes of it: the prompt ends up in the folder, and the command runs there.
    [Test]
    public async Task PromptGoesToAFolderWithAnAmpersand()
    {
        using var fixture = Fixture.Create();
        var folder = fixture.At("R&D");
        Directory.CreateDirectory(folder);
        var arguments = FileOperations.PromptArguments(new(folder, ["cmd.exe", "/c", "cd"]));

        // /c rather than /k, so the prompt closes once the command has run.
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c{arguments[2..]}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fixture.Root
        };
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(output.Trim()).IsEqualTo(folder);
    }

    [Test]
    public async Task CopyToAnotherFolder()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("report2.beta"), fixture.At("Source")];

        await FileOperations.Transfer(IntPtr.Zero, sources, fixture.At("Archive"), move: false);

        await Verify(fixture.Tree())
            .Snapshot(
                """
                [
                  Projects/Archive/,
                  Projects/Archive/Source/,
                  Projects/Archive/Source/Deep/,
                  Projects/Archive/Source/Deep/leaf.aleph (40 bytes),
                  Projects/Archive/Source/main.aleph (5,000 bytes),
                  Projects/Archive/notes.aleph (12 bytes),
                  Projects/Archive/report2.beta (2,048 bytes),
                  Projects/Cache/,
                  Projects/Docs/,
                  Projects/Docs/guide.aleph (900 bytes),
                  Projects/Report1.beta (1,500,000 bytes),
                  Projects/Source/,
                  Projects/Source/Deep/,
                  Projects/Source/Deep/leaf.aleph (40 bytes),
                  Projects/Source/main.aleph (5,000 bytes),
                  Projects/hidden.aleph (64 bytes),
                  Projects/notes.aleph (12 bytes),
                  Projects/report10.beta (300,000 bytes),
                  Projects/report2.beta (2,048 bytes),
                  Projects/summary.gamma (0 bytes)
                ]
                """);
    }

    // Windows reports a folder copied and then each thing in it. What was done, for undoing it,
    // is the folder alone: the files in it go wherever it goes.
    [Test]
    public async Task CopyingAFolderIsOneChange()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("Source"), fixture.At("notes.aleph")];

        var outcome = await FileOperations.OnStaThread(
            () => ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Copy, sources, fixture.At("Archive"), 0));

        await Assert.That(outcome.Changes).IsEquivalentTo<IReadOnlyList<ShellFileOperation.Change>, ShellFileOperation.Change>(
            [
                new(fixture.At("Source"), fixture.At("Archive", "Source")),
                new(fixture.At("notes.aleph"), fixture.At("Archive", "notes.aleph"))
            ],
            CollectionOrdering.Matching);
        await Assert.That(File.Exists(fixture.At("Archive", "Source", "Deep", "leaf.aleph"))).IsTrue();
    }

    // A folder copied into one of the same name already there merges into it. That folder is
    // not new, so the changes are what the copy brought, item by item, and undoing the copy
    // leaves what was there before. A folder inside merges too, where the one there has one of
    // its name.
    [Test]
    public async Task CopyingAFolderIntoOneOfTheSameNameMergesIt()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Archive", "Source", "Deep"));
        await File.WriteAllTextAsync(fixture.At("Archive", "Source", "existing.aleph"), "there before");

        var outcome = await FileOperations.OnStaThread(
            () => ShellFileOperation.Run(
                IntPtr.Zero,
                ShellFileOperation.Kind.Copy,
                [fixture.At("Source")],
                fixture.At("Archive"),
                ShellFileOperation.NoConfirmation));

        await Verify(
            new
            {
                outcome.Succeeded,
                changes = outcome.Changes.Select(_ => $"{fixture.Relative(_.Source)} -> {fixture.Relative(_.Result!)}"),
                merged = outcome.Merged.Select(_ => $"{fixture.Relative(_.Source)} -> {fixture.Relative(_.Result!)}")
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  changes: [
                    Projects/Source/main.aleph -> Projects/Archive/Source/main.aleph,
                    Projects/Source/Deep/leaf.aleph -> Projects/Archive/Source/Deep/leaf.aleph
                  ],
                  merged: [
                    Projects/Source -> Projects/Archive/Source,
                    Projects/Source/Deep -> Projects/Archive/Source/Deep
                  ]
                }
                """);
    }

    // A folder renamed to the name of one beside it merges into it, which Windows does as a
    // move of what is in it.
    [Test]
    public async Task RenamingAFolderToOneBesideItMergesIt()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Deep"));
        Directory.CreateDirectory(fixture.At("Archive", "Deep"));

        var outcome = ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Rename, [fixture.At("Docs")], "Archive", ShellFileOperation.NoConfirmation);

        await Verify(
            new
            {
                outcome.Succeeded,
                changes = outcome.Changes.Select(_ => $"{fixture.Relative(_.Source)} -> {fixture.Relative(_.Result!)}"),
                merged = outcome.Merged.Select(_ => $"{fixture.Relative(_.Source)} -> {fixture.Relative(_.Result!)}"),
                tree = fixture.Tree()
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  changes: [
                    Projects/Docs/guide.aleph -> Projects/Archive/guide.aleph
                  ],
                  merged: [
                    Projects/Docs -> Projects/Archive,
                    Projects/Docs/Deep -> Projects/Archive/Deep
                  ],
                  tree: [
                    Projects/Archive/,
                    Projects/Archive/Deep/,
                    Projects/Archive/guide.aleph (900 bytes),
                    Projects/Cache/,
                    Projects/Report1.beta (1,500,000 bytes),
                    Projects/Source/,
                    Projects/Source/Deep/,
                    Projects/Source/Deep/leaf.aleph (40 bytes),
                    Projects/Source/main.aleph (5,000 bytes),
                    Projects/hidden.aleph (64 bytes),
                    Projects/notes.aleph (12 bytes),
                    Projects/report10.beta (300,000 bytes),
                    Projects/report2.beta (2,048 bytes),
                    Projects/summary.gamma (0 bytes)
                  ]
                }
                """);
    }

    // Asked for something that is not there, Windows does none of it. One gone since it was
    // picked is left out, and the rest are done.
    [Test]
    public async Task WhatIsNotThereIsLeftOut()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("gone.aleph"), fixture.At("Gone")];

        var outcome = await FileOperations.OnStaThread(
            () => ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Copy, sources, fixture.At("Archive"), 0));

        await Assert.That(outcome.Succeeded).IsTrue();
        await Assert.That(outcome.Missing).IsEquivalentTo([fixture.At("gone.aleph"), fixture.At("Gone")], CollectionOrdering.Matching);
        await Assert.That(outcome.Changes).IsEquivalentTo<IReadOnlyList<ShellFileOperation.Change>, ShellFileOperation.Change>(
            [new(fixture.At("notes.aleph"), fixture.At("Archive", "notes.aleph"))],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task NothingThereDoesNothing()
    {
        using var fixture = Fixture.Create();

        var outcome = await FileOperations.OnStaThread(
            () => ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Move, [fixture.At("gone.aleph")], fixture.At("Archive"), 0));

        await Assert.That(outcome.Changes).IsEmpty();
        await Assert.That(outcome.Missing).IsEquivalentTo([fixture.At("gone.aleph")]);
    }

    // Nothing can go into a folder that has gone, so none of it is done, and it says which.
    [Test]
    public async Task AFolderToGoIntoThatHasGoneDoesNothing()
    {
        using var fixture = Fixture.Create();

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => FileOperations.OnStaThread(
                () => ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Copy, [fixture.At("notes.aleph")], fixture.At("Gone"), 0)));

        await Assert.That(exception!.Message).IsEqualTo($"Lode can't find the folder '{fixture.At("Gone")}'.");
        await Assert.That(fixture.Tree()).DoesNotContain("Projects/Gone/");
    }

    [Test]
    public Task MissingMessage() =>
        Verify(
            new
            {
                none = FileOperations.MissingMessage(ShellFileOperation.Kind.Copy, []),
                one = FileOperations.MissingMessage(ShellFileOperation.Kind.Move, [@"C:\Work\a.txt"]),
                drive = FileOperations.MissingMessage(ShellFileOperation.Kind.Copy, [@"Z:\"]),
                two = FileOperations.MissingMessage(ShellFileOperation.Kind.Delete, [@"C:\Work\a.txt", @"C:\Work\Folder"]),
                many = FileOperations.MissingMessage(ShellFileOperation.Kind.Copy, Enumerable.Range(1, 12).Select(_ => $@"C:\Work\{_}.txt").ToList())
            })
            .Snapshot(
                """
                {
                  one: Lode can't find 'a.txt', so it wasn't moved.,
                  drive: Lode can't find 'Z:\', so it wasn't copied.,
                  two:
                Lode can't find these 2 items, so they weren't deleted:

                  a.txt
                  Folder,
                  many:
                Lode can't find these 12 items, so they weren't copied:

                  1.txt
                  2.txt
                  3.txt
                  4.txt
                  5.txt
                  6.txt
                  7.txt
                  8.txt
                  9.txt
                  10.txt
                  and 2 more
                }
                """);

    // A file picked beside the folder it is in, as from a search, is copied twice: with the
    // folder, and by itself, which is a change of its own.
    [Test]
    public async Task CopyingAFolderAndAFileInItIsTwoChanges()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("Source"), fixture.At("Source", "main.aleph")];

        var outcome = await FileOperations.OnStaThread(
            () => ShellFileOperation.Run(IntPtr.Zero, ShellFileOperation.Kind.Copy, sources, fixture.At("Archive"), 0));

        await Assert.That(outcome.Changes).IsEquivalentTo<IReadOnlyList<ShellFileOperation.Change>, ShellFileOperation.Change>(
            [
                new(fixture.At("Source"), fixture.At("Archive", "Source")),
                new(fixture.At("Source", "main.aleph"), fixture.At("Archive", "main.aleph"))
            ],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task CopyIntoSameFolderMakesDuplicate()
    {
        using var fixture = Fixture.Create();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("notes.aleph")], fixture.Root, move: false);

        await Verify(fixture.Tree())
            .Snapshot(
                """
                [
                  Projects/Archive/,
                  Projects/Cache/,
                  Projects/Docs/,
                  Projects/Docs/guide.aleph (900 bytes),
                  Projects/Report1.beta (1,500,000 bytes),
                  Projects/Source/,
                  Projects/Source/Deep/,
                  Projects/Source/Deep/leaf.aleph (40 bytes),
                  Projects/Source/main.aleph (5,000 bytes),
                  Projects/hidden.aleph (64 bytes),
                  Projects/notes - Copy.aleph (12 bytes),
                  Projects/notes.aleph (12 bytes),
                  Projects/report10.beta (300,000 bytes),
                  Projects/report2.beta (2,048 bytes),
                  Projects/summary.gamma (0 bytes)
                ]
                """);
    }

    // Pasted with a copy of one already in the folder, items from elsewhere, a search say, keep
    // the shell's prompt for a clash: only the one in the folder becomes "- Copy".
    [Test]
    public async Task OnlyWhatIsInTheFolderAlreadyIsRenamedOnACopy()
    {
        const uint rename = ShellFileOperation.RenameOnCollision;
        string[] sources = [@"C:\Work\a.txt", @"D:\Elsewhere\b.txt", @"C:\Work\Deep\c.txt"];

        var copied = FileOperations.Batches(sources, @"C:\Work", move: false)
            .Select(_ => (string.Join(", ", _.Sources), (_.Flags & rename) != 0));
        var moved = FileOperations.Batches(sources, @"C:\Work", move: true)
            .Select(_ => (string.Join(", ", _.Sources), (_.Flags & rename) != 0));

        await Assert.That(copied).IsEquivalentTo(
            [
                (@"C:\Work\a.txt", true),
                (@"D:\Elsewhere\b.txt, C:\Work\Deep\c.txt", false)
            ],
            CollectionOrdering.Matching);
        await Assert.That(moved).IsEquivalentTo(
            [(@"D:\Elsewhere\b.txt, C:\Work\Deep\c.txt", false)],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task MoveToAnotherFolder()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("Docs")];

        await FileOperations.Transfer(IntPtr.Zero, sources, fixture.At("Archive"), move: true);

        await Verify(fixture.Tree())
            .Snapshot(
                """
                [
                  Projects/Archive/,
                  Projects/Archive/Docs/,
                  Projects/Archive/Docs/guide.aleph (900 bytes),
                  Projects/Archive/notes.aleph (12 bytes),
                  Projects/Cache/,
                  Projects/Report1.beta (1,500,000 bytes),
                  Projects/Source/,
                  Projects/Source/Deep/,
                  Projects/Source/Deep/leaf.aleph (40 bytes),
                  Projects/Source/main.aleph (5,000 bytes),
                  Projects/hidden.aleph (64 bytes),
                  Projects/report10.beta (300,000 bytes),
                  Projects/report2.beta (2,048 bytes),
                  Projects/summary.gamma (0 bytes)
                ]
                """);
    }

    // Windows won't move or rename a folder while anything beneath it is watched, as a tab
    // showing a folder inside it, or a folder expanded beneath it in the tree, is. Lode lets go
    // of its own watches for the length of its own operation, and finds the folder gone after.
    [Test]
    public async Task MoveAndRenameAFolderWithOneInsideItWatched()
    {
        using var fixture = Fixture.Create();
        using var inSource = new FolderWatcher(fixture.At("Source", "Deep"), NotifyFilters.DirectoryName, () => { });
        using var inDocs = new FolderWatcher(fixture.At("Docs"), NotifyFilters.DirectoryName, () => { });
        Directory.CreateDirectory(fixture.At("Docs", "Drafts"));
        using var inDrafts = new FolderWatcher(fixture.At("Docs", "Drafts"), NotifyFilters.DirectoryName, () => { });

        var moved = await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Source")], fixture.At("Archive"), move: true);
        var renamed = FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");

        await Verify(
            new
            {
                moved,
                renamed,
                stillWatching = new
                {
                    inSource = inSource.IsWatching,
                    inDocs = inDocs.IsWatching,
                    inDrafts = inDrafts.IsWatching
                },
                tree = fixture.Tree()
            })
            .Snapshot(
                """
                {
                  moved: true,
                  renamed: true,
                  stillWatching: {
                    inSource: false,
                    inDocs: false,
                    inDrafts: false
                  },
                  tree: [
                    Projects/Archive/,
                    Projects/Archive/Source/,
                    Projects/Archive/Source/Deep/,
                    Projects/Archive/Source/Deep/leaf.aleph (40 bytes),
                    Projects/Archive/Source/main.aleph (5,000 bytes),
                    Projects/Cache/,
                    Projects/Documents/,
                    Projects/Documents/Drafts/,
                    Projects/Documents/guide.aleph (900 bytes),
                    Projects/Report1.beta (1,500,000 bytes),
                    Projects/hidden.aleph (64 bytes),
                    Projects/notes.aleph (12 bytes),
                    Projects/report10.beta (300,000 bytes),
                    Projects/report2.beta (2,048 bytes),
                    Projects/summary.gamma (0 bytes)
                  ]
                }
                """);
    }

    [Test]
    public async Task MoveIntoSameFolderChangesNothing()
    {
        using var fixture = Fixture.Create();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("notes.aleph")], fixture.Root, move: true);

        await Verify(fixture.Tree())
            .Snapshot(
                """
                [
                  Projects/Archive/,
                  Projects/Cache/,
                  Projects/Docs/,
                  Projects/Docs/guide.aleph (900 bytes),
                  Projects/Report1.beta (1,500,000 bytes),
                  Projects/Source/,
                  Projects/Source/Deep/,
                  Projects/Source/Deep/leaf.aleph (40 bytes),
                  Projects/Source/main.aleph (5,000 bytes),
                  Projects/hidden.aleph (64 bytes),
                  Projects/notes.aleph (12 bytes),
                  Projects/report10.beta (300,000 bytes),
                  Projects/report2.beta (2,048 bytes),
                  Projects/summary.gamma (0 bytes)
                ]
                """);
    }

    // Delete has no test: recycling would leave files in the Recycle Bin of whoever ran the
    // tests, and a permanent delete through the shell stops to ask for confirmation.

    [Test]
    public async Task Rename()
    {
        using var fixture = Fixture.Create();

        var file = FileOperations.Rename(IntPtr.Zero, fixture.At("notes.aleph"), "renamed.aleph");
        var caseOnly = FileOperations.Rename(IntPtr.Zero, fixture.At("report2.beta"), "REPORT2.BETA");
        var folder = FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");

        await Verify(
            new
            {
                file,
                caseOnly,
                folder,
                tree = fixture.Tree()
            })
            .Snapshot(
                """
                {
                  file: true,
                  caseOnly: true,
                  folder: true,
                  tree: [
                    Projects/Archive/,
                    Projects/Cache/,
                    Projects/Documents/,
                    Projects/Documents/guide.aleph (900 bytes),
                    Projects/REPORT2.BETA (2,048 bytes),
                    Projects/Report1.beta (1,500,000 bytes),
                    Projects/Source/,
                    Projects/Source/Deep/,
                    Projects/Source/Deep/leaf.aleph (40 bytes),
                    Projects/Source/main.aleph (5,000 bytes),
                    Projects/hidden.aleph (64 bytes),
                    Projects/renamed.aleph (12 bytes),
                    Projects/report10.beta (300,000 bytes),
                    Projects/summary.gamma (0 bytes)
                  ]
                }
                """);
    }

    [Test]
    public async Task CreateFolderPicksFreeName()
    {
        using var fixture = Fixture.Create();

        string[] created =
        [
            FileOperations.CreateFolder(fixture.Root),
            FileOperations.CreateFolder(fixture.Root),
            FileOperations.CreateFolder(fixture.Root)
        ];

        await Verify(created.Select(fixture.Relative))
            .Snapshot(
                """
                [
                  Projects/New folder,
                  Projects/New folder (2),
                  Projects/New folder (3)
                ]
                """);
    }

    [Test]
    public async Task CreateFilePicksFreeNameAndWritesData()
    {
        using var fixture = Fixture.Create();
        var empty = new ShellNew.Entry(".txt", "Text Document");
        var withData = new ShellNew.Entry(".rtf", "Rich Text Document", @"{\rtf1}"u8.ToArray());

        string[] created =
        [
            FileOperations.CreateFile(fixture.Root, empty),
            FileOperations.CreateFile(fixture.Root, empty),
            FileOperations.CreateFile(fixture.Root, withData)
        ];

        await Verify(created.Select(_ => new
        {
            path = fixture.Relative(_),
            content = File.ReadAllText(_)
        }))
        .Snapshot(
            """
            [
              {
                path: Projects/New Text Document.txt,
                content: 
              },
              {
                path: Projects/New Text Document (2).txt,
                content: 
              },
              {
                path: Projects/New Rich Text Document.rtf,
                content: {\rtf1}
              }
            ]
            """);
    }

    [Test]
    public async Task CreateShortcutPicksFreeName()
    {
        using var fixture = Fixture.Create();

        string[] created =
        [
            FileOperations.CreateShortcut(fixture.At("notes.aleph")),
            FileOperations.CreateShortcut(fixture.At("notes.aleph")),
            FileOperations.CreateShortcut(fixture.At("Docs"))
        ];

        await Verify(created.Select(_ => new
        {
            path = fixture.Relative(_),
            written = new FileInfo(_).Length > 0
        }))
        .Snapshot(
            """
            [
              {
                path: Projects/notes - Shortcut.lnk,
                written: true
              },
              {
                path: Projects/notes - Shortcut (2).lnk,
                written: true
              },
              {
                path: Projects/Docs - Shortcut.lnk,
                written: true
              }
            ]
            """);
    }

    // Past the old 260 character limit, which SHFileOperation could not go beyond.
    [Test]
    public async Task LongPaths()
    {
        using var fixture = Fixture.Create();
        var deep = fixture.Root;
        for (var level = 0; deep.Length < 300; level++)
        {
            deep = Path.Combine(deep, $"a folder with a long name to get past the limit {level}");
        }

        var source = Path.Combine(deep, "source");
        var target = Path.Combine(deep, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var file = Path.Combine(source, "file.txt");
        await File.WriteAllTextAsync(file, "content");

        var copied = await FileOperations.Transfer(IntPtr.Zero, [file], target, move: false);
        var renamed = FileOperations.Rename(IntPtr.Zero, Path.Combine(target, "file.txt"), "renamed.txt");
        var moved = await FileOperations.Transfer(IntPtr.Zero, [file], target, move: true);

        await Verify(
            new
            {
                pathLength = file.Length > 260,
                copied,
                renamed,
                moved,
                source = Directory.GetFileSystemEntries(source).Select(Path.GetFileName),
                target = Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Order()
            })
            .Snapshot(
                """
                {
                  pathLength: true,
                  copied: true,
                  renamed: true,
                  moved: true,
                  target: [
                    file.txt,
                    renamed.txt
                  ]
                }
                """);
    }
}
