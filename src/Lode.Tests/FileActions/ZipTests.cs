

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

    // Extracted here, into a folder already holding something of the same name, nothing is
    // written, and what is in the way is listed.
    [Test]
    public async Task ExtractHereStopsAtWhatIsThere()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", ("fresh.txt", "fresh"), ("notes.aleph", "clash"));

        var (outcome, topLevel) = Zip.ExtractTo(zip, fixture.Root, true, null, Cancel.None);

        await Assert.That(outcome.Succeeded).IsFalse();
        await Assert.That(outcome.Summary).IsEqualTo("1 item there already");
        await Assert.That(topLevel).IsEmpty();
        await Assert.That(File.Exists(fixture.At("fresh.txt"))).IsFalse();
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
            var progress = new Reporting(
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
    sealed class Reporting(Action<int> report) : IProgress<int>
    {
        public void Report(int value) =>
            report(value);
    }

    // Zips extracted here together can share a folder, as the parts of a Google Takeout all hold
    // Takeout. Each is extracted in its turn, so the second finds what the first wrote in its
    // way and writes nothing, and the first keeps all it wrote: the two at once, both found
    // nothing in the way, and the one that then failed on what the other wrote took the other's
    // files away with its own.
    [Test]
    public async Task ZipsExtractedTogetherTakeTurns()
    {
        using var fixture = Fixture.Create();
        var first = MakeZip(fixture, "takeout-001.zip", [.. Enumerable.Range(0, 200).Select(_ => ($"Takeout/one-{_}.txt", "one")), ("Takeout/same.txt", "first")]);
        var second = MakeZip(fixture, "takeout-002.zip", [.. Enumerable.Range(0, 200).Select(_ => ($"Takeout/two-{_}.txt", "two")), ("Takeout/same.txt", "second")]);

        var placed = await Zip.ExtractInBackground([first, second], true);

        await Assert.That(Directory.GetFiles(fixture.At("Takeout"), "one-*.txt").Length).IsEqualTo(200);
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Takeout", "same.txt"))).IsEqualTo("first");
        await Assert.That(Directory.GetFiles(fixture.At("Takeout")).Length).IsEqualTo(201);
        await Assert.That(ActionLog.Entries.Where(_ => _.Title == "Extract").Select(_ => $"{_.FolderName}: {_.Summary}"))
            .IsEquivalentTo(["takeout-001.zip: Extracted takeout-001.zip", "takeout-002.zip: 1 item there already"], CollectionOrdering.Matching);
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

    // Cancels on the report given, on the thread reporting it.
    sealed class CancelOnReport(CancelSource cancel, string report) : IProgress<string>
    {
        public void Report(string value)
        {
            if (value == report)
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
}
