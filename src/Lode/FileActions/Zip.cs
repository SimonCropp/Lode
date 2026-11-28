/// <summary>
/// Zip archives: making one of what is selected, and taking what one holds out of it, as
/// Explorer's Compress to ZIP file and Extract All do, each as an entry in the Actions panel,
/// which shows how far it has got and can stop it. And looking inside one, as a folder that can't
/// be changed: what is opened, copied or dragged out of it is extracted to the temp folder first.
/// Tars, .tar, .tar.gz and .tgz, are extracted and looked inside the same way, though not made.
/// </summary>
static class Zip
{
    public const string Extension = ".zip";

    // What an archive is named with: a zip, or a tar, gzipped or not. 7z and rar, which Explorer
    // opens too, would need a library to read.
    static string[] extensions = [Extension, ".tar", ".tar.gz", ".tgz"];

    static string[] gzippedTarExtensions = [".tar.gz", ".tgz"];

    // For reading. An entry that says its name is UTF-8 is read as that whatever is given here:
    // this is for those that don't say.
    static ZipArchive OpenRead(string zip) =>
        ZipFile.Open(zip, ZipArchiveMode.Read, entryNames);

    static EntryNames entryNames = new();

    // How an entry is named when it doesn't say. As UTF-8 where its bytes are that, as zips made
    // on a Mac, or by Linux's zip, are named without saying so. Otherwise in the code page of
    // programs that know no Unicode, which Windows' own zips were long named in, and DOS's
    // before them: read as UTF-8, as they are when nothing is given, the é of such a name came
    // out as the mark for a character that couldn't be read.
    sealed class EntryNames : Encoding
    {
        static Encoding unsaid = CodePagesEncodingProvider.Instance.GetEncoding((int) GetOEMCP()) ?? UTF8;

        static Encoding Of(byte[] bytes, int index, int count)
        {
            if (Utf8.IsValid(bytes.AsSpan(index, count)))
            {
                return UTF8;
            }

            return unsaid;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) =>
            Of(bytes, index, count).GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            Of(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override int GetMaxCharCount(int byteCount) =>
            Math.Max(UTF8.GetMaxCharCount(byteCount), unsaid.GetMaxCharCount(byteCount));

        // Names are only ever read with it: nothing is written.
        public override int GetByteCount(char[] chars, int index, int count) =>
            UTF8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

        public override int GetMaxByteCount(int charCount) =>
            UTF8.GetMaxByteCount(charCount);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is named as an archive Lode reads: a zip, or a tar,
    /// gzipped or not.
    /// </summary>
    public static bool IsArchive(string path) =>
        IsArchive(path.AsSpan());

    static bool IsArchive(CharSpan path)
    {
        foreach (var extension in extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsTar(string path) =>
        !path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) &&
        IsArchive(path);

    static bool IsGzipped(string path) =>
        gzippedTarExtensions.Any(_ => path.EndsWith(_, StringComparison.OrdinalIgnoreCase));

    // What messages call the kind of archive path is.
    static string Noun(string path)
    {
        if (IsTar(path))
        {
            return "tar";
        }

        return "zip";
    }

    // An archive's name without what names it an archive: Bundle, of Bundle.zip, Bundle.tar.gz
    // or Bundle.tgz, where Bundle.tar would be left of Bundle.tar.gz without its last extension.
    static string ArchiveStem(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var extension in extensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// What an entry of an archive is: a file, a folder, a link, symbolic or hard, which is
    /// neither followed nor made, as it could lead outside where it is extracted to, or
    /// something else, a device or a pipe, which Windows has no file for.
    /// </summary>
    enum EntryKind
    {
        File,
        Folder,
        Link,
        Special
    }

    // An entry of an archive as it is read: its name in it, what it is, how much it holds, and
    // when it was last changed.
    abstract class ArchiveEntry(string name, EntryKind kind, long length, DateTimeOffset modified)
    {
        public string Name { get; } = name;

        public EntryKind Kind { get; } = kind;

        public bool IsFolder => Kind == EntryKind.Folder;

        // Neither a file nor a folder, and so left out.
        public bool IsLeftOut => Kind is EntryKind.Link or EntryKind.Special;

        public long Length { get; } = length;

        public DateTimeOffset Modified { get; } = modified;

        // Writes what it holds to target, a piece at a time, told of each, and stopped by cancel
        // between pieces. Only while it is the entry come to: a tar is read from its start to its
        // end, and what an entry holds is passed by once the next is read.
        public abstract void CopyTo(Stream target, Action<long>? copied, Cancel cancel);
    }

    // An archive, its entries read in its order, from the first, each time they are asked for.
    abstract class Reader : IDisposable
    {
        public static Reader Open(string path)
        {
            if (IsTar(path))
            {
                return new TarFile(path, IsGzipped(path));
            }

            return new ZipReader(path);
        }

        public abstract IEnumerable<ArchiveEntry> Entries();

        public virtual void Dispose()
        {
        }
    }

    // A zip, which is read from its end, where it lists what it holds, and its entries from
    // wherever they are in it, so read once, and its entries kept.
    sealed class ZipReader(string path) : Reader
    {
        ZipArchive archive = OpenRead(path);
        List<ArchiveEntry>? entries;

        public override IEnumerable<ArchiveEntry> Entries() =>
            entries ??= archive.Entries
                .Select(_ => (ArchiveEntry) new ZipMember(_))
                .ToList();

        public override void Dispose() =>
            archive.Dispose();
    }

    sealed class ZipMember(ZipArchiveEntry entry) :
        ArchiveEntry(entry.FullName, IsFolderEntry(entry.FullName) ? EntryKind.Folder : EntryKind.File, entry.Length, entry.LastWriteTime)
    {
        public override void CopyTo(Stream target, Action<long>? copied, Cancel cancel)
        {
            using var content = entry.Open();
            Copy(content, target, copied, cancel);
        }
    }

    // A tar, or a tar gzipped, as a .tar.gz or .tgz is, which can only be read from its start,
    // so is, each time its entries are asked for.
    sealed class TarFile(string path, bool gzipped) : Reader
    {
        public override IEnumerable<ArchiveEntry> Entries()
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, pieceLength);
            Stream content = file;
            if (gzipped)
            {
                content = new GZipStream(file, CompressionMode.Decompress);
            }

            using var reader = new TarReader(content);
            var first = true;
            while (Next(reader, first) is { } entry)
            {
                first = false;

                // What a pax tar says of all that follows, not an item of its own.
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                yield return new TarMember(entry, Unprefixed(entry.Name), KindOf(entry.EntryType));
            }
        }

        // The next entry, or null at the end. What can't be read at all, a .tar.gz that is a
        // gzipped text say, or no gzip at all, is said to be no tar; one that ends part way,
        // as a download not finished does, to be cut short.
        TarEntry? Next(TarReader reader, bool first)
        {
            try
            {
                return reader.GetNextEntry();
            }
            catch (Exception exception)
                when (
                    first &&
                    exception is
                        InvalidDataException or
                        EndOfStreamException)
            {
                var kind = "a tar archive";
                if (gzipped)
                {
                    kind = "a tar archive compressed with gzip";
                }

                throw new InvalidDataException($"{Path.GetFileName(path)} is not {kind}.", exception);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} ends part way through what it holds, as one not all downloaded does.", exception);
            }
        }

        static EntryKind KindOf(TarEntryType type) =>
            type switch
            {
                TarEntryType.RegularFile or
                    TarEntryType.V7RegularFile or
                    TarEntryType.ContiguousFile => EntryKind.File,
                TarEntryType.Directory or
                    TarEntryType.DirectoryList => EntryKind.Folder,
                TarEntryType.SymbolicLink or
                    TarEntryType.HardLink => EntryKind.Link,
                _ => EntryKind.Special
            };

        // Without the ./ a tar of a folder made as itself, by tar -cf folder.tar . say, puts
        // before every name: ./src/main.c is src/main.c, and ./ the tar's top.
        static string Unprefixed(string name)
        {
            while (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name == ".")
            {
                return "";
            }

            return name;
        }
    }

    sealed class TarMember(TarEntry entry, string name, EntryKind kind) :
        ArchiveEntry(name, kind, entry.Length, entry.ModificationTime)
    {
        // Not disposed: the reader goes on from where it ends to the next entry.
        public override void CopyTo(Stream target, Action<long>? copied, Cancel cancel)
        {
            if (entry.DataStream is { } content)
            {
                Copy(content, target, copied, cancel);
            }
        }
    }

    /// <summary>
    /// What an archive of <paramref name="source"/> is named for, as Explorer names it: a
    /// folder's name, or a file's without its extension.
    /// </summary>
    public static string Stem(string source)
    {
        if (Directory.Exists(source))
        {
            return Path.GetFileName(source);
        }

        return Path.GetFileNameWithoutExtension(source);
    }

    /// <summary>
    /// A path in <paramref name="folder"/> nothing is at yet: "<paramref name="stem"/>.zip",
    /// then "<paramref name="stem"/> (2).zip", or, with no extension, a folder's name.
    /// </summary>
    public static string FreePath(string folder, string stem, string extension = Extension)
    {
        var path = Path.Combine(folder, stem + extension);
        for (var counter = 2; Path.Exists(path); counter++)
        {
            path = Path.Combine(folder, $"{stem} ({counter}){extension}");
        }

        return path;
    }

    /// <summary>
    /// Makes a zip of <paramref name="sources"/> in <paramref name="folder"/>, named for the
    /// first of them, in the Actions panel. Returns its path once it is made, or null when it
    /// failed or was cancelled, which leaves no part of it behind.
    /// </summary>
    public static async Task<string?> CompressInBackground(IReadOnlyList<string> sources, string folder)
    {
        var zip = FreePath(folder, Stem(sources[0]));
        var made = false;
        await ActionLog.Run(
            "Compress",
            [folder],
            (progress, cancel) => Task.Run(
                () =>
                {
                    var outcome = Compress(zip, sources, progress, cancel);
                    made = outcome.Succeeded;
                    return outcome;
                },
                cancel));
        if (made)
        {
            return zip;
        }

        return null;
    }

    /// <summary>
    /// Writes <paramref name="sources"/> into a new zip at <paramref name="zip"/>, each at its
    /// top: a folder under its own name, with all it holds, empty folders included. Undo sends the
    /// zip to the Recycle Bin. When it fails, or <paramref name="cancel"/> stops it, the zip
    /// is deleted.
    /// </summary>
    public static ActionLog.Outcome Compress(string zip, IReadOnlyList<string> sources, IProgress<string>? progress, Cancel cancel)
    {
        // The files, and the folders with nothing in them, which a zip holds as entries of their
        // own. A folder with something in it is there in the names of what it holds.
        var items = new List<(string Path, string Name, bool IsFolder, long Length)>();

        // Items of more than one folder, as a search finds, can share a name, which the zip
        // would then hold twice, and extracting it fail on. Those after the first are numbered,
        // as Explorer numbers a copy.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            if (Directory.Exists(source))
            {
                AddFolder(source, Untaken(name, "", taken), items, zip);
                continue;
            }

            var extension = Path.GetExtension(name);
            items.Add((source, Untaken(name[..^extension.Length], extension, taken), false, new FileInfo(source).Length));
        }

        var files = items.Count(_ => !_.IsFolder);
        var reporter = new Reporter(progress, "Compressing", files, items.Sum(_ => _.Length));
        Action<long> copied = reporter.Copied;
        try
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var (path, name, isFolder, _) in items)
            {
                cancel.ThrowIfCancellationRequested();
                if (isFolder)
                {
                    archive.CreateEntry(name + '/');
                    continue;
                }

                reporter.Next();
                AddFile(archive, path, name, copied, cancel);
            }
        }
        catch
        {
            TryDelete(zip);
            throw;
        }

        UndoHistory.Instance.RecordCreated("compress", [zip]);
        return new(true, $"Compressed {Count(files, "file")} into {Path.GetFileName(zip)}", zip);
    }

    // The name, or, once taken, the first of "name (2)", "name (3)" and so on that isn't.
    static string Untaken(string stem, string extension, HashSet<string> taken)
    {
        var name = stem + extension;
        for (var number = 2; !taken.Add(name); number++)
        {
            name = $"{stem} ({number}){extension}";
        }

        return name;
    }

    // Writes the file at path into the zip, named name, as CreateEntryFromFile does, with the
    // time it was last changed, or, should the zip be unable to hold that, the first it can, as
    // CreateEntryFromFile gives it. But a piece at a time, told of each, and stopped by cancel
    // between pieces, so a big file stops within one, rather than once all of it is written.
    static void AddFile(ZipArchive archive, string path, string name, Action<long> copied, Cancel cancel)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        var written = File.GetLastWriteTime(path);
        if (written.Year is < 1980 or > 2107)
        {
            written = new(1980, 1, 1, 0, 0, 0);
        }

        entry.LastWriteTime = written;
        using var target = entry.Open();
        Copy(source, target, copied, cancel);
    }

    // Copies source to target a piece at a time, told of each piece, and stopped by cancel
    // between pieces.
    static void Copy(Stream source, Stream target, Action<long>? copied, Cancel cancel)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(pieceLength);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, pieceLength)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                copied?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // What is copied at a time, as Stream.CopyTo copies.
    const int pieceLength = 81_920;

    /// <summary>
    /// Says how far compressing or extracting has got: how many files it has come to, and how
    /// much of what they hold it has written, a big file saying nothing else for as long as it
    /// takes. Only when that changes: each piece of a big file would otherwise tell the panel.
    /// </summary>
    sealed class Reporter(IProgress<string>? progress, string doing, int files, long total)
    {
        int file;
        long done;
        string reported = "";

        public void Next()
        {
            file++;
            Report();
        }

        public void Copied(long bytes)
        {
            done += bytes;
            Report();
        }

        void Report()
        {
            if (progress == null)
            {
                return;
            }

            var text = $"{doing} {file} of {files}, {Formatting.Size(done)} of {Formatting.Size(total)}";
            if (text == reported)
            {
                return;
            }

            reported = text;
            progress.Report(text);
        }
    }

    // A folder, named in the zip as zips name what is in them, with forward slashes: what it
    // holds, or itself when it holds nothing. Not the zip being made, should it be in there.
    static void AddFolder(string folder, string name, List<(string, string, bool, long)> items, string zip)
    {
        var before = items.Count;
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false
        };
        var holds = new DirectoryInfo(folder);
        foreach (var directory in holds.EnumerateDirectories("*", options).Where(IsNotLink).OrderBy(_ => _.FullName, StringComparer.OrdinalIgnoreCase))
        {
            AddFolder(directory.FullName, $"{name}/{directory.Name}", items, zip);
        }

        foreach (var file in holds.EnumerateFiles("*", options).Where(IsNotLink).OrderBy(_ => _.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (!Locations.Same(file.FullName, zip))
            {
                items.Add((file.FullName, $"{name}/{file.Name}", false, file.Length));
            }
        }

        if (items.Count == before)
        {
            items.Add((folder, name, true, 0));
        }
    }

    // Not a junction or a symbolic link, which lead elsewhere, round in a circle perhaps, and
    // are left out. The file system marks more than those the way it marks them: a folder kept
    // in cloud storage, OneDrive's say, which, left out by the mark, took all it holds with it.
    static bool IsNotLink(FileSystemInfo item) =>
        (item.Attributes & FileAttributes.ReparsePoint) == 0 ||
        item.LinkTarget == null;

    /// <summary>
    /// What extracting a zip did: what it made at the top of where it went, what it made there or
    /// beneath, for undo (the highest of each, made by it rather than found there), what it left
    /// out, and why, and what was already there, which stops it before anything is written, as
    /// <see cref="TooLittleRoom"/> and <see cref="Unreadable"/> do.
    /// </summary>
    public sealed record Extraction(
        IReadOnlyList<string> TopLevel,
        IReadOnlyList<string> Created,
        IReadOnlyList<LeftOutEntry> LeftOut,
        IReadOnlyList<string> Conflicts)
    {
        /// <summary>
        /// How much it would write, and the room the drive it goes to has, where that is less.
        /// </summary>
        public Room? TooLittleRoom { get; init; }

        /// <summary>
        /// Why the archive can't be read, where it can't: a .tar.gz that is no tar, say.
        /// </summary>
        public string? Unreadable { get; init; }
    }

    /// <summary>
    /// How much extracting a zip would write, and how much room there is for it.
    /// </summary>
    public sealed record Room(long Needed, long Free);

    /// <summary>
    /// An entry of a zip that extracting it left out, by its name in the zip, and why.
    /// </summary>
    public sealed record LeftOutEntry(string Name, LeftOutReason Reason);

    /// <summary>
    /// Why an entry of a zip is left out of what is extracted from it.
    /// </summary>
    public enum LeftOutReason
    {
        /// <summary>
        /// Named to go outside where it is extracted to, or with a name Windows can't have.
        /// </summary>
        Unsafe,

        /// <summary>
        /// Named, before its first dot, as a device Windows keeps the name of, aux.c say, which
        /// would open the device rather than a file.
        /// </summary>
        Device,

        /// <summary>
        /// Named as another entry is but for case, which Windows takes for the same name: the
        /// second of two files, or a file where the zip has a folder.
        /// </summary>
        Duplicate,

        /// <summary>
        /// A link in a tar, symbolic or hard, which is neither followed nor made: it could lead
        /// outside where it is extracted to, or have what is written through it go there.
        /// </summary>
        Link,

        /// <summary>
        /// Something in a tar that is neither a file nor a folder, a device or a pipe, which
        /// Windows has nothing to make of.
        /// </summary>
        Special
    }

    /// <summary>
    /// Extracts each zip, one after another, in the Actions panel: to a new folder beside it
    /// named for it, or, with <paramref name="here"/>, into the folder it is in, the shell asking,
    /// in a dialog <paramref name="owner"/> owns, what to do with each file there already.
    /// Returns what each put at the top of where it went, for selecting, those that failed or were
    /// cancelled having left nothing.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExtractInBackground(IReadOnlyList<string> zips, bool here, IntPtr owner = default) =>
        ExtractEach(zips, null, here, owner);

    /// <summary>
    /// Extracts each zip, one after another, in the Actions panel, into <paramref name="folder"/>,
    /// as Extract here does into the folder each is in: Extract to, the folder picked.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExtractToInBackground(IReadOnlyList<string> zips, string folder, IntPtr owner = default) =>
        ExtractEach(zips, folder, true, owner);

    // Into the folder given, or, with none, the folder each is in.
    static async Task<IReadOnlyList<string>> ExtractEach(IReadOnlyList<string> zips, string? into, bool here, IntPtr owner)
    {
        // In turn rather than all at once. Zips extracted together often share a folder, as the
        // parts of a Google Takeout all hold Takeout, and each looks for what is in its way
        // before it writes anything, which another writing meanwhile would make wrong.
        var placed = new List<string>();
        foreach (var zip in zips)
        {
            var folder = into ?? Path.GetDirectoryName(zip)!;
            IReadOnlyList<string> topLevel = [];
            await ActionLog.Run(
                "Extract",
                [zip],
                (progress, cancel) => Task.Run(
                    () =>
                    {
                        var (outcome, extracted) = ExtractTo(zip, folder, here, progress, cancel, owner);
                        topLevel = extracted;
                        return outcome;
                    },
                    cancel));
            placed.AddRange(topLevel);
        }

        // A folder they share, merged into by each, once.
        return placed
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extracts <paramref name="zip"/>: to a new folder in <paramref name="folder"/> named for it,
    /// or, with <paramref name="here"/>, into <paramref name="folder"/> itself, through the
    /// shell's move where anything is there already, which asks, in a dialog
    /// <paramref name="owner"/> owns, what to do with each file. Returns how it ended, and what
    /// it put at the top of where it went: the new folder, or, extracted here, what the zip has
    /// at its top.
    /// </summary>
    public static (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) ExtractTo(string zip, string folder, bool here, IProgress<string>? progress, Cancel cancel, IntPtr owner = default)
    {
        var name = Path.GetFileName(zip);
        if (here)
        {
            var extraction = Extract(zip, folder, progress, cancel);
            if (extraction.Unreadable is { } unreadable)
            {
                return (UnreadableOutcome(unreadable), []);
            }

            if (extraction.Conflicts.Count > 0)
            {
                return ExtractThroughTheShell(zip, folder, progress, cancel, owner);
            }

            if (extraction.TooLittleRoom is { } room)
            {
                return (RoomOutcome(name, room), []);
            }

            UndoHistory.Instance.RecordCreated("extract", extraction.Created);
            return (ExtractedOutcome(zip, folder, extraction), extraction.TopLevel);
        }

        // Named as FreePath names one, but each name made only where nothing is, and the next
        // tried where something is, so a folder another extract made a moment before, of a zip
        // named as this one's would be numbered, is not taken for this one's too.
        var stem = ArchiveStem(zip);
        var destination = Path.Combine(folder, stem);
        for (var counter = 2; !CreateNewFolder(destination); counter++)
        {
            destination = Path.Combine(folder, $"{stem} ({counter})");
        }

        try
        {
            var extraction = Extract(zip, destination, progress, cancel, intoNewFolder: true);
            if (extraction.Unreadable is { } unreadable)
            {
                TakeAway([destination]);
                return (UnreadableOutcome(unreadable), []);
            }

            if (extraction.TooLittleRoom is { } room)
            {
                TakeAway([destination]);
                return (RoomOutcome(name, room), []);
            }

            UndoHistory.Instance.RecordCreated("extract", [destination]);
            return (ExtractedOutcome(zip, destination, extraction), [destination]);
        }
        catch
        {
            // What the extract made in it, it has taken away, so the folder is empty, unless
            // something else has been put there meanwhile, which stays.
            TakeAway([destination]);
            throw;
        }
    }

    // Into a folder holding something of the name of what the archive puts there: written in
    // full to a hidden folder of its own in that folder, then moved into place by the shell,
    // which asks of each file there already whether to replace it, skip it or compare the two,
    // as Explorer's extract does, and merges a folder into one there. In the folder it goes to,
    // rather than the temp folder, so the move is a rename, however much there is, the room for
    // it is asked of the drive it goes to, and what is moved has the permissions of where it
    // goes, which those of the temp folder, moved with it, would not be. What the move leaves
    // there, skipped, goes with the folder. Undo takes away what was moved, and only that.
    static (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) ExtractThroughTheShell(string zip, string folder, IProgress<string>? progress, Cancel cancel, IntPtr owner)
    {
        var name = Path.GetFileName(zip);
        var staging = Path.Combine(folder, $".Lode extracting {Guid.NewGuid():N}");
        CreateNewFolder(staging);
        try
        {
            File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
            var extraction = Extract(zip, staging, progress, cancel);
            if (extraction.Unreadable is { } unreadable)
            {
                return (UnreadableOutcome(unreadable), []);
            }

            if (extraction.TooLittleRoom is { } room)
            {
                return (RoomOutcome(name, room), []);
            }

            // Once the shell has begun, its own dialog is what stops it.
            cancel.ThrowIfCancellationRequested();
            var outcome = MoveIntoPlace(owner, extraction.TopLevel, folder);
            var moved = outcome.Changes
                .Where(_ => _.Result != null)
                .Select(_ => _.Result!)
                .ToList();
            UndoHistory.Instance.RecordCreated("extract", moved);

            // What is at the top for it: each item moved there, or merged into, once.
            var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topLevel = new List<string>();
            foreach (var path in moved.Concat(outcome.Merged.Where(_ => _.Result != null).Select(_ => _.Result!)))
            {
                var top = TopOf(path, folder);
                if (tops.Add(top))
                {
                    topLevel.Add(top);
                }
            }

            var skipped = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(_ => Path.GetRelativePath(staging, _).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            return (ExtractedOutcome(zip, folder, extraction, skipped, moved.Count > 0), topLevel);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Moves <paramref name="sources"/>, what an extract wrote beside where it goes, into
    /// <paramref name="destination"/> with the shell's IFileOperation, which asks, in a dialog
    /// owned by the window given, whether to replace, skip or compare each file there already,
    /// and merges a folder into one there. On a thread of its own, as the shell needs. Tests stand
    /// in for it, to answer without a dialog.
    /// </summary>
    internal static Func<IntPtr, IReadOnlyList<string>, string, ShellFileOperation.Outcome> MoveIntoPlace { get; set; } =
        (owner, sources, destination) =>
            FileOperations.OnStaThread(() => ShellFileOperation.Run(owner, ShellFileOperation.Kind.Move, sources, destination, ShellFileOperation.AllowUndo))
                .GetAwaiter()
                .GetResult();

    static ActionLog.Outcome RoomOutcome(string name, Room room)
    {
        var needed = Formatting.Size(room.Needed);
        var free = Formatting.Size(room.Free);
        return new(false, $"Not enough room: needs {needed}, {free} free", $"Nothing was extracted from {name}: it needs {needed}, and the drive it goes to has {free} free.");
    }

    // Nothing written, as the archive can't be read, what was read of it saying why.
    static ActionLog.Outcome UnreadableOutcome(string reason) =>
        new(false, "Can't be read", $"Nothing was extracted: {reason}");

    // What was extracted, and left out, and why. Moved into place by the shell, what it skipped,
    // as there was something of its name there already that was kept, or the move was cancelled,
    // and whether it moved anything at all.
    static ActionLog.Outcome ExtractedOutcome(string archive, string destination, Extraction extraction, IReadOnlyList<string>? skipped = null, bool anyMoved = true)
    {
        var name = Path.GetFileName(archive);
        var details = $"Extracted {name} to {destination}";
        foreach (var reason in extraction.LeftOut.GroupBy(_ => _.Reason).OrderBy(_ => _.Key))
        {
            details += $"{Environment.NewLine}{Environment.NewLine}Left out, {Saying(reason.Key, Noun(archive))}:{Environment.NewLine}{string.Join(Environment.NewLine, reason.Select(_ => _.Name))}";
        }

        skipped ??= [];
        if (skipped.Count > 0)
        {
            details += $"{Environment.NewLine}{Environment.NewLine}Skipped, something of its name being there already, or the move having been cancelled:{Environment.NewLine}{string.Join(Environment.NewLine, skipped)}";
        }

        var summary = $"Extracted {name}";
        if (!anyMoved)
        {
            summary = $"Nothing extracted from {name}";
        }

        if (extraction.LeftOut.Count > 0)
        {
            summary += $", leaving out {Count(extraction.LeftOut.Count, "item")}";
        }

        if (skipped.Count > 0)
        {
            summary += $", skipping {Count(skipped.Count, "file")}";
        }

        return new(true, summary, details);
    }

    // Why entries were left out, as the details of an extract say it, of each of them, of the
    // kind of archive, noun, they were in.
    static string Saying(LeftOutReason reason, string noun) =>
        reason switch
        {
            LeftOutReason.Device => "with names Windows keeps for devices, as CON, AUX and NUL",
            LeftOutReason.Duplicate => $"being named as another item in the {noun} is, but for case, which Windows takes for the same name",
            LeftOutReason.Link => "being links, which are neither followed nor made, as one could lead outside the folder",
            LeftOutReason.Special => "being neither files nor folders, as a device or a pipe is",
            _ => "being named to go outside the folder or with names Windows can't have"
        };

    /// <summary>
    /// Writes what <paramref name="zip"/>, a zip or a tar, holds into
    /// <paramref name="destination"/>, which is there, each item with the time it was last
    /// changed, and each file with the archive's mark of the web, should it have one. Leaves out
    /// an entry named to go outside it, with a name Windows can't have, or keeps for a device,
    /// or named as another is but for case, which Windows takes for the same name, and a tar's
    /// links, and what else in one is neither file nor folder. When anything it would write is
    /// there already, the drive hasn't the room for all of it, or the archive can't be read,
    /// writes nothing and returns that. Says how far it has got, in files and bytes. When it
    /// fails, or <paramref name="cancel"/> stops it, which it heeds part way through a file,
    /// deletes what it made, and only that.
    /// </summary>
    public static Extraction Extract(string zip, string destination, IProgress<string>? progress, Cancel cancel) =>
        Extract(zip, destination, progress, cancel, intoNewFolder: false);

    // As Extract. Into a new folder, made for it, of which nothing but the folder is used, it
    // says neither what it put at the top nor what it made, which would be worked out for
    // nothing.
    static Extraction Extract(string zip, string destination, IProgress<string>? progress, Cancel cancel, bool intoNewFolder)
    {
        var mark = MarkOfTheWeb(zip);
        using var reader = Reader.Open(zip);
        var named = new List<Target>();
        var leftOut = new List<LeftOutEntry>();

        // The folders it would make there, or find: those the zip has entries of their own for,
        // and those only there in the names of what is in them. A folder's being in it says
        // those it is in are too.
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read through before anything is written: a tar is read as it is gone through, so
        // whether it is one, and all of it there, is only known at its end.
        try
        {
            foreach (var (index, entry) in reader.Entries().Index())
            {
                if (entry.Kind == EntryKind.Link)
                {
                    leftOut.Add(new(entry.Name, LeftOutReason.Link));
                    continue;
                }

                if (entry.Kind == EntryKind.Special)
                {
                    leftOut.Add(new(entry.Name, LeftOutReason.Special));
                    continue;
                }

                if (Segments(entry.Name, out var reason) is not { } segments)
                {
                    leftOut.Add(new(entry.Name, reason));
                    continue;
                }

                if (segments.Length == 0)
                {
                    continue;
                }

                var path = Path.Join([destination, .. segments]);
                if (!Locations.IsWithin(path, destination) ||
                    Locations.Same(path, destination))
                {
                    leftOut.Add(new(entry.Name, LeftOutReason.Unsafe));
                    continue;
                }

                named.Add(new(entry, index, path));
                var folder = entry.IsFolder ? path : Path.GetDirectoryName(path);
                while (folder != null &&
                       !Locations.Same(folder, destination) &&
                       folders.Add(folder))
                {
                    folder = Path.GetDirectoryName(folder);
                }
            }
        }
        catch (InvalidDataException exception)
        {
            return new([], [], leftOut, [])
            {
                Unreadable = exception.Message
            };
        }

        // Entries that are one item here, as a zip made where case tells names apart can hold:
        // two files, Makefile and makefile say, of which the first is extracted, or a file and a
        // folder, README and readme/notes.txt, of which the folder is, the file before it or
        // after. Each is the one shown when the zip is gone into, and the other is left out,
        // rather than writing it failing on the first, and all that was written being taken away.
        var targets = new List<Target>(named.Count);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in named)
        {
            if (!target.Entry.IsFolder &&
                (folders.Contains(target.Path) ||
                 !files.Add(target.Path)))
            {
                leftOut.Add(new(target.Entry.Name, LeftOutReason.Duplicate));
                continue;
            }

            targets.Add(target);
        }

        var conflicts = targets
            .Where(_ => _.Entry.IsFolder ? File.Exists(_.Path) : Path.Exists(_.Path))
            .Select(_ => _.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (conflicts.Count > 0)
        {
            return new([], [], leftOut, conflicts);
        }

        // What it would write, against the room the drive it goes to has. A zip can hold far
        // more than it takes, a few megabytes that fill the disk, which, written till it was
        // full, failed then, and took all it had written away again.
        var total = 0L;
        foreach (var target in targets)
        {
            if (!target.Entry.IsFolder)
            {
                total += target.Entry.Length;
            }
        }

        if (FreeSpace(destination) is { } free &&
            total > free)
        {
            return new([], [], leftOut, [])
            {
                TooLittleRoom = new(total, free)
            };
        }

        // The top of each, beneath the destination, what was there and what is new, each once,
        // as the zip first names it. Those had are a set, as looking along the list for each, in
        // a zip of thousands at its top, a folder of photos say, took minutes.
        var topLevel = new List<string>();
        if (!intoNewFolder)
        {
            var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                var top = TopOf(target.Path, destination);
                if (tops.Add(top))
                {
                    topLevel.Add(top);
                }
            }
        }

        // What it made, each folder and file as it was made, and only those, rather than what
        // was not there before it started: another extract may make the same folder meanwhile,
        // and what it puts in it is not this one's to delete when this one fails, nor to undo.
        var made = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            destination,
            Path.TrimEndingDirectorySeparator(destination)
        };
        var reporter = new Reporter(progress, "Extracting", files.Count, total);
        Action<long> copied = reporter.Copied;
        try
        {
            // Through the archive again, in its order, which the targets are in, each written as
            // it is come to: what a tar's entry holds is there to be read only then.
            var next = 0;
            foreach (var (index, entry) in reader.Entries().Index())
            {
                if (next == targets.Count)
                {
                    break;
                }

                var (planned, plannedIndex, path) = targets[next];
                if (index != plannedIndex)
                {
                    continue;
                }

                next++;
                cancel.ThrowIfCancellationRequested();
                if (!string.Equals(entry.Name, planned.Name, StringComparison.Ordinal))
                {
                    throw Changed(zip);
                }

                if (entry.IsFolder)
                {
                    MakeFolder(path, known, made);
                    continue;
                }

                reporter.Next();
                MakeFolder(Path.GetDirectoryName(path)!, known, made);
                ExtractFile(entry, path, mark, made, copied, cancel);
            }

            if (next < targets.Count)
            {
                throw Changed(zip);
            }
        }
        catch
        {
            TakeAway(made);
            throw;
        }

        if (intoNewFolder)
        {
            return new([], [], leftOut, []);
        }

        // For undo, the highest of what it made: a folder it made, with all it made in it, or
        // what it made in a folder that was there.
        var madeSet = new HashSet<string>(made, StringComparer.OrdinalIgnoreCase);
        var created = made
            .Where(_ => !madeSet.Contains(Path.GetDirectoryName(_)!))
            .ToList();
        return new(topLevel, created, leftOut, []);
    }

    // An entry to be extracted: the entry, as first read, how far along the archive it is, and
    // where it goes.
    readonly record struct Target(ArchiveEntry Entry, int Index, string Path);

    // Read through a second time, to be written, the archive is not as it was the first: it was
    // written to meanwhile.
    static IOException Changed(string archive) =>
        new($"{Path.GetFileName(archive)} changed while it was extracted.");

    /// <summary>
    /// How much the drive <paramref name="folder"/> is on has room for, for this user, or null
    /// when it can't say. Tests stand in for a drive short of room.
    /// </summary>
    internal static Func<string, long?> FreeSpace { get; set; } = RoomOn;

    static long? RoomOn(string folder)
    {
        var path = Extended(folder);
        if (!Path.EndsInDirectorySeparator(path))
        {
            path += Path.DirectorySeparatorChar;
        }

        if (GetDiskFreeSpaceEx(path, out var freeToCaller, out _, out _))
        {
            return (long) Math.Min(freeToCaller, long.MaxValue);
        }

        return null;
    }

    // Makes folder, and those it is in that are not there, adding each it made to made. The
    // folders known to be there, found or made, are in known, which the destination starts.
    static void MakeFolder(string folder, HashSet<string> known, List<string> made)
    {
        if (known.Contains(folder))
        {
            return;
        }

        if (Path.GetDirectoryName(folder) is { } parent &&
            !known.Contains(parent))
        {
            if (Directory.Exists(parent))
            {
                known.Add(parent);
            }
            else
            {
                MakeFolder(parent, known, made);
            }
        }

        if (CreateNewFolder(folder))
        {
            made.Add(folder);
        }
        else if (!Directory.Exists(folder))
        {
            throw new IOException($"Cannot create '{folder}' because a file with the same name already exists.");
        }

        known.Add(folder);
    }

    /// <summary>
    /// Makes <paramref name="folder"/>, in a folder that is there, only where nothing is: true
    /// when it did, false when something is there already, which Directory.CreateDirectory
    /// would take as done. Past 260 characters whatever the machine allows, as .NET's own calls
    /// are.
    /// </summary>
    static bool CreateNewFolder(string folder)
    {
        if (CreateDirectory(Extended(folder), IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorAlreadyExists)
        {
            return false;
        }

        var reason = new Win32Exception(error);
        throw new IOException($"Cannot create '{folder}': {reason.Message}", reason);
    }

    // The path as Windows takes one of any length: \\?\C:\..., or \\?\UNC\server\share\... for
    // a share.
    static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return full;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return $@"\\?\UNC\{full[2..]}";
        }

        return $@"\\?\{full}";
    }

    // Takes away what an extract made, the last made first, so what is in a folder goes before
    // it: a file, and a folder once it is empty. What else has come to be in one meanwhile, and
    // the folder with it, stays.
    static void TakeAway(List<string> made)
    {
        for (var index = made.Count - 1; index >= 0; index--)
        {
            var path = made[index];
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                    continue;
                }

                File.Delete(path);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
            }
        }
    }

    // The stream Windows writes beside a file a browser downloads, saying where it came from:
    // the mark of the web.
    const string zoneIdentifier = ":Zone.Identifier";

    // The internet's zone. Above it is the one for sites set as restricted.
    const int internetZone = 3;

    /// <summary>
    /// The mark of the web <paramref name="zip"/> has, as it is written, when it says the zip
    /// came from the internet or a restricted site. Explorer puts the zip's mark on all it
    /// extracts from it, which has Office open a document in Protected View, and SmartScreen
    /// check a program before it runs, so Lode does too. Null when the zip has no mark, or one
    /// of a zone nothing is checked for, the machine's own or a trusted site's.
    /// </summary>
    static byte[]? MarkOfTheWeb(string zip)
    {
        byte[] mark;
        try
        {
            mark = File.ReadAllBytes(zip + zoneIdentifier);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(mark), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            var equals = line.IndexOf('=');
            if (equals > 0 &&
                line[..equals].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(equals + 1)..].Trim(), CultureInfo.InvariantCulture, out var zone) &&
                zone >= internetZone)
            {
                return mark;
            }
        }

        return null;
    }

    // Writes an entry's file, as ExtractToFile does, only where nothing is, adding it to made, as
    // soon as it is there, so one that fails part written is taken away with the rest. Then the
    // zip's mark, should it have one, and the time the entry was last changed, which writing the
    // mark would change. A drive that can't hold the mark, FAT or exFAT say, has the file without
    // it, as Explorer leaves it there, rather than the extract failing. Told of what it writes as
    // it goes, a piece at a time, and stopped by cancel between pieces.
    static void ExtractFile(ArchiveEntry entry, string path, byte[]? mark, List<string>? made, Action<long>? copied = null, Cancel cancel = default)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            made?.Add(path);
            entry.CopyTo(file, copied, cancel);
        }

        if (mark != null)
        {
            try
            {
                File.WriteAllBytes(path + zoneIdentifier, mark);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
            }
        }

        // As ExtractToFile, which goes on without it where the time can't be given. A zip's time
        // is the clock's here, which its offset is, as ExtractToFile takes it.
        try
        {
            File.SetLastWriteTimeUtc(path, entry.Modified.UtcDateTime);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentOutOfRangeException)
        {
        }
    }

    static string TopOf(string path, string destination)
    {
        var top = path;
        for (var parent = Path.GetDirectoryName(top);
             parent != null &&
             !Locations.Same(parent, destination);
             parent = Path.GetDirectoryName(parent))
        {
            top = parent;
        }

        return top;
    }

    static bool IsFolderEntry(string name) =>
        name.EndsWith('/') || name.EndsWith('\\');

    static char[] invalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// The folders and name of an entry, by its name in the zip, which may use either slash. Null
    /// for a name that would go outside where it is extracted to, or that Windows can't have:
    /// rooted, with a drive, "." or "..", a character no file name can have, ending in a dot or
    /// a space, which Windows drops, or named as a device is (see <see cref="IsDevice"/>). Empty
    /// for the zip's own top.
    /// </summary>
    public static string[]? Segments(string name) =>
        Segments(name, out _);

    // As Segments, saying why a name it refuses is refused: one that goes outside, or that
    // Windows can't have at all, before one named as a device.
    static string[]? Segments(string name, out LeftOutReason reason)
    {
        reason = LeftOutReason.Unsafe;
        if (name.StartsWith('/') ||
            name.StartsWith('\\') ||
            name.Contains(':'))
        {
            return null;
        }

        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var device = false;
        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.IndexOfAny(invalidNameChars) >= 0 ||
                segment.EndsWith('.') ||
                segment.EndsWith(' '))
            {
                return null;
            }

            device |= IsDevice(segment);
        }

        if (device)
        {
            reason = LeftOutReason.Device;
            return null;
        }

        return segments;
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is named as a device Windows keeps the name of in every
    /// folder: CON, PRN, AUX, NUL, or COM or LPT with a digit, as itself or before its first dot,
    /// any spaces before the dot dropped, in any case. Windows opens the device for such a name,
    /// aux.c or con.h as C sources made on Linux have them say, rather than a file. The digit may
    /// be ¹, ² or ³ too, which Windows takes for digits there.
    /// </summary>
    static bool IsDevice(string segment)
    {
        var dot = segment.IndexOf('.');
        if (dot < 0)
        {
            dot = segment.Length;
        }

        var stem = segment.AsSpan(0, dot).TrimEnd(' ');
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (char.IsAsciiDigit(stem[3]) ||
                stem[3] is '¹' or '²' or '³');
    }

    static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return;
            }

            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Somewhere inside a zip: the zip, and the path in it, empty at its top.
    /// </summary>
    public readonly record struct Location(string Archive, string Inner);

    public enum Kind
    {
        Folder,
        File,
        Missing
    }

    /// <summary>
    /// Where <paramref name="path"/> is in an archive, when it is the archive itself or
    /// somewhere inside one: the first part of it named as one, *.zip, *.tar, *.tar.gz or *.tgz,
    /// that is a file. Null for anywhere else, a folder named *.zip included. Only those parts
    /// are asked after, so most paths cost nothing.
    /// </summary>
    public static Location? Split(string path)
    {
        for (var start = 0; start <= path.Length;)
        {
            var found = path.AsSpan(start).IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var end = path.Length;
            if (found >= 0)
            {
                end = start + found;
            }

            if (IsArchive(path.AsSpan(0, end)))
            {
                var archive = path[..end];
                if (File.Exists(archive))
                {
                    return new(archive, path[end..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }

            start = end + 1;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside a zip, or is the zip itself. See <see cref="Split"/>.
    /// </summary>
    public static bool IsArchivePath(string path) =>
        Split(path) != null;

    /// <summary>
    /// An item in a zip, as a folder lists it.
    /// </summary>
    public sealed record Item(string Name, bool IsFolder, long Size, DateTime Modified);

    // A zip as read: what each folder in it holds, by its path in the zip, the top being empty.
    // Kept for the zip as it was written then, so going from folder to folder in it reads it only
    // once, and the file is not held open in between.
    sealed record Listing(DateTime Written, long Length, Dictionary<string, Dictionary<string, Item>> Folders);

    static ConcurrentDictionary<string, Listing> listings = new(StringComparer.OrdinalIgnoreCase);
    const int listingsKept = 16;

    /// <summary>
    /// What each folder in <paramref name="archive"/> holds, by its path in it, the top being
    /// empty, each item by its name. Folders the zip has no entries of their own for are there
    /// from the names of what is in them. Names are told apart as Windows tells them apart,
    /// ignoring case: the first of those differing only so is kept, and a folder wins over a
    /// file. An entry with a name <see cref="Segments"/> refuses is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, Dictionary<string, Item>> Read(string archive)
    {
        var info = new FileInfo(archive);
        var written = info.LastWriteTimeUtc;
        var length = info.Length;
        if (listings.TryGetValue(archive, out var listing) &&
            listing.Written == written &&
            listing.Length == length)
        {
            return listing.Folders;
        }

        var folders = Build(archive);
        if (listings.Count >= listingsKept)
        {
            listings.Clear();
        }

        listings[archive] = new(written, length, folders);
        return folders;
    }

    static Dictionary<string, Dictionary<string, Item>> Build(string path)
    {
        var folders = new Dictionary<string, Dictionary<string, Item>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new(StringComparer.OrdinalIgnoreCase)
        };
        using var reader = Reader.Open(path);
        foreach (var entry in reader.Entries())
        {
            if (entry.IsLeftOut ||
                Segments(entry.Name) is not { Length: > 0 } segments)
            {
                continue;
            }

            var isFolder = entry.IsFolder;
            var modified = entry.Modified.LocalDateTime;
            var folderCount = segments.Length;
            if (!isFolder)
            {
                folderCount--;
            }

            var parent = "";
            for (var index = 0; index < folderCount; index++)
            {
                var name = segments[index];
                var items = folders[parent];
                var ownEntry = isFolder &&
                               index == folderCount - 1;
                if (items.TryGetValue(name, out var existing) &&
                    existing.IsFolder)
                {
                    // Its own entry gives a folder its time, otherwise the first entry in it does.
                    if (ownEntry)
                    {
                        items[existing.Name] = existing with { Modified = modified };
                    }

                    name = existing.Name;
                }
                else
                {
                    // A folder wins over a file of the same name, keeping the file's spelling.
                    name = existing?.Name ?? name;
                    items[name] = new(name, true, 0, modified);
                }

                var inner = Path.Join(parent, name);
                folders.TryAdd(inner, new(StringComparer.OrdinalIgnoreCase));
                parent = inner;
            }

            if (isFolder)
            {
                continue;
            }

            var fileName = segments[^1];
            folders[parent].TryAdd(fileName, new(fileName, false, entry.Length, modified));
        }

        return folders;
    }

    /// <summary>
    /// Whether <paramref name="location"/> is a folder in its zip, a file, or not there. The top
    /// of a zip is a folder, the zip unread. A zip that can't be read holds nothing.
    /// </summary>
    public static Kind Find(Location location)
    {
        if (location.Inner.Length == 0)
        {
            return Kind.Folder;
        }

        IReadOnlyDictionary<string, Dictionary<string, Item>> folders;
        try
        {
            folders = Read(location.Archive);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    InvalidDataException or
                    UnauthorizedAccessException)
        {
            return Kind.Missing;
        }

        if (folders.ContainsKey(location.Inner))
        {
            return Kind.Folder;
        }

        var parent = Path.GetDirectoryName(location.Inner) ?? "";
        if (folders.TryGetValue(parent, out var items) &&
            items.TryGetValue(Path.GetFileName(location.Inner), out var item) &&
            !item.IsFolder)
        {
            return Kind.File;
        }

        return Kind.Missing;
    }

    /// <summary>
    /// What the folder at <paramref name="location"/> holds, as the list shows it, each at its
    /// path under <paramref name="path"/>, where the view is. Throws
    /// <see cref="DirectoryNotFoundException"/> when the zip has no such folder, as listing a
    /// folder that has gone does.
    /// </summary>
    public static List<FileEntry> List(Location location, string path)
    {
        var folders = Read(location.Archive);
        if (!folders.TryGetValue(location.Inner, out var items))
        {
            throw new DirectoryNotFoundException($"{Path.GetFileName(location.Archive)} has no folder '{location.Inner}'.");
        }

        var result = new List<FileEntry>(items.Count);
        foreach (var item in items.Values)
        {
            var attributes = FileAttributes.Normal;
            if (item.IsFolder)
            {
                attributes = FileAttributes.Directory;
            }

            result.Add(new(item.Name, Path.Join(path, item.Name), item.IsFolder, item.Size, item.Modified, attributes, InArchive: true));
        }

        return result;
    }

    /// <summary>
    /// Where what is extracted from zips to be opened, copied or dragged goes: a folder of
    /// Lode's own in the temp folder.
    /// </summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "Lode", "Zip");

    /// <summary>
    /// Extracts each of <paramref name="paths"/>, items in a zip, a folder with all it holds, to
    /// the temp folder, under its own name, each file with the zip's mark of the web, should it
    /// have one. Returns where each is there. Each goes in a folder of its own for the zip as it
    /// is now and the item, so extracting it again goes to the same place, written again unless
    /// what is there is in use, an editor having it open say, when that is kept.
    /// </summary>
    public static IReadOnlyList<string> ExtractToTemp(IReadOnlyList<string> paths) =>
        ExtractToTemp(paths, null, Cancel.None);

    /// <summary>
    /// <see cref="ExtractToTemp(IReadOnlyList{string})"/>, saying how far it has got, as a
    /// percentage of what there is to write, and stopped by <paramref name="cancel"/>. Each item is
    /// written beside where it goes, and put there once it is all written, so one stopped part
    /// way leaves nothing of itself behind, and what an earlier extract left there, still on the
    /// clipboard say, is as it was.
    /// </summary>
    public static IReadOnlyList<string> ExtractToTemp(IReadOnlyList<string> paths, IProgress<int>? progress, Cancel cancel)
    {
        var locations = new List<Location>(paths.Count);
        foreach (var path in paths)
        {
            if (Split(path) is not { Inner.Length: > 0 } location)
            {
                throw new FileNotFoundException($"'{path}' is not in an archive.");
            }

            locations.Add(location);
        }

        // How much there is to write, only where anyone is to be told how far it has got.
        var total = 0L;
        if (progress != null)
        {
            foreach (var location in locations)
            {
                using var reader = Reader.Open(location.Archive);
                total += SizeOf(reader, location.Inner);
            }
        }

        var done = 0L;
        var reported = -1;
        void Copied(long bytes)
        {
            done += bytes;
            var percent = total == 0 ? 100 : (int) (done * 100 / total);
            if (percent != reported)
            {
                reported = percent;
                progress?.Report(percent);
            }
        }

        var extracted = new List<string>(paths.Count);
        foreach (var location in locations)
        {
            cancel.ThrowIfCancellationRequested();
            var target = TempTarget(location);
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            var staging = Path.Combine(folder, $".extracting-{Guid.NewGuid():N}");
            try
            {
                var written = Path.Combine(staging, Path.GetFileName(target));
                var mark = MarkOfTheWeb(location.Archive);
                using (var reader = Reader.Open(location.Archive))
                {
                    ExtractItem(reader, location.Inner, written, mark, Copied, cancel);
                }

                if (TryClear(target))
                {
                    if (Directory.Exists(written))
                    {
                        Directory.Move(written, target);
                    }
                    else
                    {
                        File.Move(written, target);
                    }
                }
            }
            finally
            {
                TryDelete(staging);

                // Made for this one, which was stopped before it was there.
                if (Directory.Exists(folder) &&
                    !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    TryDelete(folder);
                }
            }

            extracted.Add(target);
        }

        progress?.Report(100);
        return extracted;
    }

    /// <summary>
    /// Where <see cref="ExtractToTemp(IReadOnlyList{string})"/> puts the item at
    /// <paramref name="location"/>: in a folder of its own for the zip as it is now and the item.
    /// </summary>
    internal static string TempTarget(Location location)
    {
        var written = File.GetLastWriteTimeUtc(location.Archive);
        var key = $"{location.Archive}|{location.Inner}|{written.Ticks}".ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(TempRoot, hash, Path.GetFileName(location.Inner));
    }

    // How much the item at inner, a file or a folder with all beneath it, comes to extracted.
    static long SizeOf(Reader reader, string inner)
    {
        var innerSegments = inner.Split(Path.DirectorySeparatorChar);
        var size = 0L;
        foreach (var entry in reader.Entries())
        {
            if (IsBeneath(entry, innerSegments) is not null &&
                !entry.IsFolder)
            {
                size += entry.Length;
            }
        }

        return size;
    }

    // The segments of the entry's name, where it is the item innerSegments names, or beneath it.
    // Never a tar's link, nor what else in one is neither file nor folder.
    static string[]? IsBeneath(ArchiveEntry entry, string[] innerSegments)
    {
        if (entry.IsLeftOut ||
            Segments(entry.Name) is not { } segments ||
            segments.Length < innerSegments.Length ||
            !innerSegments.AsSpan().SequenceEqual(segments.AsSpan(0, innerSegments.Length), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return segments;
    }

    // Takes away what an earlier extract left at target. False when it is in use, to be kept.
    static bool TryClear(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }

            return true;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Writes the item at inner, a file or a folder with all beneath it, to target, each file with
    // the zip's mark of the web, should it have one: opened from there, or copied or dragged on
    // from there, Windows is as wary of it as of the zip it came out of. Told of what it writes
    // as it goes, and stopped by cancel.
    static void ExtractItem(Reader reader, string inner, string target, byte[]? mark, Action<long> copied, Cancel cancel)
    {
        var innerSegments = inner.Split(Path.DirectorySeparatorChar);
        var any = false;
        foreach (var entry in reader.Entries())
        {
            cancel.ThrowIfCancellationRequested();
            if (IsBeneath(entry, innerSegments) is not { } segments)
            {
                continue;
            }

            var path = Path.Join([target, .. segments[innerSegments.Length..]]);
            if (entry.IsFolder)
            {
                Directory.CreateDirectory(path);
                any = true;
                continue;
            }

            // The first of names differing only in case, as the list shows it.
            if (Path.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ExtractFile(entry, path, mark, null, copied, cancel);
            any = true;
        }

        // A folder only there in the names of what is in it, all of them left out.
        if (!any)
        {
            Directory.CreateDirectory(target);
        }
    }

    /// <summary>
    /// Takes away what was extracted to the temp folder more than a day ago, to be opened,
    /// copied or dragged. What is newer may still be open, or still to be pasted.
    /// </summary>
    public static void ClearOldTemp()
    {
        if (!Directory.Exists(TempRoot))
        {
            return;
        }

        var before = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(TempRoot))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < before)
                {
                    TryClear(folder);
                }
            }
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    static string Count(int count, string noun)
    {
        if (count == 1)
        {
            return $"1 {noun}";
        }

        return $"{count} {noun}s";
    }
}
