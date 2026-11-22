

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
            });
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
            .IsEquivalentTo(["notes.zip", "notes (2).zip", "Docs.zip"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
            });
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

        await Assert.That(extraction.TopLevel.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Docs", "New"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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

    [Test]
    public async Task Segments()
    {
        await Assert.That(Zip.Segments("a/b\\c.txt")).IsEquivalentTo(["a", "b", "c.txt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Zip.Segments("folder/")).IsEquivalentTo(["folder"]);
        await Assert.That(Zip.Segments("a/../b")).IsNull();
        await Assert.That(Zip.Segments("a/./b")).IsNull();
        await Assert.That(Zip.Segments("\\a")).IsNull();
        await Assert.That(Zip.Segments("a/b?")).IsNull();
        await Assert.That(Zip.Segments("a /b")).IsNull();
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
                        .ToList()));
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
            await Assert.That(extracted.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["top.txt", "Inner"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
}
