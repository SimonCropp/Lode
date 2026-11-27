using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Objects read in process as git reads them, loose and packed every way git packs them, git
/// cat-file the oracle; deltas, loose objects and packs that aren't as git writes them, refused;
/// and commit graphs of every shape, and broken ones passed over, as git passes over them.
/// </summary>
public class ObjectStoreTests
{
    [Test]
    public async Task ParsesAndWritesIds()
    {
        const string hex = "0123456789abcdef0123456789abcdef01234567";
        var parsed = ObjectId.TryParse(hex, out var fromChars);
        var parsedBytes = ObjectId.TryParse(Encoding.ASCII.GetBytes(hex.ToUpperInvariant() + "\n"), out var fromBytes);

        await Assert.That(parsed && parsedBytes).IsTrue();
        await Assert.That(fromChars).IsEqualTo(fromBytes);
        await Assert.That(fromChars.ToString()).IsEqualTo(hex);
        await Assert.That(fromChars.FirstByte).IsEqualTo(1);
        await Assert.That(fromChars.Equals((object)fromBytes)).IsTrue();
        await Assert.That(fromChars.Equals((object)hex)).IsFalse();
        await Assert.That(fromChars.GetHashCode()).IsEqualTo(fromBytes.GetHashCode());
        await Assert.That(fromChars != fromBytes).IsFalse();
        await Assert.That(ObjectId.TryParse("0123", out _)).IsFalse();
        await Assert.That(ObjectId.TryParse(hex.Replace('a', 'g'), out _)).IsFalse();
        await Assert.That(ObjectId.TryParse("0123"u8, out _)).IsFalse();
        await Assert.That(ObjectId.TryParse(Encoding.ASCII.GetBytes(hex.Replace('7', 'z')), out _)).IsFalse();
        await Assert.That(ObjectId.TryParse(Encoding.ASCII.GetBytes("g" + hex[1..]), out _)).IsFalse();

        Span<byte> bytes = stackalloc byte[ObjectId.Length];
        fromChars.CopyTo(bytes);
        var same = fromChars.CompareTo(bytes);
        bytes[19]++;
        var below = fromChars.CompareTo(bytes);
        bytes[19] -= 2;
        var aboveLast = fromChars.CompareTo(bytes);
        bytes[19]++;
        bytes[10]++;
        var belowMiddle = fromChars.CompareTo(bytes);
        bytes[10]--;
        bytes[2]--;
        var aboveFirst = fromChars.CompareTo(bytes);
        await Assert.That((same, below, aboveLast, belowMiddle, aboveFirst)).IsEqualTo((0, -1, 1, -1, 1));
    }

    static byte[] Size(long size)
    {
        var bytes = new List<byte>();
        do
        {
            var c = (byte)(size & 0x7f);
            size >>= 7;
            if (size > 0)
            {
                c |= 0x80;
            }

            bytes.Add(c);
        } while (size > 0);

        return bytes.ToArray();
    }

    // A copy of offset and size, each byte that isn't 0 said by a bit.
    static byte[] Copy(int offset, int size)
    {
        var bytes = new List<byte>
        {
            0x80
        };
        for (var bit = 0; bit < 4; bit++)
        {
            var part = (byte)(offset >> (8 * bit));
            if (part != 0)
            {
                bytes[0] |= (byte)(1 << bit);
                bytes.Add(part);
            }
        }

        for (var bit = 0; bit < 3; bit++)
        {
            var part = (byte)(size >> (8 * bit));
            if (part != 0)
            {
                bytes[0] |= (byte)(0x10 << bit);
                bytes.Add(part);
            }
        }

        return bytes.ToArray();
    }

    static byte[] Delta(int sourceSize, int resultSize, params byte[][] commands) =>
        [.. Size(sourceSize), .. Size(resultSize), .. commands.SelectMany(_ => _)];

    // Copies from the base and bytes of the delta's own, a copy of 64 KB said as size 0; then
    // deltas that don't fit their base, or go past it or their own end, refused.
    [Test]
    public async Task AppliesDeltas()
    {
        var source = Enumerable.Range(0, 0x10000 + 10).Select(_ => (byte)_).ToArray();
        var whole = global::Delta.Apply(source, Delta(source.Length, 0x10000 + 3, [0x80], [3, 7, 8, 9]));
        var offsetCopy = global::Delta.Apply(source, Delta(source.Length, 4, Copy(0x10002, 4)));

        await Assert.That(whole.AsSpan(0, 0x10000).SequenceEqual(source.AsSpan(0, 0x10000))).IsTrue();
        await Assert.That(whole[^3..]).IsEquivalentTo(new byte[] { 7, 8, 9 }, CollectionOrdering.Matching);
        await Assert.That(offsetCopy).IsEquivalentTo(source[0x10002..0x10006], CollectionOrdering.Matching);

        byte[][] bad =
        [
            Delta(source.Length - 1, 1, [1, 0]),
            Delta(source.Length, 4, Copy(source.Length - 2, 4)),
            Delta(source.Length, 8, Copy(0, 4), Copy(0, 8)),
            Delta(source.Length, 3, [5, 1, 2]),
            Delta(source.Length, 1, [2, 1, 2]),
            Delta(source.Length, 1, [0]),
            Delta(source.Length, 5, [1, 1]),
            [0x80],
            [.. Size(source.Length), 0x80]
        ];
        foreach (var delta in bad)
        {
            await Assert.That(() => global::Delta.Apply(source, delta)).Throws<GitFilesException>();
        }

        await Assert.That(() => global::Delta.Apply(source, Delta(source.Length, int.MaxValue + 1L))).Throws<GitFilesException>();
    }

    static byte[] Delta(int sourceSize, long resultSize) =>
        [.. Size(sourceSize), .. Size(resultSize)];

    // A loose object as git writes one, deflated whole, its header first.
    static ObjectId WriteLoose(string objects, string hex, byte[] content)
    {
        var folder = Path.Combine(objects, hex[..2]);
        Directory.CreateDirectory(folder);
        using (var file = File.Create(Path.Combine(folder, hex[2..])))
        using (var zlib = new ZLibStream(file, CompressionLevel.Fastest))
        {
            zlib.Write(content);
        }

        ObjectId.TryParse(hex, out var id);
        return id;
    }

    // What git writes is read, by its header, all of it, and its type alone; what it doesn't
    // is refused: not deflated, no NUL after the header, a type not known, a size not a number,
    // less than the header said, or no object there at all.
    [Test]
    public async Task ReadsLooseObjectsAndRefusesBrokenOnes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var objects = scratch.At("objects");
        Directory.CreateDirectory(objects);
        var blob = WriteLoose(objects, "1000000000000000000000000000000000000001", "blob 3\0abc"u8.ToArray());
        var big = WriteLoose(objects, "1000000000000000000000000000000000000002", [.. "blob 100000\0"u8, .. new byte[100_000]]);
        var commit = WriteLoose(objects, "1000000000000000000000000000000000000003", "commit 0\0"u8.ToArray());
        var tree = WriteLoose(objects, "1000000000000000000000000000000000000004", "tree 0\0"u8.ToArray());
        var tag = WriteLoose(objects, "1000000000000000000000000000000000000005", "tag 0\0"u8.ToArray());
        ObjectId[] broken =
        [
            WriteLoose(objects, "2000000000000000000000000000000000000001", "blob 3abc"u8.ToArray()),
            WriteLoose(objects, "2000000000000000000000000000000000000002", "wat 3\0abc"u8.ToArray()),
            WriteLoose(objects, "2000000000000000000000000000000000000003", "blob x\0abc"u8.ToArray()),
            WriteLoose(objects, "2000000000000000000000000000000000000004", "blob 10\0abc"u8.ToArray()),
            WriteLoose(objects, "2000000000000000000000000000000000000005", Encoding.ASCII.GetBytes($"blob {new string('9', 70)}\0abc")),
            WriteLoose(objects, "2000000000000000000000000000000000000006", "blob 99999999999\0abc"u8.ToArray())
        ];
        var notDeflated = scratch.At("objects", "30", "00000000000000000000000000000000000001");
        Directory.CreateDirectory(Path.GetDirectoryName(notDeflated)!);
        await File.WriteAllBytesAsync(notDeflated, [1, 2, 3, 4, 5, 6]);
        ObjectId.TryParse("3000000000000000000000000000000000000001", out var garbage);
        ObjectId.TryParse("4000000000000000000000000000000000000001", out var missing);
        using var database = new ObjectDatabase(objects);

        await Assert.That(database.TryRead(blob) is { Type: GitObjectType.Blob, Data: [(byte)'a', (byte)'b', (byte)'c'] }).IsTrue();
        await Assert.That(database.TryReadHeader(big)).IsEqualTo((GitObjectType.Blob, 100_000L));
        await Assert.That(database.Read(big).Data.Length).IsEqualTo(100_000);
        await Assert.That(database.TryReadHeader(commit)?.Type).IsEqualTo(GitObjectType.Commit);
        await Assert.That(database.TryReadHeader(tree)?.Type).IsEqualTo(GitObjectType.Tree);
        await Assert.That(database.TryReadHeader(tag)?.Type).IsEqualTo(GitObjectType.Tag);
        await Assert.That(database.TryRead(missing)).IsNull();
        await Assert.That(database.TryReadHeader(missing)).IsNull();
        await Assert.That(() => database.Read(missing)).Throws<GitFilesException>();
        await Assert.That(() => database.Read(blob, GitObjectType.Tree)).Throws<GitFilesException>();
        foreach (var id in broken.Append(garbage))
        {
            await Assert.That(() => database.TryRead(id)).Throws<GitFilesException>();
        }

        await Assert.That(() => database.TryReadHeader(garbage)).Throws<GitFilesException>();
        await Assert.That(() => database.TryReadHeader(broken[0])).Throws<GitFilesException>();
    }

    // A repository with history enough for deltas: files changed a line at a time, one of over
    // 64 KB, in folders, merged and tagged.
    static string History(CodeCommandsTests.Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "folder", "deeper"));
        var lines = Enumerable.Range(0, 400).Select(_ => $"Line {_} of a file that changes a line at a time").ToList();
        for (var index = 0; index < 12; index++)
        {
            lines[index * 7] = $"Changed by {index}";
            File.WriteAllText(Path.Combine(repo, "big.txt"), string.Join('\n', Enumerable.Repeat(string.Join('\n', lines), 4)));
            File.WriteAllText(Path.Combine(repo, "folder", "deeper", $"file{index % 3}.txt"), string.Join('\n', lines.Take(60)));
            CodeCommandsTests.Scratch.Git(repo, "add", ".");
            CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", $"Commit {index}");
        }

        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "v1", "-m", "One");
        return repo;
    }

    static string PackFile(string repo, string extension) =>
        Directory.EnumerateFiles(Path.Combine(repo, ".git", "objects", "pack"), $"*.{extension}").Single();

    // Every object the repository has: its type and size as git cat-file gives them, and its
    // content, which hashes to its id.
    static async Task ReadsEveryObjectAsGit(string repo)
    {
        var listed = GitRun.Output(repo, "cat-file", "--batch-all-objects", "--batch-check");
        await Assert.That(listed.Length).IsGreaterThan(0);
        using var database = new ObjectDatabase(Path.Combine(repo, ".git", "objects"));
        foreach (var line in listed.Split('\n'))
        {
            var parts = line.Split(' ');
            ObjectId.TryParse(parts[0], out var id);
            var header = database.TryReadHeader(id);
            var read = database.TryRead(id)!.Value;
            var hashed = SHA1.HashData([.. Encoding.ASCII.GetBytes($"{parts[1]} {read.Data.Length}\0"), .. read.Data]);
            await Assert.That((header?.Type.ToString().ToLowerInvariant(), header?.Size)).IsEqualTo((parts[1], long.Parse(parts[2], CultureInfo.InvariantCulture)));
            await Assert.That(read.Type).IsEqualTo(header!.Value.Type);
            await Assert.That(new ObjectId(hashed)).IsEqualTo(id);
        }

        await GitRun.Agree(repo);
    }

    // Loose; packed by gc, deltas on bases by where they are; by ids, as packs without offset
    // deltas have them; with a version 1 index, and one giving every offset in 8 bytes, as for a
    // pack past 2 GB.
    [Test]
    [Arguments("loose")]
    [Arguments("gc")]
    [Arguments("mixed")]
    [Arguments("ids")]
    [Arguments("version1")]
    [Arguments("offsets64")]
    public async Task ReadsObjectsPackedEveryWay(string packing)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = History(scratch);
        switch (packing)
        {
            case "gc":
                CodeCommandsTests.Scratch.Git(repo, "gc", "-q");
                break;
            case "mixed":
                CodeCommandsTests.Scratch.Git(repo, "gc", "-q");
                File.AppendAllText(Path.Combine(repo, "big.txt"), "\nAfter the pack");
                CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-am", "Loose");
                break;
            case "ids":
                CodeCommandsTests.Scratch.Git(repo, "-c", "repack.usedeltabaseoffset=false", "repack", "-adfq");
                break;
            case "version1" or "offsets64":
                CodeCommandsTests.Scratch.Git(repo, "repack", "-adfq");
                var pack = PackFile(repo, "pack");
                var index = PackFile(repo, "idx");
                File.SetAttributes(index, FileAttributes.Normal);
                File.Delete(index);
                var version = "--index-version=1";
                if (packing == "offsets64")
                {
                    version = "--index-version=2,12";
                }

                CodeCommandsTests.Scratch.Git(repo, "index-pack", version, "-o", index, pack);
                break;
        }

        await ReadsEveryObjectAsGit(repo);
    }

    // The bases of deltas kept are let go of once they pass what may be kept, and put together
    // again when wanted.
    [Test]
    [NotInParallel("PackCache")]
    public async Task LetsGoOfBasesPastTheLimit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = History(scratch);
        CodeCommandsTests.Scratch.Git(repo, "gc", "-q");
        var limit = global::PackFile.CacheLimit;
        global::PackFile.CacheLimit = 1;
        try
        {
            await ReadsEveryObjectAsGit(repo);
        }
        finally
        {
            global::PackFile.CacheLimit = limit;
        }
    }

    // Objects in another repository's, as git clone --shared leaves them, and as a hand-written
    // alternates file names them, relative, commented, and naming a folder twice; followed five
    // deep, as git follows them.
    [Test]
    public async Task ReadsObjectsOfAlternates()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        History(scratch);
        CodeCommandsTests.Scratch.Git(scratch.Root, "clone", "-q", "--shared", scratch.At("Repo"), scratch.At("Shared"));
        await ReadsEveryObjectAsGit(scratch.At("Shared"));

        // Chained: each folder names the one after it, the last with an object of its own.
        var folders = Enumerable.Range(0, 8).Select(_ => scratch.At($"chain{_}")).ToList();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(folder, "info"));
        }

        for (var index = 0; index < folders.Count - 1; index++)
        {
            var next = Path.GetRelativePath(folders[index], folders[index + 1]);
            await File.WriteAllTextAsync(Path.Combine(folders[index], "info", "alternates"), $"# The next\n\n{next}\n{folders[index]}\n");
        }

        var ids = new List<ObjectId>();
        for (var index = 0; index < folders.Count; index++)
        {
            ids.Add(WriteLoose(folders[index], $"5{index}00000000000000000000000000000000000001", "blob 1\0x"u8.ToArray()));
        }

        using var database = new ObjectDatabase(folders[0]);
        var found = ids.Select(_ => database.TryRead(_) != null).ToList();

        await Assert.That(found).IsEquivalentTo([true, true, true, true, true, true, false, false], CollectionOrdering.Matching);
    }

    // A pack whose index is of a version not known, whose pack isn't one, or whose objects are
    // of a type not known, too big, or not deflated, is refused; an index without its pack, half
    // written, is passed over.
    [Test]
    public async Task RefusesBrokenPacks()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = History(scratch);
        CodeCommandsTests.Scratch.Git(repo, "repack", "-adfq");
        var pack = PackFile(repo, "pack");
        var index = PackFile(repo, "idx");
        var objects = Path.Combine(repo, ".git", "objects");
        File.Copy(index, Path.Combine(objects, "pack", "pack-stray.idx"));
        var verified = GitRun.Output(repo, "verify-pack", "-v", index);
        var blob = verified
            .Split('\n')
            .Select(_ => _.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .First(_ => _.Length == 5 && _[1] == "blob");
        ObjectId.TryParse(blob[0], out var id);
        var offset = long.Parse(blob[4], CultureInfo.InvariantCulture);
        File.SetAttributes(pack, FileAttributes.Normal);
        File.SetAttributes(index, FileAttributes.Normal);
        var original = await File.ReadAllBytesAsync(pack);

        // Passed over: the stray index's pack isn't there.
        using (var database = new ObjectDatabase(objects))
        {
            await Assert.That(database.TryRead(id)?.Type).IsEqualTo(GitObjectType.Blob);
        }

        async Task Refused(Action<byte[]> breakIt)
        {
            var broken = (byte[])original.Clone();
            breakIt(broken);
            await File.WriteAllBytesAsync(pack, broken);
            using var database = new ObjectDatabase(objects);
            await Assert.That(() => database.TryRead(id)).Throws<GitFilesException>();
        }

        await Refused(_ => "XXXX"u8.CopyTo(_));
        await Refused(_ => _[offset] = (byte)((_[offset] & 0x8f) | 0x50));
        await Refused(_ => new byte[] { 0xbf, 0xff, 0xff, 0xff, 0xff, 0x7f }.CopyTo(_, offset));
        await Refused(_ =>
        {
            var data = offset + 1;
            while ((_[data - 1] & 0x80) != 0)
            {
                data++;
            }

            _[data] = 0xff;
            _[data + 1] = 0xff;
        });

        // A delta whose own start isn't deflated, read only for how big it is.
        var delta = verified
            .Split('\n')
            .Select(_ => _.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .First(_ => _.Length == 7);
        ObjectId.TryParse(delta[0], out var deltaId);
        var deltaOffset = long.Parse(delta[4], CultureInfo.InvariantCulture);
        var brokenDelta = (byte[])original.Clone();
        var start = deltaOffset;
        while ((brokenDelta[start++] & 0x80) != 0)
        {
        }

        while ((brokenDelta[start++] & 0x80) != 0)
        {
        }

        brokenDelta[start] = 0xff;
        brokenDelta[start + 1] = 0xff;
        await File.WriteAllBytesAsync(pack, brokenDelta);
        using (var database = new ObjectDatabase(objects))
        {
            await Assert.That(() => database.TryReadHeader(deltaId)).Throws<GitFilesException>();
        }

        // An index naming where no object of the pack is, past its end.
        await File.WriteAllBytesAsync(pack, original);
        var indexBytes = await File.ReadAllBytesAsync(index);
        var count = BinaryPrimitives.ReadInt32BigEndian(indexBytes.AsSpan(8 + 255 * 4));
        var offsets = 8 + 256 * 4 + count * 24;
        var pastTheEnd = (byte[])indexBytes.Clone();
        for (var at = 0; at < count; at++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(pastTheEnd.AsSpan(offsets + at * 4), 0x7fff_fff0);
        }

        await File.WriteAllBytesAsync(index, pastTheEnd);
        using (var database = new ObjectDatabase(objects))
        {
            await Assert.That(() => database.TryRead(id)).Throws<GitFilesException>();
        }

        indexBytes[7] = 3;
        await File.WriteAllBytesAsync(index, indexBytes);
        using var unknown = new ObjectDatabase(objects);
        await Assert.That(() => unknown.TryRead(id)).Throws<GitFilesException>();
    }

    static void Import(string repo, string stream)
    {
        var result = GitRun.Run(repo, null, Encoding.UTF8.GetBytes(stream), "fast-import", "--quiet");
        if (!result.Succeeded)
        {
            throw new($"git fast-import failed: {result.Error}");
        }
    }

    // An octopus merge of seventy branches, its parents past the two a commit graph has room
    // for, waiting to be walked all at once; and commits since the graph was written, which it
    // hasn't got.
    static string Octopus(CodeCommandsTests.Scratch scratch)
    {
        var repo = scratch.At("Octopus");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init", "-q", "--initial-branch=main");
        var stream = new StringBuilder();
        void Commit(string branch, int mark, long time, string file, string? from, IEnumerable<int> merges)
        {
            stream.Append($"commit refs/heads/{branch}\nmark :{mark}\n");
            stream.Append($"author Test <test@example.com> {time} +0000\ncommitter Test <test@example.com> {time} +0000\n");
            var message = $"Commit {mark}\n";
            stream.Append($"data {Encoding.UTF8.GetByteCount(message)}\n{message}");
            if (from != null)
            {
                stream.Append($"from {from}\n");
            }

            foreach (var merge in merges)
            {
                stream.Append($"merge :{merge}\n");
            }

            var content = $"{mark}\n";
            stream.Append($"M 100644 inline {file}\ndata {content.Length}\n{content}\n");
        }

        Commit("main", 1, 1_600_000_000, "base.txt", null, []);
        for (var branch = 0; branch < 70; branch++)
        {
            Commit($"b{branch}", branch + 2, 1_600_000_100 + branch * 7 % 11, $"file{branch}.txt", ":1", []);
        }

        Commit("main", 100, 1_600_001_000, "merged.txt", ":1", Enumerable.Range(2, 70));
        Import(repo, stream.ToString());
        CodeCommandsTests.Scratch.Git(repo, "reset", "-q", "--hard", "main");
        return repo;
    }

    // Each commit as a line, or none at all where it couldn't be read.
    static List<string> Lines(List<GitCommit>? commits)
    {
        if (commits == null)
        {
            return ["none"];
        }

        return commits
            .Select(_ => $"{_.Sha} [{string.Join(' ', _.Parents)}] {_.Author} {_.When.ToUnixTimeSeconds()} [{string.Join(' ', _.Tags)}] {_.Subject}")
            .ToList();
    }

    static async Task HistoryAgrees(string repo)
    {
        await Assert.That(Lines(InProcessHistory.Read(repo, 0, 500, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, 500, Cancel.None)), CollectionOrdering.Matching);
        await Assert.That(Lines(InProcessHistory.Read(repo, 3, 40, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 3, 40, Cancel.None)), CollectionOrdering.Matching);
    }

    // Walked from its objects, then by a commit graph of one file, and by a chain of them, as
    // fetch writes them, each with commits made after it.
    [Test]
    public async Task WalksEveryShapeOfCommitGraph()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Octopus(scratch);
        await HistoryAgrees(repo);

        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable");
        await Assert.That(File.Exists(Path.Combine(repo, ".git", "objects", "info", "commit-graph"))).IsTrue();
        await File.WriteAllTextAsync(Path.Combine(repo, "after.txt"), "after");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "After the graph");
        await HistoryAgrees(repo);

        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable", "--split=no-merge");
        await File.WriteAllTextAsync(Path.Combine(repo, "later.txt"), "later");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "Later still");
        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable", "--split=no-merge");
        var chain = Path.Combine(repo, ".git", "objects", "info", "commit-graphs", "commit-graph-chain");
        await Assert.That((await File.ReadAllLinesAsync(chain)).Length).IsGreaterThan(1);
        await HistoryAgrees(repo);

        // A chain naming a file not there, after a blank line, is read as far as that.
        File.SetAttributes(chain, FileAttributes.Normal);
        await File.AppendAllTextAsync(chain, "\n" + new string('0', 40) + "\n");
        await HistoryAgrees(repo);
        await GitRun.Agree(repo);
    }

    // A thousand commits, merging a branch every so often, by a commit graph whose commits are
    // too many for each first byte to have only one: as git log and git describe find them.
    [Test]
    public async Task WalksABigCommitGraph()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.At("Big");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init", "-q", "--initial-branch=main");
        var stream = new StringBuilder();
        var main = 0;
        var mark = 0;
        for (var index = 0; index < 1000; index++)
        {
            var time = 1_600_000_000 + index * 60;
            var side = 0;
            if (index > 0 &&
                index % 25 == 0)
            {
                side = ++mark;
                stream.Append($"commit refs/heads/side\nmark :{side}\ncommitter T <t@example.com> {time - 30} +0000\ndata 5\nside\nfrom :{main}\n");
                stream.Append($"M 100644 inline side.txt\ndata {index.ToString(CultureInfo.InvariantCulture).Length}\n{index}\n\n");
            }

            var commit = ++mark;
            stream.Append($"commit refs/heads/main\nmark :{commit}\ncommitter T <t@example.com> {time} +0000\ndata 4\nmain\n");
            if (main > 0)
            {
                stream.Append($"from :{main}\n");
            }

            if (side > 0)
            {
                stream.Append($"merge :{side}\n");
            }

            stream.Append($"M 100644 inline main.txt\ndata {index.ToString(CultureInfo.InvariantCulture).Length}\n{index}\n\n");
            if (index % 100 == 50)
            {
                stream.Append($"reset refs/tags/t{index}\nfrom :{commit}\n\n");
            }

            main = commit;
        }

        Import(repo, stream.ToString());
        CodeCommandsTests.Scratch.Git(repo, "reset", "-q", "--hard", "main");
        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable");

        await HistoryAgrees(repo);
        await Assert.That(Lines(InProcessHistory.Read(repo, 900, 100, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 900, 100, Cancel.None)), CollectionOrdering.Matching);
        await GitRun.Agree(repo);

        // Every commit found in it where it is, and objects that aren't commits not found.
        using var graph = CommitGraph.Open(Path.Combine(repo, ".git", "objects"))!;
        var found = 0;
        foreach (var line in GitRun.Output(repo, "rev-list", "--all").Split('\n'))
        {
            ObjectId.TryParse(line, out var id);
            if (graph.TryFind(id, out var position) &&
                graph.IdAt(position) == id)
            {
                found++;
            }
        }

        ObjectId.TryParse(GitRun.Output(repo, "rev-parse", "HEAD^{tree}"), out var tree);
        await Assert.That(found).IsEqualTo(graph.Count);
        await Assert.That(graph.TryFind(tree, out _)).IsFalse();
    }

    // A graph that isn't one git writes, or is cut short, is passed over as git passes over it:
    // the history is walked from the objects instead.
    [Test]
    [Arguments("empty")]
    [Arguments("signature")]
    [Arguments("version")]
    [Arguments("short")]
    [Arguments("chunks")]
    [Arguments("fanout")]
    [Arguments("counts")]
    public async Task PassesOverBrokenCommitGraphs(string breaking)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = History(scratch);
        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable");
        var objects = Path.Combine(repo, ".git", "objects");
        var graph = Path.Combine(objects, "info", "commit-graph");
        File.SetAttributes(graph, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(graph);
        switch (breaking)
        {
            case "empty":
                bytes = [];
                break;
            case "signature":
                bytes[0] = (byte)'X';
                break;
            case "version":
                bytes[4] = 2;
                break;
            case "short":
                bytes = bytes[..10];
                break;
            case "chunks":
                bytes[6] = 0;
                break;
            case "fanout":
                bytes = bytes[..(bytes.Length / 2)];
                break;
            case "counts":
                // As many commits as the fan out says, more than the file holds.
                for (var chunk = 0; chunk < bytes[6]; chunk++)
                {
                    if (Encoding.ASCII.GetString(bytes, 8 + chunk * 12, 4) == "OIDF")
                    {
                        var fanout = (int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8 + chunk * 12 + 4));
                        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(fanout + 255 * 4), 0x00ff_ffff);
                    }
                }

                break;
        }

        await File.WriteAllBytesAsync(graph, bytes);

        await Assert.That(CommitGraph.Open(objects)).IsNull();
        await HistoryAgrees(repo);
    }

    // A graph whose commits name parents it hasn't got, or say they have more than two but have
    // no edges to say which, is refused, and the history left to git.
    [Test]
    [Arguments("parent")]
    [Arguments("edges")]
    public async Task RefusesCommitGraphsNamingWhatTheyHaventGot(string breaking)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = Octopus(scratch);
        CodeCommandsTests.Scratch.Git(repo, "commit-graph", "write", "--reachable");
        var graph = Path.Combine(repo, ".git", "objects", "info", "commit-graph");
        File.SetAttributes(graph, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(graph);
        var table = 8;
        for (var chunk = 0; chunk < bytes[6]; chunk++, table += 12)
        {
            var id = Encoding.ASCII.GetString(bytes, table, 4);
            if (breaking == "edges" && id == "EDGE")
            {
                "XDGE"u8.CopyTo(bytes.AsSpan(table));
            }

            if (breaking == "parent" && id == "CDAT")
            {
                var data = (int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(table + 4));
                for (var at = data; at < data + 36 * 71; at += 36)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 20), 0x6fff_ffff);
                }
            }
        }

        await File.WriteAllBytesAsync(graph, bytes);

        await Assert.That(InProcessHistory.Read(repo, 0, 500, Cancel.None)).IsNull();
        await Assert.That(Lines(InProcessHistory.ReadFaster(repo, 0, 500, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, 500, Cancel.None)), CollectionOrdering.Matching);
    }
}
