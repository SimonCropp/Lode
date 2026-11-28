/// <summary>
/// Zip archives: making one of what is selected, and taking what one holds out of it, as
/// Explorer's Compress to ZIP file and Extract All do, each as an entry in the Actions panel,
/// which shows how far it has got and can stop it. And looking inside one, as a folder that can't
/// be changed: what is opened, copied or dragged out of it is extracted to the temp folder first.
/// </summary>
static class Zip
{
    public const string Extension = ".zip";

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

    public static bool IsZip(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

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
        var items = new List<(string Path, string Name, bool IsFolder)>();

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
            items.Add((source, Untaken(name[..^extension.Length], extension, taken), false));
        }

        var files = items.Count(_ => !_.IsFolder);
        var written = 0;
        try
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var (path, name, isFolder) in items)
            {
                cancel.ThrowIfCancellationRequested();
                if (isFolder)
                {
                    archive.CreateEntry(name + '/');
                    continue;
                }

                written++;
                progress?.Report($"Compressing {written} of {files}");
                archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
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

    // A folder, named in the zip as zips name what is in them, with forward slashes: what it
    // holds, or itself when it holds nothing. Not the zip being made, should it be in there.
    static void AddFolder(string folder, string name, List<(string, string, bool)> items, string zip)
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
                items.Add((file.FullName, $"{name}/{file.Name}", false));
            }
        }

        if (items.Count == before)
        {
            items.Add((folder, name, true));
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
    /// out, and why, and what was already there, which stops it before anything is written.
    /// </summary>
    public sealed record Extraction(
        IReadOnlyList<string> TopLevel,
        IReadOnlyList<string> Created,
        IReadOnlyList<LeftOutEntry> LeftOut,
        IReadOnlyList<string> Conflicts);

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
        Device
    }

    /// <summary>
    /// Extracts each zip, one after another, in the Actions panel: to a new folder beside it
    /// named for it, or, with <paramref name="here"/>, into the folder it is in. Returns what each
    /// put at the top of where it went, for selecting, those that failed or were cancelled having
    /// left nothing.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractInBackground(IReadOnlyList<string> zips, bool here)
    {
        // In turn rather than all at once. Zips extracted together often share a folder, as the
        // parts of a Google Takeout all hold Takeout, and each looks for what is in its way
        // before it writes anything, which another writing meanwhile would make wrong.
        var placed = new List<string>();
        foreach (var zip in zips)
        {
            var folder = Path.GetDirectoryName(zip)!;
            IReadOnlyList<string> topLevel = [];
            await ActionLog.Run(
                "Extract",
                [zip],
                (progress, cancel) => Task.Run(
                    () =>
                    {
                        var (outcome, extracted) = ExtractTo(zip, folder, here, progress, cancel);
                        topLevel = extracted;
                        return outcome;
                    },
                    cancel));
            placed.AddRange(topLevel);
        }

        return placed;
    }

    /// <summary>
    /// Extracts <paramref name="zip"/>: to a new folder in <paramref name="folder"/> named for it,
    /// or, with <paramref name="here"/>, into <paramref name="folder"/> itself. Returns how it
    /// ended, and what it put at the top of where it went: the new folder, or, extracted here,
    /// what the zip has at its top.
    /// </summary>
    public static (ActionLog.Outcome Outcome, IReadOnlyList<string> TopLevel) ExtractTo(string zip, string folder, bool here, IProgress<string>? progress, Cancel cancel)
    {
        var name = Path.GetFileName(zip);
        if (here)
        {
            var extraction = Extract(zip, folder, progress, cancel);
            if (extraction.Conflicts.Count > 0)
            {
                return (ConflictOutcome(name, extraction.Conflicts), []);
            }

            UndoHistory.Instance.RecordCreated("extract", extraction.Created);
            return (ExtractedOutcome(name, folder, extraction), extraction.TopLevel);
        }

        // Named as FreePath names one, but each name made only where nothing is, and the next
        // tried where something is, so a folder another extract made a moment before, of a zip
        // named as this one's would be numbered, is not taken for this one's too.
        var stem = Path.GetFileNameWithoutExtension(zip);
        var destination = Path.Combine(folder, stem);
        for (var counter = 2; !CreateNewFolder(destination); counter++)
        {
            destination = Path.Combine(folder, $"{stem} ({counter})");
        }

        try
        {
            var extraction = Extract(zip, destination, progress, cancel);
            UndoHistory.Instance.RecordCreated("extract", [destination]);
            return (ExtractedOutcome(name, destination, extraction), [destination]);
        }
        catch
        {
            // What the extract made in it, it has taken away, so the folder is empty, unless
            // something else has been put there meanwhile, which stays.
            TakeAway([destination]);
            throw;
        }
    }

    static ActionLog.Outcome ConflictOutcome(string name, IReadOnlyList<string> conflicts)
    {
        var details = $"Nothing was extracted from {name}: these are there already.{Environment.NewLine}{string.Join(Environment.NewLine, conflicts)}";
        return new(false, $"{Count(conflicts.Count, "item")} there already", details);
    }

    static ActionLog.Outcome ExtractedOutcome(string name, string destination, Extraction extraction)
    {
        var details = $"Extracted {name} to {destination}";
        foreach (var reason in extraction.LeftOut.GroupBy(_ => _.Reason).OrderBy(_ => _.Key))
        {
            details += $"{Environment.NewLine}{Environment.NewLine}Left out, {Saying(reason.Key)}:{Environment.NewLine}{string.Join(Environment.NewLine, reason.Select(_ => _.Name))}";
        }

        var summary = $"Extracted {name}";
        if (extraction.LeftOut.Count > 0)
        {
            summary += $", leaving out {Count(extraction.LeftOut.Count, "item")}";
        }

        return new(true, summary, details);
    }

    // Why entries were left out, as the details of an extract say it, of each of them.
    static string Saying(LeftOutReason reason) =>
        reason switch
        {
            LeftOutReason.Device => "with names Windows keeps for devices, as CON, AUX and NUL",
            _ => "being named to go outside the folder or with names Windows can't have"
        };

    /// <summary>
    /// Writes what <paramref name="zip"/> holds into <paramref name="destination"/>, which is
    /// there, each item with the time it was last changed, and each file with the zip's mark of
    /// the web, should it have one. Leaves out an entry named to go outside it, or with a name
    /// Windows can't have. When anything it would write is there already, writes nothing and
    /// returns that. When it fails, or <paramref name="cancel"/> stops it, deletes what it made,
    /// and only that.
    /// </summary>
    public static Extraction Extract(string zip, string destination, IProgress<string>? progress, Cancel cancel)
    {
        var mark = MarkOfTheWeb(zip);
        using var archive = OpenRead(zip);
        var targets = new List<(ZipArchiveEntry Entry, string Path, bool IsFolder)>();
        var leftOut = new List<LeftOutEntry>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (Segments(entry.FullName, out var reason) is not { } segments)
            {
                leftOut.Add(new(entry.FullName, reason));
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
                leftOut.Add(new(entry.FullName, LeftOutReason.Unsafe));
                continue;
            }

            // Two entries for the one file here, as a zip made where case tells names apart
            // can hold, Makefile and makefile say. The first is the one extracted, as it is
            // the one shown when the zip is gone into, rather than the second failing it all.
            var isFolder = IsFolderEntry(entry.FullName);
            if (!isFolder &&
                !files.Add(path))
            {
                leftOut.Add(new(entry.FullName, LeftOutReason.Unsafe));
                continue;
            }

            targets.Add((entry, path, isFolder));
        }

        var conflicts = targets
            .Where(_ => _.IsFolder ? File.Exists(_.Path) : Path.Exists(_.Path))
            .Select(_ => _.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (conflicts.Count > 0)
        {
            return new([], [], leftOut, conflicts);
        }

        // The top of each, beneath the destination: what was there and what is new.
        var topLevel = new List<string>();
        foreach (var (_, path, _) in targets)
        {
            var top = TopOf(path, destination);
            if (!topLevel.Contains(top, StringComparer.OrdinalIgnoreCase))
            {
                topLevel.Add(top);
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
        var written = 0;
        try
        {
            foreach (var (entry, path, isFolder) in targets)
            {
                cancel.ThrowIfCancellationRequested();
                if (isFolder)
                {
                    MakeFolder(path, known, made);
                    continue;
                }

                written++;
                progress?.Report($"Extracting {written} of {files.Count}");
                MakeFolder(Path.GetDirectoryName(path)!, known, made);
                ExtractFile(entry, path, mark, made);
            }
        }
        catch
        {
            TakeAway(made);
            throw;
        }

        // For undo, the highest of what it made: a folder it made, with all it made in it, or
        // what it made in a folder that was there.
        var madeSet = new HashSet<string>(made, StringComparer.OrdinalIgnoreCase);
        var created = made
            .Where(_ => !madeSet.Contains(Path.GetDirectoryName(_)!))
            .ToList();
        return new(topLevel, created, leftOut, []);
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
    static void ExtractFile(ZipArchiveEntry entry, string path, byte[]? mark, List<string>? made, Action<long>? copied = null, Cancel cancel = default)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            made?.Add(path);
            using var content = entry.Open();
            var buffer = new byte[81_920];
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                file.Write(buffer, 0, read);
                copied?.Invoke(read);
            }
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

        // As ExtractToFile, which goes on without it where the time can't be given.
        try
        {
            File.SetLastWriteTime(path, entry.LastWriteTime.DateTime);
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
    /// Where <paramref name="path"/> is in a zip, when it is the zip itself or somewhere inside
    /// one: the first part of it named *.zip that is a file. Null for anywhere else, a folder
    /// named *.zip included. Only those parts are asked after, so most paths cost nothing.
    /// </summary>
    public static Location? Split(string path)
    {
        var start = 0;
        while (true)
        {
            var found = path.IndexOf(Extension, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return null;
            }

            var end = found + Extension.Length;
            start = end;
            if (end < path.Length &&
                path[end] != Path.DirectorySeparatorChar &&
                path[end] != Path.AltDirectorySeparatorChar)
            {
                continue;
            }

            var archive = path[..end];
            if (File.Exists(archive))
            {
                return new(archive, path[end..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
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
        using var archive = OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            if (Segments(entry.FullName) is not { Length: > 0 } segments)
            {
                continue;
            }

            var isFolder = IsFolderEntry(entry.FullName);
            var modified = entry.LastWriteTime.LocalDateTime;
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
                throw new FileNotFoundException($"'{path}' is not in a zip.");
            }

            locations.Add(location);
        }

        // How much there is to write, only where anyone is to be told how far it has got.
        var total = 0L;
        if (progress != null)
        {
            foreach (var location in locations)
            {
                using var archive = OpenRead(location.Archive);
                total += SizeOf(archive, location.Inner);
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
                using (var archive = OpenRead(location.Archive))
                {
                    ExtractItem(archive, location.Inner, written, mark, Copied, cancel);
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
    static long SizeOf(ZipArchive archive, string inner)
    {
        var innerSegments = inner.Split(Path.DirectorySeparatorChar);
        var size = 0L;
        foreach (var entry in archive.Entries)
        {
            if (IsBeneath(entry, innerSegments) is not null &&
                !IsFolderEntry(entry.FullName))
            {
                size += entry.Length;
            }
        }

        return size;
    }

    // The segments of the entry's name, where it is the item innerSegments names, or beneath it.
    static string[]? IsBeneath(ZipArchiveEntry entry, string[] innerSegments)
    {
        if (Segments(entry.FullName) is not { } segments ||
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
    static void ExtractItem(ZipArchive archive, string inner, string target, byte[]? mark, Action<long> copied, Cancel cancel)
    {
        var innerSegments = inner.Split(Path.DirectorySeparatorChar);
        var any = false;
        foreach (var entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (IsBeneath(entry, innerSegments) is not { } segments)
            {
                continue;
            }

            var path = Path.Join([target, .. segments[innerSegments.Length..]]);
            if (IsFolderEntry(entry.FullName))
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
