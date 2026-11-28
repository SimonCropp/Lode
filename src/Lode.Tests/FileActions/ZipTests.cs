

// The Actions panel's log, which compressing and extracting add to, is shared with the window
// tests, so these take turns with them.
[NotInParallel("windows")]
public class ZipTests
{
    [After(Test)]
    public void ClearActions() =>
        ActionLog.Reset();

    static List<string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries
            .Select(_ => $"{_.FullName} ({_.Length:N0} bytes)")
            .ToList();
    }

    // A folder goes in under its own name with all it holds, a folder with nothing in it as an
    // entry of its own, and files beside it at the top.
    [Test]
    public async Task CompressFilesAndFolders()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Source", "Empty"));
        var zip = fixture.At("Source.zip");

        var outcome = Zip.Compress(zip, [fixture.At("Source"), fixture.At("notes.aleph")], null, Cancel.None);

        await Verify(
            new
            {
                outcome.Succeeded,
                outcome.Summary,
                entries = Entries(zip)
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: Compressed 3 files into Source.zip,
                  entries: [
                    Source/Deep/leaf.aleph (40 bytes),
                    Source/Empty/ (0 bytes),
                    Source/main.aleph (5,000 bytes),
                    notes.aleph (12 bytes)
                  ]
                }
                """);
    }

    // Named for the first item, as Explorer names it: a file without its extension, a folder by
    // its name, then numbered.
    [Test]
    public async Task CompressPicksFreeName()
    {
        using var fixture = Fixture.Create();

        var first = await Zip.CompressInBackground([fixture.At("notes.aleph"), fixture.At("Docs")], fixture.Root);
        var second = await Zip.CompressInBackground([fixture.At("notes.aleph")], fixture.Root);
        var folder = await Zip.CompressInBackground([fixture.At("Docs")], fixture.Root);

        await Assert.That(new[] { first, second, folder }.Select(_ => Path.GetFileName(_!)))
            .IsEquivalentTo(["notes.zip", "notes (2).zip", "Docs.zip"], CollectionOrdering.Matching);
    }

    // A zip of the folder it is made in leaves itself out.
    [Test]
    public async Task CompressLeavesOutTheZipBeingMade()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Docs", "Docs.zip");

        Zip.Compress(zip, [fixture.At("Docs")], null, Cancel.None);

        await Assert.That(Entries(zip)).IsEquivalentTo(["Docs/guide.aleph (900 bytes)"]);
    }

    // A junction leads elsewhere, here back up to the folder being compressed, so is left out,
    // while the folder beside it goes in with what it holds. What cloud storage marks as a
    // junction is marked, though it is no link, can't be made here.
    [Test]
    public async Task CompressLeavesOutLinks()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Inner"));
        await File.WriteAllTextAsync(fixture.At("Docs", "Inner", "deep.aleph"), "deep");
        var link = fixture.At("Docs", "Loop");
        using (var junction = Process.Start(
                   new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{fixture.At("Docs")}\"")
                   {
                       CreateNoWindow = true,
                       UseShellExecute = false
                   })!)
        {
            await junction.WaitForExitAsync();
        }

        var zip = fixture.At("Docs.zip");

        Zip.Compress(zip, [fixture.At("Docs")], null, Cancel.None);

        await Assert.That(Directory.Exists(Path.Combine(link, "Inner"))).IsTrue();
        await Assert.That(Entries(zip)).IsEquivalentTo(["Docs/Inner/deep.aleph (4 bytes)", "Docs/guide.aleph (900 bytes)"]);
    }

    // Items found in more than one folder can share a name. The zip holds each of them, those
    // after the first numbered, rather than two entries of the one name.
    [Test]
    public async Task CompressNumbersItemsThatShareAName()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("One", "Same"));
        Directory.CreateDirectory(fixture.At("Two", "Same"));
        await File.WriteAllTextAsync(fixture.At("One", "same.txt"), "one");
        await File.WriteAllTextAsync(fixture.At("Two", "same.txt"), "second");
        await File.WriteAllTextAsync(fixture.At("Two", "Same", "inner.txt"), "inner");
        var zip = fixture.At("Same.zip");

        Zip.Compress(zip, [fixture.At("One", "same.txt"), fixture.At("Two", "same.txt"), fixture.At("One", "Same"), fixture.At("Two", "Same")], null, Cancel.None);

        await Assert.That(Entries(zip)).IsEquivalentTo(["same.txt (3 bytes)", "same (2).txt (6 bytes)", "Same/ (0 bytes)", "Same (2)/inner.txt (5 bytes)"]);
    }

    // A zip made where case tells names apart can hold two entries that are the one file here.
    // The first is extracted, the one shown when the zip is gone into, and the other said to
    // be left out, rather than the second failing all of it.
    [Test]
    public async Task ExtractKeepsTheFirstOfEntriesThatAreOneFileHere()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Target"));
        var zip = MakeZip(fixture, "Case.zip", ("Makefile", "first"), ("makefile", "second"), ("other.txt", "other"));

        var extraction = Zip.Extract(zip, fixture.At("Target"), null, Cancel.None);

        await Assert.That(extraction.LeftOut).IsEquivalentTo([new Zip.LeftOutEntry("makefile", Zip.LeftOutReason.Duplicate)]);
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Target", "Makefile"))).IsEqualTo("first");
        await Assert.That(File.Exists(fixture.At("Target", "other.txt"))).IsTrue();
    }

    // A zip made where case tells names apart can hold a file and a folder that are one name
    // here, README and readme/notes.txt say, the file before the folder or after it. The folder
    // is extracted, as it is the one shown when the zip is gone into, and the file said to be
    // left out for its name, as the second of two files of one name is, rather than writing one
    // failing on the other, and all that was written being taken away.
    [Test]
    public async Task ExtractKeepsTheFolderOfAFileAndFolderThatAreOneHere()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(
            fixture,
            "Case.zip",
            ("README", "file"),
            ("readme/notes.txt", "notes"),
            ("Makefile", "first"),
            ("makefile", "second"),
            ("docs/guide.txt", "guide"),
            ("DOCS", "file after"),
            ("Empty/", ""),
            ("EMPTY", "file of the folder's own entry"),
            ("other.txt", "other"));

        var (outcome, _) = Zip.ExtractTo(zip, fixture.Root, false, null, Cancel.None);

        await Verify(
            new
            {
                outcome.Succeeded,
                outcome.Summary,
                leftOut = outcome.Details.Split(Environment.NewLine).Skip(1),
                tree = fixture.Tree().Where(_ => _.StartsWith("Projects/Case/", StringComparison.Ordinal))
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: Extracted Case.zip, leaving out 4 items,
                  leftOut: [
                    ,
                    Left out, being named as another item in the zip is, but for case, which Windows takes for the same name:,
                    README,
                    makefile,
                    DOCS,
                    EMPTY
                  ],
                  tree: [
                    Projects/Case/,
                    Projects/Case/Empty/,
                    Projects/Case/Makefile (5 bytes),
                    Projects/Case/docs/,
                    Projects/Case/docs/guide.txt (5 bytes),
                    Projects/Case/other.txt (5 bytes),
                    Projects/Case/readme/,
                    Projects/Case/readme/notes.txt (5 bytes)
                  ]
                }
                """);
    }

    // Entries that don't say how their names are written. A zip made on a Mac, or by Linux's
    // zip, names them in UTF-8 without saying so; one of Windows' own, in the code page of
    // programs that know no Unicode. Each is read as what it is.
    [Test]
    public async Task EntryNamesThatDoNotSayHowTheyAreWritten()
    {
        var unsaid = CodePagesEncodingProvider.Instance.GetEncoding((int) NativeMethods.GetOEMCP());
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Target"));
        var asUtf8 = fixture.At("utf8.zip");
        using (var archive = await ZipFile.OpenAsync(asUtf8, ZipArchiveMode.Create, Encoding.Latin1))
        {
            archive.CreateEntry(Encoding.Latin1.GetString("naïve.txt"u8.ToArray()));
        }

        Zip.Extract(asUtf8, fixture.At("Target"), null, Cancel.None);

        // A machine set to UTF-8 throughout has no other code page to name them in.
        if (unsaid != null)
        {
            var inCodePage = fixture.At("codepage.zip");
            using (var archive = await ZipFile.OpenAsync(inCodePage, ZipArchiveMode.Create, unsaid))
            {
                archive.CreateEntry("café.txt");
            }

            Zip.Extract(inCodePage, fixture.At("Target"), null, Cancel.None);

            await Assert.That(File.Exists(fixture.At("Target", "café.txt"))).IsTrue();
        }

        await Assert.That(File.Exists(fixture.At("Target", "naïve.txt"))).IsTrue();
    }

    // Stopped, or failed, nothing is left of the zip.
    [Test]
    public async Task CancelledCompressLeavesNoZip()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Source.zip");
        using var cancellation = new CancelSource();
        await cancellation.CancelAsync();

        await Assert.That(() => Zip.Compress(zip, [fixture.At("Source")], null, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    static string MakeZip(Fixture fixture, string name, params (string Entry, string Content)[] entries)
    {
        var zip = fixture.At(name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var created = archive.CreateEntry(entry);
            if (content.Length == 0)
            {
                continue;
            }

            using var writer = new StreamWriter(created.Open());
            writer.Write(content);
        }

        return zip;
    }

    // Extract All makes a folder named for the zip, numbered when there is one already.
    [Test]
    public async Task ExtractAllMakesAFolderNamedForTheZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("top.txt", "top"), ("Inner/deep.txt", "deep"), ("Empty/", ""));
        Directory.CreateDirectory(fixture.At("Bundle"));

        var (outcome, topLevel) = Zip.ExtractTo(zip, fixture.Root, false, null, Cancel.None);

        await Verify(
            new
            {
                outcome.Succeeded,
                outcome.Summary,
                topLevel = topLevel.Select(fixture.Relative),
                tree = fixture.Tree().Where(_ => _.StartsWith("Projects/Bundle", StringComparison.Ordinal))
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: Extracted Bundle.zip,
                  topLevel: [
                    Projects/Bundle (2)
                  ],
                  tree: [
                    Projects/Bundle (2)/,
                    Projects/Bundle (2)/Empty/,
                    Projects/Bundle (2)/Inner/,
                    Projects/Bundle (2)/Inner/deep.txt (4 bytes),
                    Projects/Bundle (2)/top.txt (3 bytes),
                    Projects/Bundle.zip (327 bytes),
                    Projects/Bundle/
                  ]
                }
                """);
    }

    /// <summary>
    /// The shell's move, done for real but with no dialog, for <see cref="Zip.MoveIntoPlace"/>:
    /// each item moved where nothing of its name is, a folder merged into one of its name there,
    /// item by item, then emptied and deleted, as Windows does, and each file there already
    /// replaced, or skipped, as answered. What it was asked to move, and where, is kept, and
    /// whether what it moved from was hidden.
    /// </summary>
    internal sealed class ShellMove(bool replace)
    {
        public List<(IReadOnlyList<string> Sources, string Destination, bool FromHidden)> Asked { get; } = [];

        public ShellFileOperation.Outcome Move(IntPtr owner, IReadOnlyList<string> sources, string destination)
        {
            var from = Path.GetDirectoryName(sources[0])!;
            Asked.Add((sources, destination, File.GetAttributes(from).HasFlag(FileAttributes.Hidden)));
            var changes = new List<ShellFileOperation.Change>();
            var merged = new List<ShellFileOperation.Change>();
            foreach (var source in sources)
            {
                Into(source, destination);
            }

            return new(true, changes, merged, []);

            void Into(string source, string folder)
            {
                var target = Path.Combine(folder, Path.GetFileName(source));
                if (Directory.Exists(source))
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(source, target);
                        changes.Add(new(source, target));
                        return;
                    }

                    merged.Add(new(source, target));
                    foreach (var inner in Directory.GetFileSystemEntries(source))
                    {
                        Into(inner, target);
                    }

                    if (!Directory.EnumerateFileSystemEntries(source).Any())
                    {
                        Directory.Delete(source);
                    }

                    return;
                }

                if (File.Exists(target) &&
                    !replace)
                {
                    return;
                }

                File.Move(source, target, overwrite: true);
                changes.Add(new(source, target));
            }
        }
    }

    // Extracted here into a folder holding something of the same name, all of the zip is
    // written to a hidden folder of its own there, then moved into place by the shell, which asks
    // what to do with each file there already: skipped, it is kept as it was, and said to have
    // been, and replaced, it is the zip's. What is new goes in beside it, a folder there merged
    // into, and nothing is left of the folder it was written to. Undo takes away what each move
    // brought, and only that. Where nothing is in the way, the shell isn't asked.
    [Test]
    public async Task ExtractHereMovesIntoPlaceThroughTheShellWhereSomethingIsThere()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("fresh.txt", "fresh"), ("notes.aleph", "from the zip"), ("Docs/more.txt", "more"));
        var clear = MakeZip(fixture, "Clear.zip", ("clear.txt", "clear"));
        var notes = await File.ReadAllTextAsync(fixture.At("notes.aleph"));
        var skip = new ShellMove(replace: false);
        var replace = new ShellMove(replace: true);
        var real = Zip.MoveIntoPlace;
        var recycling = new Recycling();
        var history = UndoHistory.Instance;
        UndoHistory.Instance = new(recycling);
        (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) skipped;
        (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) replaced;
        string notesSkipped;
        try
        {
            Zip.MoveIntoPlace = skip.Move;
            Zip.ExtractTo(clear, fixture.Root, true, null, Cancel.None);
            skipped = Zip.ExtractTo(zip, fixture.Root, true, null, Cancel.None);
            notesSkipped = await File.ReadAllTextAsync(fixture.At("notes.aleph"));
            Zip.MoveIntoPlace = replace.Move;
            replaced = Zip.ExtractTo(zip, fixture.Root, true, null, Cancel.None);

            // Tests elsewhere may record in it meanwhile, and are undone on paper too.
            while (UndoHistory.Instance.CanUndo)
            {
                await UndoHistory.Instance.Undo(IntPtr.Zero);
            }
        }
        finally
        {
            Zip.MoveIntoPlace = real;
            UndoHistory.Instance = history;
        }

        var asked = skip.Asked.Single();
        await Assert.That(asked.Sources.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["fresh.txt", "notes.aleph", "Docs"], CollectionOrdering.Matching);
        await Assert.That(asked.Destination).IsEqualTo(fixture.Root);
        await Assert.That(asked.FromHidden).IsTrue();
        await Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(asked.Sources[0]))).IsEqualTo(fixture.Root);
        await Assert.That(replace.Asked.Count).IsEqualTo(1);
        await Assert.That(notesSkipped).IsEqualTo(notes);
        await Assert.That(await File.ReadAllTextAsync(fixture.At("notes.aleph"))).IsEqualTo("from the zip");
        await Assert.That(Directory.GetDirectories(fixture.Root, ".Lode*")).IsEmpty();
        await Verify(
                new
                {
                    skipped = new
                    {
                        skipped.Outcome.Succeeded,
                        skipped.Outcome.Summary,
                        details = skipped.Outcome.Details.Split(Environment.NewLine).Skip(1),
                        topLevel = skipped.TopLevel.Select(fixture.Relative)
                    },
                    replaced = new
                    {
                        replaced.Outcome.Succeeded,
                        replaced.Outcome.Summary,
                        topLevel = replaced.TopLevel.Select(fixture.Relative)
                    },
                    undone = recycling.Recycled
                        .Where(_ => _.All(path => Locations.IsWithin(path, fixture.Root)))
                        .Select(_ => _.Select(fixture.Relative)),
                    tree = fixture.Tree()
                })
            .Snapshot(
                """
                {
                  skipped: {
                    Succeeded: true,
                    Summary: Extracted Bundle.zip, skipping 1 file,
                    details: [
                      ,
                      Skipped, something of its name being there already, or the move having been cancelled:,
                      notes.aleph
                    ],
                    topLevel: [
                      Projects/fresh.txt,
                      Projects/Docs
                    ]
                  },
                  replaced: {
                    Succeeded: true,
                    Summary: Extracted Bundle.zip,
                    topLevel: [
                      Projects/fresh.txt,
                      Projects/notes.aleph,
                      Projects/Docs
                    ]
                  },
                  undone: [
                    [
                      Projects/fresh.txt,
                      Projects/notes.aleph,
                      Projects/Docs/more.txt
                    ],
                    [
                      Projects/fresh.txt,
                      Projects/Docs/more.txt
                    ],
                    [
                      Projects/clear.txt
                    ]
                  ],
                  tree: [
                    Projects/Archive/,
                    Projects/Bundle.zip (361 bytes),
                    Projects/Cache/,
                    Projects/Clear.zip (129 bytes),
                    Projects/Docs/,
                    Projects/Docs/guide.aleph (900 bytes),
                    Projects/Docs/more.txt (4 bytes),
                    Projects/Report1.beta (1,500,000 bytes),
                    Projects/Source/,
                    Projects/Source/Deep/,
                    Projects/Source/Deep/leaf.aleph (40 bytes),
                    Projects/Source/main.aleph (5,000 bytes),
                    Projects/clear.txt (5 bytes),
                    Projects/fresh.txt (5 bytes),
                    Projects/hidden.aleph (64 bytes),
                    Projects/notes.aleph (12 bytes),
                    Projects/report10.beta (300,000 bytes),
                    Projects/report2.beta (2,048 bytes),
                    Projects/summary.gamma (0 bytes)
                  ]
                }
                """);
    }

    // Extracted here, a folder that is there already is merged into, and undo takes away only
    // what the extract made.
    [Test]
    public async Task ExtractHereMergesIntoAFolder()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("Docs/more.txt", "more"), ("New/one.txt", "one"), ("New/two.txt", "two"));

        var extraction = Zip.Extract(zip, fixture.Root, null, Cancel.None);

        await Assert.That(extraction.TopLevel.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Docs", "New"], CollectionOrdering.Matching);
        await Assert.That(extraction.Created.Select(fixture.Relative)).IsEquivalentTo(["Projects/Docs/more.txt", "Projects/New"]);
        await Assert.That(File.Exists(fixture.At("Docs", "guide.aleph"))).IsTrue();
    }

    // Extracted here, what it put at the top is each item there once, as the zip first names it,
    // in the zip's order, a folder there already among them, and a folder named twice but for
    // case once; what it made, for undo, the highest of each, a folder it made with all in it,
    // whatever case what is in it names it in, and what it put in a folder that was there.
    // Extracted all, the new folder is both.
    [Test]
    public async Task ExtractSaysWhatItPutAtTheTopAndWhatItMade()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(
            fixture,
            "Bundle.zip",
            ("one.txt", "one"),
            ("Docs/more.txt", "more"),
            ("New/Inner/deep.txt", "deep"),
            ("new/two.txt", "two"),
            ("DOCS/again.txt", "again"),
            ("Empty/", ""),
            ("last.txt", "last"));

        var extraction = Zip.Extract(zip, fixture.Root, null, Cancel.None);
        var (_, topLevel) = Zip.ExtractTo(zip, fixture.At("Docs"), false, null, Cancel.None);

        await Assert.That(extraction.TopLevel.Select(fixture.Relative))
            .IsEquivalentTo(["Projects/one.txt", "Projects/Docs", "Projects/New", "Projects/Empty", "Projects/last.txt"], CollectionOrdering.Matching);
        await Assert.That(extraction.Created.Select(fixture.Relative))
            .IsEquivalentTo(["Projects/one.txt", "Projects/Docs/more.txt", "Projects/New", "Projects/DOCS/again.txt", "Projects/Empty", "Projects/last.txt"], CollectionOrdering.Matching);
        await Assert.That(topLevel.Select(fixture.Relative)).IsEquivalentTo(["Projects/Docs/Bundle"]);
    }

    // An entry named to go outside the folder, or with a name Windows can't have, is left out
    // (Zip Slip). The rest is extracted.
    [Test]
    public async Task ExtractLeavesOutUnsafeNames()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Target"));
        var zip = MakeZip(
            fixture,
            "Bad.zip",
            ("../escaped.txt", "x"),
            ("Inner/../../escaped.txt", "x"),
            ("/rooted.txt", "x"),
            ("C:/drive.txt", "x"),
            ("trailing. ", "x"),
            ("safe.txt", "safe"));

        var extraction = Zip.Extract(zip, fixture.At("Target"), null, Cancel.None);

        await Assert.That(extraction.LeftOut.Count).IsEqualTo(5);
        await Assert.That(File.Exists(fixture.At("escaped.txt"))).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(fixture.At("Target")).Select(_ => Path.GetFileName(_))).IsEquivalentTo(["safe.txt"]);
    }

    // Windows takes a name it keeps for a device, CON, AUX or NUL say, for the device, whatever
    // follows the first dot, so aux.c and con.h, as C sources made on Linux have, would open the
    // device rather than a file, and fail the extract, or lose what was written to them. Each is
    // left out, saying so, and the rest extracted, console.h among them.
    [Test]
    public async Task ExtractLeavesOutDeviceNames()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Devices.zip", ("src/aux.c", "aux"), ("src/main.c", "main"), ("con.h", "con"), ("nul", "nul"), ("console.h", "console"));

        var (outcome, _) = Zip.ExtractTo(zip, fixture.Root, false, null, Cancel.None);

        await Verify(
            new
            {
                outcome.Succeeded,
                outcome.Summary,
                leftOut = outcome.Details.Split(Environment.NewLine).Skip(1),
                tree = fixture.Tree().Where(_ => _.StartsWith("Projects/Devices/", StringComparison.Ordinal))
            })
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: Extracted Devices.zip, leaving out 3 items,
                  leftOut: [
                    ,
                    Left out, with names Windows keeps for devices, as CON, AUX and NUL:,
                    src/aux.c,
                    con.h,
                    nul
                  ],
                  tree: [
                    Projects/Devices/,
                    Projects/Devices/console.h (7 bytes),
                    Projects/Devices/src/,
                    Projects/Devices/src/main.c (4 bytes)
                  ]
                }
                """);
    }

    [Test]
    public async Task Segments()
    {
        await Assert.That(Zip.Segments("a/b\\c.txt")).IsEquivalentTo(["a", "b", "c.txt"], CollectionOrdering.Matching);
        await Assert.That(Zip.Segments("folder/")).IsEquivalentTo(["folder"]);
        await Assert.That(Zip.Segments("a/../b")).IsNull();
        await Assert.That(Zip.Segments("a/./b")).IsNull();
        await Assert.That(Zip.Segments("\\a")).IsNull();
        await Assert.That(Zip.Segments("a/b?")).IsNull();
        await Assert.That(Zip.Segments("a /b")).IsNull();
        await Assert.That(Zip.Segments("src/aux.c")).IsNull();
        await Assert.That(Zip.Segments("Con/inner.txt")).IsNull();
        await Assert.That(Zip.Segments("nul.tar.gz")).IsNull();
        await Assert.That(Zip.Segments("prn .txt")).IsNull();
        await Assert.That(Zip.Segments("COM0")).IsNull();
        await Assert.That(Zip.Segments("lpt9.log")).IsNull();
        await Assert.That(Zip.Segments("COM¹")).IsNull();
        await Assert.That(Zip.Segments("LPT³.txt")).IsNull();
        await Assert.That(Zip.Segments("console.h")).IsEquivalentTo(["console.h"]);
        await Assert.That(Zip.Segments("auxiliary/com10.c")).IsEquivalentTo(["auxiliary", "com10.c"], CollectionOrdering.Matching);
        await Assert.That(Zip.Segments("COM⁴")).IsEquivalentTo(["COM⁴"]);
    }

    // Past the old 260 character limit, both ways.
    [Test]
    public async Task LongPaths()
    {
        using var fixture = Fixture.Create();
        var deep = fixture.At("Deep");
        for (var level = 0; deep.Length < 300; level++)
        {
            deep = Path.Combine(deep, $"a folder with a long name to get past the limit {level}");
        }

        Directory.CreateDirectory(deep);
        await File.WriteAllTextAsync(Path.Combine(deep, "file.txt"), "content");
        var zip = fixture.At("Deep.zip");
        Zip.Compress(zip, [fixture.At("Deep")], null, Cancel.None);
        Directory.Delete(fixture.At("Deep"), true);

        Zip.ExtractTo(zip, fixture.Root, true, null, Cancel.None);

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(deep, "file.txt"))).IsEqualTo("content");
    }

    // The first part of a path named *.zip that is a file is the zip; what follows is where in
    // it. A folder named so is a folder, and a zip in a zip is only somewhere in the first.
    [Test]
    public async Task Split()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("Inner/deep.txt", "deep"));
        Directory.CreateDirectory(fixture.At("Folder.zip"));

        await Assert.That(Zip.Split(zip)).IsEqualTo(new Zip.Location(zip, ""));
        await Assert.That(Zip.Split(Path.Combine(zip, "Inner", "deep.txt"))).IsEqualTo(new Zip.Location(zip, @"Inner\deep.txt"));
        await Assert.That(Zip.Split(Path.Combine(zip, "Nested.zip", "a"))).IsEqualTo(new Zip.Location(zip, @"Nested.zip\a"));
        await Assert.That(Zip.Split(fixture.At("Folder.zip"))).IsNull();
        await Assert.That(Zip.Split(fixture.At("Bundle.zipper"))).IsNull();
        await Assert.That(Zip.Split(fixture.Root)).IsNull();
    }

    // What each folder holds: those with no entries of their own made from the names of what is
    // in them, names differing only in case told apart as Windows tells them apart, a folder
    // winning over a file of the same name, and names that are not safe left out.
    [Test]
    public async Task Read()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(
            fixture,
            "Bundle.zip",
            ("top.txt", "top"),
            ("Inner/deep.txt", "deep"),
            ("INNER/Deeper/leaf.txt", "leaf"),
            ("inner/DEEP.TXT", "clash"),
            ("Empty/", ""),
            ("Both", "file"),
            ("Both/in.txt", "in"),
            ("../escaped.txt", "x"));

        var folders = Zip.Read(zip);

        await Verify(
            folders
                .OrderBy(_ => _.Key, StringComparer.Ordinal)
                .ToDictionary(
                    _ => _.Key,
                    _ => _.Value.Values
                        .Select(item => $"{item.Name}{(item.IsFolder ? "/" : $" ({item.Size} bytes)")}")
                        .Order(StringComparer.Ordinal)
                        .ToList()))
                        .Snapshot(
                            """
                            {
                              : [
                                Both/,
                                Empty/,
                                Inner/,
                                top.txt (3 bytes)
                              ],
                              Both: [
                                in.txt (2 bytes)
                              ],
                              Inner: [
                                Deeper/,
                                deep.txt (4 bytes)
                              ],
                              Inner\Deeper: [
                                leaf.txt (4 bytes)
                              ]
                            }
                            """);
    }

    // Opened, copied or dragged, an item in a zip is extracted to the temp folder, a folder with
    // all it holds. Again, it goes to the same place.
    [Test]
    public async Task ExtractToTemp()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("top.txt", "top"), ("Inner/deep.txt", "deep"), ("Inner/Empty/", ""));

        var extracted = Zip.ExtractToTemp([Path.Combine(zip, "top.txt"), Path.Combine(zip, "Inner")]);
        var again = Zip.ExtractToTemp([Path.Combine(zip, "top.txt")]);

        try
        {
            await Assert.That(extracted.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["top.txt", "Inner"], CollectionOrdering.Matching);
            await Assert.That(extracted.All(_ => _.StartsWith(Zip.TempRoot, StringComparison.OrdinalIgnoreCase))).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(extracted[0])).IsEqualTo("top");
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(extracted[1], "deep.txt"))).IsEqualTo("deep");
            await Assert.That(Directory.Exists(Path.Combine(extracted[1], "Empty"))).IsTrue();
            await Assert.That(again[0]).IsEqualTo(extracted[0]);
        }
        finally
        {
            foreach (var path in extracted)
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }

    // Stopped part way, an extract to the temp folder leaves nothing of itself there: none of
    // an item not extracted before, and of one that was, what was there before, untouched,
    // rather than taken away to be written again. It said how far it had got as it went.
    [Test]
    public async Task ExtractToTempStoppedLeavesNothingOfItself()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] {"Before/big.bin", "Fresh/big.bin"})
            {
                await using var stream = archive.CreateEntry(name).Open();
                stream.Write(new byte[2_000_000]);
            }
        }

        var before = Zip.ExtractToTemp([Path.Combine(zip, "Before")])[0];
        var fresh = Zip.TempTarget(new(zip, "Fresh"));
        var reported = new List<int>();
        IReadOnlyList<string> StoppedPartWay(string item)
        {
            using var cancel = new CancelSource();
            var progress = new Reporting<int>(
                _ =>
                {
                    reported.Add(_);
                    if (_ is > 0 and < 100)
                    {
                        cancel.Cancel();
                    }
                });
            return Zip.ExtractToTemp([Path.Combine(zip, item)], progress, cancel.Token);
        }

        try
        {
            await Assert.That(() => StoppedPartWay("Before")).Throws<OperationCanceledException>();
            await Assert.That(() => StoppedPartWay("Fresh")).Throws<OperationCanceledException>();

            await Assert.That(new FileInfo(Path.Combine(before, "big.bin")).Length).IsEqualTo(2_000_000);
            await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(before)!)).IsEquivalentTo([before]);
            await Assert.That(Directory.Exists(Path.GetDirectoryName(fresh))).IsFalse();
            await Assert.That(reported.Count).IsEqualTo(2);
            await Assert.That(reported.All(_ => _ is > 0 and < 100)).IsTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(before)!, true);
        }
    }

    // Told at once, on the thread reporting, as Progress, which posts, would not be.
    sealed class Reporting<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) =>
            report(value);
    }

    // What a big entry is made of: enough pieces of what is copied at a time to stop part way.
    const int bigLength = 4_000_000;

    // Reports what it is told, and cancels on the second: once the first piece of a big file is
    // written.
    static (Reporting<string> Progress, List<string> Reports) CancelOnTheSecondReport(CancelSource cancel)
    {
        var reports = new List<string>();
        var progress = new Reporting<string>(
            _ =>
            {
                reports.Add(_);
                if (reports.Count == 2)
                {
                    cancel.Cancel();
                }
            });
        return (progress, reports);
    }

    // A big entry, stopped part way, stops within the piece it is writing, rather than once all
    // of it is written, and takes away what it wrote, the folder made for it too. It says how
    // far it has got in bytes as it goes, where Extracting 1 of 1 was all it said till the entry
    // was done, the panel saying Cancelling meanwhile.
    [Test]
    public async Task CancelledExtractStopsPartWayThroughABigEntry()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var stream = archive.CreateEntry("big.bin", CompressionLevel.NoCompression).Open();
            stream.Write(new byte[bigLength]);
        }

        using var cancel = new CancelSource();
        var (progress, reports) = CancelOnTheSecondReport(cancel);

        await Assert.That(() => Zip.ExtractTo(zip, fixture.Root, false, progress, cancel.Token)).Throws<OperationCanceledException>();

        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[0]).IsEqualTo("Extracting 1 of 1, 0 bytes of 3.81 MB");
        await Assert.That(reports[1]).StartsWith("Extracting 1 of 1, ").And.EndsWith(" KB of 3.81 MB");
        await Assert.That(Path.Exists(fixture.At("Big"))).IsFalse();
    }

    // A zip can hold far more than it takes, a few megabytes that fill the disk. Where the drive
    // it goes to hasn't the room for all it holds, nothing is extracted, here or to a folder of
    // its own, which goes too, saying how much it needs and how much there is, rather than
    // writing till the disk is full. The drive here stands in for one short of room, the real
    // one having some.
    [Test]
    public async Task ExtractRefusesWhereThereIsNotTheRoom()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("one.txt", new('a', 600)), ("Inner/two.txt", new('b', 600)), ("Empty/", ""));
        var before = fixture.Tree();
        var real = Zip.FreeSpace;
        await Assert.That(real(fixture.Root) ?? 0).IsGreaterThan(0);
        var askedOf = new List<string>();
        Zip.FreeSpace = _ =>
        {
            askedOf.Add(fixture.Relative(_));
            return 1_000;
        };
        (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) here;
        (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) all;
        try
        {
            here = Zip.ExtractTo(zip, fixture.Root, true, null, Cancel.None);
            all = Zip.ExtractTo(zip, fixture.Root, false, null, Cancel.None);
        }
        finally
        {
            Zip.FreeSpace = real;
        }

        await Assert.That(fixture.Tree()).IsEquivalentTo(before, CollectionOrdering.Matching);
        await Assert.That(here.TopLevel.Concat(all.TopLevel)).IsEmpty();
        await Verify(
            new
            {
                here = here.Outcome,
                all = all.Outcome,
                askedOf
            })
            .Snapshot(
                """
                {
                  here: {
                    Succeeded: false,
                    Summary: Not enough room: needs 1.17 KB, 1,000 bytes free,
                    Details: Nothing was extracted from Bundle.zip: it needs 1.17 KB, and the drive it goes to has 1,000 bytes free.
                  },
                  all: {
                    Succeeded: false,
                    Summary: Not enough room: needs 1.17 KB, 1,000 bytes free,
                    Details: Nothing was extracted from Bundle.zip: it needs 1.17 KB, and the drive it goes to has 1,000 bytes free.
                  },
                  askedOf: [
                    Projects,
                    Projects/Bundle
                  ]
                }
                """);
    }

    // As a big entry extracted is, a big file compressed, stopped part way, stops within the
    // piece it is writing, leaving no zip, and says how far it has got in bytes as it goes.
    [Test]
    public async Task CancelledCompressStopsPartWayThroughABigFile()
    {
        using var fixture = Fixture.Create();
        var big = fixture.At("big.bin");
        await File.WriteAllBytesAsync(big, new byte[bigLength]);
        var zip = fixture.At("Big.zip");
        using var cancel = new CancelSource();
        var (progress, reports) = CancelOnTheSecondReport(cancel);

        await Assert.That(() => Zip.Compress(zip, [big], progress, cancel.Token)).Throws<OperationCanceledException>();

        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[0]).IsEqualTo("Compressing 1 of 1, 0 bytes of 3.81 MB");
        await Assert.That(reports[1]).StartsWith("Compressing 1 of 1, ").And.EndsWith(" KB of 3.81 MB");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    // Zips extracted here together can share a folder, as the parts of a Google Takeout all hold
    // Takeout. Each is extracted in its turn, so the second finds what the first wrote in its
    // way, and the shell asks what to do with it, here skipping it, and the first keeps all it
    // wrote: the two at once, both found nothing in the way, and the one that then failed on
    // what the other wrote took the other's files away with its own.
    [Test]
    public async Task ZipsExtractedTogetherTakeTurns()
    {
        using var fixture = Fixture.Create();
        var first = MakeZip(fixture, "takeout-001.zip", [.. Enumerable.Range(0, 200).Select(_ => ($"Takeout/one-{_}.txt", "one")), ("Takeout/same.txt", "first")]);
        var second = MakeZip(fixture, "takeout-002.zip", [.. Enumerable.Range(0, 200).Select(_ => ($"Takeout/two-{_}.txt", "two")), ("Takeout/same.txt", "second")]);
        var skip = new ShellMove(replace: false);
        var real = Zip.MoveIntoPlace;
        Zip.MoveIntoPlace = skip.Move;
        IReadOnlyList<string> placed;
        try
        {
            placed = await Zip.ExtractInBackground([first, second], true);
        }
        finally
        {
            Zip.MoveIntoPlace = real;
        }

        await Assert.That(Directory.GetFiles(fixture.At("Takeout"), "one-*.txt").Length).IsEqualTo(200);
        await Assert.That(Directory.GetFiles(fixture.At("Takeout"), "two-*.txt").Length).IsEqualTo(200);
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Takeout", "same.txt"))).IsEqualTo("first");
        await Assert.That(Directory.GetFiles(fixture.At("Takeout")).Length).IsEqualTo(401);
        await Assert.That(skip.Asked.Count).IsEqualTo(1);
        await Assert.That(ActionLog.Entries.Where(_ => _.Title == "Extract").Select(_ => $"{_.FolderName}: {_.Summary}"))
            .IsEquivalentTo(["takeout-001.zip: Extracted takeout-001.zip", "takeout-002.zip: Extracted takeout-002.zip, skipping 1 file"], CollectionOrdering.Matching);
        await Assert.That(placed.Select(fixture.Relative)).IsEquivalentTo(["Projects/Takeout"]);
    }

    // Undo's work done on paper: what it sends to the Recycle Bin.
    sealed class Recycling : UndoHistory.IFileActions
    {
        public List<IReadOnlyList<string>> Recycled { get; } = [];

        public string? Rename(IntPtr owner, string path, string newName) =>
            null;

        public IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items) =>
            [];

        public IReadOnlyList<(string Original, string? InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths)
        {
            Recycled.Add(paths);
            return paths
                .Select(_ => (_, (string?) null))
                .ToList();
        }

        public IReadOnlyCollection<string> Restore(IReadOnlyList<string> inBin) =>
            inBin;

        public bool Exists(string path) =>
            Path.Exists(path);

        public void CreateFolder(string path)
        {
        }

        public void DeleteIfEmpty(string path)
        {
        }
    }

    // Each zip's undo holds what it made, and only that: undoing the second of zips extracted
    // here together sends its own files to the Recycle Bin, not the folder they share with the
    // first's files in it, which undoing the first then does. Each has files of its own at its
    // top before those in the folder, so neither has made the folder when the other starts.
    [Test]
    public async Task UndoOfOneOfZipsExtractedTogetherLeavesTheOthers()
    {
        using var fixture = Fixture.Create();
        var first = MakeZip(fixture, "takeout-001.zip", [.. Enumerable.Range(0, 100).Select(_ => ($"one-{_}.txt", "one")), ("Takeout/one.txt", "one")]);
        var second = MakeZip(fixture, "takeout-002.zip", [.. Enumerable.Range(0, 100).Select(_ => ($"two-{_}.txt", "two")), ("Takeout/two.txt", "two")]);
        var recycling = new Recycling();
        var history = UndoHistory.Instance;
        UndoHistory.Instance = new(recycling);
        try
        {
            await Zip.ExtractInBackground([first, second], true);

            // Tests elsewhere may record in it meanwhile, and are undone on paper too.
            while (UndoHistory.Instance.CanUndo)
            {
                await UndoHistory.Instance.Undo(IntPtr.Zero);
            }
        }
        finally
        {
            UndoHistory.Instance = history;
        }

        var undone = recycling.Recycled
            .Where(_ => _.All(path => Locations.IsWithin(path, fixture.Root)))
            .ToList();
        await Assert.That(undone.Count).IsEqualTo(2);
        await Assert.That(undone[0].Count).IsEqualTo(101);
        await Assert.That(undone[0]).Contains(fixture.At("Takeout", "two.txt"));
        await Assert.That(undone[0]).DoesNotContain(fixture.At("Takeout"));
        await Assert.That(undone[1].Count).IsEqualTo(101);
        await Assert.That(undone[1]).Contains(fixture.At("Takeout"));
    }

    // Cancels on the report given, of how many files it has come to, whatever it says of the
    // bytes after, on the thread reporting it.
    sealed class CancelOnReport(CancelSource cancel, string report) : IProgress<string>
    {
        public void Report(string value)
        {
            if (value.StartsWith($"{report},", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        }
    }

    // Stopped part way, an extract takes away what it made, and only that: a folder that was
    // there stays, with what it held, though the extract had put a file in it. Extracted all,
    // the folder made for it goes too.
    [Test]
    public async Task CancelledExtractTakesAwayWhatItMade()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("Docs/more.txt", "more"), ("New/Inner/one.txt", "one"), ("New/two.txt", "two"), ("last.txt", "last"));
        using var here = new CancelSource();
        using var all = new CancelSource();

        await Assert.That(() => Zip.Extract(zip, fixture.Root, new CancelOnReport(here, "Extracting 3 of 4"), here.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => Zip.ExtractTo(zip, fixture.Root, false, new CancelOnReport(all, "Extracting 3 of 4"), all.Token)).Throws<OperationCanceledException>();

        await Assert.That(Directory.GetFileSystemEntries(fixture.At("Docs")).Select(_ => Path.GetFileName(_))).IsEquivalentTo(["guide.aleph"]);
        await Assert.That(Path.Exists(fixture.At("New"))).IsFalse();
        await Assert.That(Path.Exists(fixture.At("last.txt"))).IsFalse();
        await Assert.That(Path.Exists(fixture.At("Bundle"))).IsFalse();
    }

    // Extracted all together, each zip has a folder of its own, though the name one is given is
    // the name another would have: Bundle.zip goes to Bundle (2), Bundle being there, and
    // Bundle (2).zip then to Bundle (2) (2).
    [Test]
    public async Task ZipsExtractedAllTogetherHaveAFolderEach()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Bundle"));
        var first = MakeZip(fixture, "Bundle.zip", ("one.txt", "one"));
        var second = MakeZip(fixture, "Bundle (2).zip", ("two.txt", "two"));

        var placed = await Zip.ExtractInBackground([first, second], false);

        await Assert.That(placed.Select(fixture.Relative)).IsEquivalentTo(["Projects/Bundle (2)", "Projects/Bundle (2) (2)"], CollectionOrdering.Matching);
        await Assert.That(Directory.GetFileSystemEntries(fixture.At("Bundle (2)")).Select(_ => Path.GetFileName(_))).IsEquivalentTo(["one.txt"]);
        await Assert.That(Directory.GetFileSystemEntries(fixture.At("Bundle (2) (2)")).Select(_ => Path.GetFileName(_))).IsEquivalentTo(["two.txt"]);
    }

    // What Windows writes beside a file a browser downloads, saying where it came from.
    static string? MarkOf(string path)
    {
        try
        {
            return File.ReadAllText(path + ":Zone.Identifier");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    // A zip downloaded from the internet is marked so, and Explorer marks what it extracts from
    // one the same, which has Office open a document in Protected View and SmartScreen check a
    // program: extracted all at once, or an item opened, copied or dragged out of it, each file
    // has the zip's mark, and keeps the time the zip gives it. A zip with no mark, or one from a
    // trusted site, gives none.
    [Test]
    public async Task ExtractedFromADownloadedZipIsMarkedAsTheZipIs()
    {
        using var fixture = Fixture.Create();
        const string mark = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/Downloaded.zip\r\n";
        var downloaded = MakeZip(fixture, "Downloaded.zip", ("invoice.docm", "macro"), ("Inner/setup.exe", "program"), ("Inner/Empty/", ""));
        await File.WriteAllTextAsync(downloaded + ":Zone.Identifier", mark);
        var trusted = MakeZip(fixture, "Trusted.zip", ("invoice.docm", "macro"));
        await File.WriteAllTextAsync(trusted + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
        var local = MakeZip(fixture, "Local.zip", ("invoice.docm", "macro"));
        DateTime written;
        using (var archive = ZipFile.OpenRead(downloaded))
        {
            written = archive.GetEntry("invoice.docm")!.LastWriteTime.DateTime;
        }

        Zip.ExtractTo(downloaded, fixture.Root, false, null, Cancel.None);
        Zip.ExtractTo(trusted, fixture.Root, false, null, Cancel.None);
        Zip.ExtractTo(local, fixture.Root, false, null, Cancel.None);
        var extracted = Zip.ExtractToTemp([Path.Combine(downloaded, "invoice.docm"), Path.Combine(downloaded, "Inner"), Path.Combine(local, "invoice.docm")]);

        try
        {
            await Assert.That(MarkOf(fixture.At("Downloaded", "invoice.docm"))).IsEqualTo(mark);
            await Assert.That(MarkOf(fixture.At("Downloaded", "Inner", "setup.exe"))).IsEqualTo(mark);
            await Assert.That(File.GetLastWriteTime(fixture.At("Downloaded", "invoice.docm"))).IsEqualTo(written);
            await Assert.That(MarkOf(extracted[0])).IsEqualTo(mark);
            await Assert.That(MarkOf(Path.Combine(extracted[1], "setup.exe"))).IsEqualTo(mark);
            await Assert.That(File.GetLastWriteTime(extracted[0])).IsEqualTo(written);
            await Assert.That(MarkOf(fixture.At("Trusted", "invoice.docm"))).IsNull();
            await Assert.That(MarkOf(fixture.At("Local", "invoice.docm"))).IsNull();
            await Assert.That(MarkOf(extracted[2])).IsNull();
        }
        finally
        {
            foreach (var path in extracted)
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }

    // When each entry of a tar made here was last changed.
    static readonly DateTimeOffset tarWritten = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    // A tar of the entries, gzipped unless it is named .tar: each a file holding what is given,
    // a folder, or a link, symbolic or hard, to what is given, or a pipe.
    static string MakeTar(Fixture fixture, string name, params (TarEntryType Type, string Name, string Content)[] entries)
    {
        var tar = fixture.At(name);
        Stream stream = File.Create(tar);
        if (!name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            stream = new GZipStream(stream, CompressionLevel.Optimal);
        }

        using var writer = new TarWriter(stream, TarEntryFormat.Pax);
        foreach (var (type, entryName, content) in entries)
        {
            var entry = new PaxTarEntry(type, entryName)
            {
                ModificationTime = tarWritten
            };
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                entry.LinkName = content;
            }
            else if (type == TarEntryType.RegularFile)
            {
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            }

            writer.WriteEntry(entry);
        }

        return tar;
    }

    // What a tar made by tar -czf of a folder as itself holds: every name after ./, and a folder
    // with no slash at its end, nested folders, one only there in the names of what is in it,
    // and an empty one, a name going outside, one Windows keeps for a device, links, symbolic
    // and hard, and a pipe.
    static readonly (TarEntryType Type, string Name, string Content)[] source =
    [
        (TarEntryType.Directory, "./", ""),
        (TarEntryType.Directory, "./src", ""),
        (TarEntryType.RegularFile, "./src/main.c", "main"),
        (TarEntryType.RegularFile, "./src/deep/leaf.txt", "leaf"),
        (TarEntryType.RegularFile, "../escaped.txt", "escaped"),
        (TarEntryType.RegularFile, "src/aux.c", "aux"),
        (TarEntryType.SymbolicLink, "src/passwd", "../../../etc/passwd"),
        (TarEntryType.HardLink, "src/again.c", "src/main.c"),
        (TarEntryType.Fifo, "pipe", ""),
        (TarEntryType.Directory, "empty/", ""),
        (TarEntryType.RegularFile, "readme.md", "readme")
    ];

    // A tar, gzipped or not, is extracted as a zip is: to a folder named for it, without its
    // .tar, .tar.gz or .tgz, each file with the time the tar gives it. What a zip would have left
    // out is, and so are links, which, followed or made, could lead outside the folder, and
    // what is neither file nor folder. Nothing goes outside the folder.
    [Test]
    public async Task ExtractsATar()
    {
        using var fixture = Fixture.Create();
        var tars = new[] {"Plain.tar", "Gzipped.tar.gz", "Short.tgz"}
            .Select(_ => MakeTar(fixture, _, source))
            .ToList();

        var extracted = new List<string>();
        var written = new List<DateTime>();
        foreach (var tar in tars)
        {
            var (outcome, topLevel) = Zip.ExtractTo(tar, fixture.Root, false, null, Cancel.None);
            var folder = $"{fixture.Relative(topLevel.Single())}/";
            extracted.Add(
                string.Join(
                    Environment.NewLine,
                    [
                        outcome.Succeeded.ToString(),
                        outcome.Summary.Replace(Path.GetFileName(tar), "the tar"),
                        .. outcome.Details.Split(Environment.NewLine).Skip(1),
                        "",
                        .. fixture.Tree()
                            .Where(_ => _.StartsWith(folder, StringComparison.Ordinal) &&
                                        _.Length > folder.Length)
                            .Select(_ => _[folder.Length..])
                    ]));
            written.Add(File.GetLastWriteTimeUtc(Path.Combine(topLevel[0], "src", "main.c")));
        }

        await Assert.That(fixture.Tree().Any(_ => _.Contains("escaped", StringComparison.Ordinal))).IsFalse();
        await Assert.That(written.All(_ => _ == tarWritten.UtcDateTime)).IsTrue();
        await Assert.That(extracted[1]).IsEqualTo(extracted[0]);
        await Assert.That(extracted[2]).IsEqualTo(extracted[0]);
        await Verify(extracted[0])
            .Snapshot(
                """
                True
                Extracted the tar, leaving out 5 items

                Left out, being named to go outside the folder or with names Windows can't have:
                ../escaped.txt

                Left out, with names Windows keeps for devices, as CON, AUX and NUL:
                src/aux.c

                Left out, being links, which are neither followed nor made, as one could lead outside the folder:
                src/passwd
                src/again.c

                Left out, being neither files nor folders, as a device or a pipe is:
                pipe

                empty/
                readme.md (6 bytes)
                src/
                src/deep/
                src/deep/leaf.txt (4 bytes)
                src/main.c (4 bytes)
                """);
    }

    // As a zip's, a big entry of a gzipped tar, stopped part way, stops within the piece it is
    // writing, saying how far it had got, and takes away what it wrote, here or in a folder of
    // its own, which goes too.
    [Test]
    public async Task CancelledTarExtractStopsPartWayAndTakesAwayWhatItMade()
    {
        using var fixture = Fixture.Create();
        var tar = MakeTar(fixture, "Big.tgz", (TarEntryType.Directory, "Inner/", ""), (TarEntryType.RegularFile, "Inner/big.bin", new('a', bigLength)));
        var before = fixture.Tree();
        using var all = new CancelSource();
        using var here = new CancelSource();
        var (progress, reports) = CancelOnTheSecondReport(all);

        await Assert.That(() => Zip.ExtractTo(tar, fixture.Root, false, progress, all.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => Zip.ExtractTo(tar, fixture.Root, true, CancelOnTheSecondReport(here).Progress, here.Token)).Throws<OperationCanceledException>();

        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[0]).IsEqualTo("Extracting 1 of 1, 0 bytes of 3.81 MB");
        await Assert.That(reports[1]).StartsWith("Extracting 1 of 1, ").And.EndsWith(" KB of 3.81 MB");
        await Assert.That(fixture.Tree()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    // What is named as a tar but isn't one, a gzipped text, gzipped or not, short or long, or
    // what isn't gzipped at all, is refused, saying so, and nothing is extracted, here or to a
    // folder of its own, which goes.
    [Test]
    public async Task WhatIsNotATarIsRefused()
    {
        using var fixture = Fixture.Create();
        void Gzipped(string name, string text)
        {
            using var stream = new GZipStream(File.Create(fixture.At(name)), CompressionLevel.Optimal);
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        Gzipped("Notes.tar.gz", new('x', 2_000));
        Gzipped("Short.tgz", "hello");
        await File.WriteAllTextAsync(fixture.At("Plain.tgz"), "no gzip at all");
        await File.WriteAllTextAsync(fixture.At("Text.tar"), new string('x', 2_000));
        var before = fixture.Tree();

        var outcomes = new[] {"Notes.tar.gz", "Short.tgz", "Plain.tgz", "Text.tar"}
            .SelectMany(_ => new[]
            {
                Zip.ExtractTo(fixture.At(_), fixture.Root, false, null, Cancel.None),
                Zip.ExtractTo(fixture.At(_), fixture.Root, true, null, Cancel.None)
            })
            .ToList();

        await Assert.That(fixture.Tree()).IsEquivalentTo(before, CollectionOrdering.Matching);
        await Assert.That(outcomes.All(_ => _.TopLevel.Count == 0)).IsTrue();
        await Verify(outcomes.Select(_ => $"{_.Outcome.Succeeded}, {_.Outcome.Summary}: {_.Outcome.Details}"))
            .Snapshot(
                """
                [
                  False, Can't be read: Nothing was extracted: Notes.tar.gz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Notes.tar.gz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Short.tgz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Short.tgz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Plain.tgz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Plain.tgz is not a tar archive compressed with gzip.,
                  False, Can't be read: Nothing was extracted: Text.tar is not a tar archive.,
                  False, Can't be read: Nothing was extracted: Text.tar is not a tar archive.
                ]
                """);
    }

    // A tar is looked inside as a zip is: what each folder holds, without the ./ before its
    // names, and without its links or what is neither file nor folder; somewhere in it is found
    // by its path; and what is opened, copied or dragged out of it is extracted to the temp
    // folder, a folder with all it holds, but for its links.
    [Test]
    public async Task ATarIsLookedInsideAsAZipIs()
    {
        using var fixture = Fixture.Create();
        var tar = MakeTar(fixture, "Source.tar.gz", source);

        var folders = Zip.Read(tar);
        var extracted = Zip.ExtractToTemp([Path.Combine(tar, "readme.md"), Path.Combine(tar, "src")]);

        try
        {
            await Assert.That(Zip.Split(Path.Combine(tar, "src", "main.c"))).IsEqualTo(new Zip.Location(tar, @"src\main.c"));
            await Assert.That(Zip.Split(fixture.At("Source.tar"))).IsNull();
            await Assert.That(Zip.Find(new(tar, @"src\deep"))).IsEqualTo(Zip.Kind.Folder);
            await Assert.That(Zip.Find(new(tar, "empty"))).IsEqualTo(Zip.Kind.Folder);
            await Assert.That(Zip.Find(new(tar, @"src\passwd"))).IsEqualTo(Zip.Kind.Missing);
            await Assert.That(await File.ReadAllTextAsync(extracted[0])).IsEqualTo("readme");
            await Assert.That(Directory.GetFileSystemEntries(extracted[1], "*", SearchOption.AllDirectories).Select(_ => Path.GetRelativePath(extracted[1], _)))
                .IsEquivalentTo(["deep", "main.c", @"deep\leaf.txt"]);
            await Verify(
                    folders
                        .OrderBy(_ => _.Key, StringComparer.Ordinal)
                        .ToDictionary(
                            _ => _.Key,
                            _ => _.Value.Values
                                .Select(item => $"{item.Name}{(item.IsFolder ? "/" : $" ({item.Size} bytes)")}")
                                .Order(StringComparer.Ordinal)
                                .ToList()))
                .Snapshot(
                    """
                    {
                      : [
                        empty/,
                        readme.md (6 bytes),
                        src/
                      ],
                      src: [
                        deep/,
                        main.c (4 bytes)
                      ],
                      src\deep: [
                        leaf.txt (4 bytes)
                      ]
                    }
                    """);
        }
        finally
        {
            foreach (var path in extracted)
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }
}
