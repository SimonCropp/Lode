using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Zip archives: making one of what is selected, and taking what one holds out of it, as
/// Explorer's Compress to ZIP file and Extract All do, each as an entry in the Actions panel,
/// which shows how far it has got and can stop it. And looking inside one, as a folder that can't
/// be changed: what is opened, copied or dragged out of it is extracted to the temp folder first.
/// </summary>
static class Zip
{
    public const string Extension = ".zip";

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
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            if (Directory.Exists(source))
            {
                AddFolder(source, name, items, zip);
                continue;
            }

            items.Add((source, name, false));
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

    // A folder, named in the zip as zips name what is in them, with forward slashes: what it
    // holds, or itself when it holds nothing. Not the zip being made, should it be in there.
    static void AddFolder(string folder, string name, List<(string, string, bool)> items, string zip)
    {
        var before = items.Count;
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", options).Order(StringComparer.OrdinalIgnoreCase))
        {
            AddFolder(directory, $"{name}/{Path.GetFileName(directory)}", items, zip);
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", options).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!Locations.Same(file, zip))
            {
                items.Add((file, $"{name}/{Path.GetFileName(file)}", false));
            }
        }

        if (items.Count == before)
        {
            items.Add((folder, name, true));
        }
    }

    /// <summary>
    /// What extracting a zip did: what it made at the top of where it went, what it made there or
    /// beneath, for undo (the highest of each that was not there before), what it left out as
    /// unsafe, and what was already there, which stops it before anything is written.
    /// </summary>
    public sealed record Extraction(
        IReadOnlyList<string> TopLevel,
        IReadOnlyList<string> Created,
        IReadOnlyList<string> LeftOut,
        IReadOnlyList<string> Conflicts);

    /// <summary>
    /// Extracts each zip, in the Actions panel: to a new folder beside it named for it, or, with
    /// <paramref name="here"/>, into the folder it is in. Returns what each put at the top of
    /// where it went, for selecting, those that failed or were cancelled having left nothing.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractInBackground(IReadOnlyList<string> zips, bool here)
    {
        var placed = new List<string>();
        var running = zips.Select(async zip =>
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
            return topLevel;
        });
        foreach (var topLevel in await Task.WhenAll(running))
        {
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

        var destination = FreePath(folder, Path.GetFileNameWithoutExtension(zip), "");
        Directory.CreateDirectory(destination);
        try
        {
            var extraction = Extract(zip, destination, progress, cancel);
            UndoHistory.Instance.RecordCreated("extract", [destination]);
            return (ExtractedOutcome(name, destination, extraction), [destination]);
        }
        catch
        {
            TryDelete(destination);
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
        if (extraction.LeftOut.Count > 0)
        {
            details += $"{Environment.NewLine}{Environment.NewLine}Left out, being named to go outside the folder or with names Windows can't have:{Environment.NewLine}{string.Join(Environment.NewLine, extraction.LeftOut)}";
        }

        var summary = $"Extracted {name}";
        if (extraction.LeftOut.Count > 0)
        {
            summary += $", leaving out {Count(extraction.LeftOut.Count, "item")}";
        }

        return new(true, summary, details);
    }

    /// <summary>
    /// Writes what <paramref name="zip"/> holds into <paramref name="destination"/>, which is
    /// there, each item with the time it was last changed. Leaves out an entry named to go
    /// outside it, or with a name Windows can't have. When anything it would write is there
    /// already, writes nothing and returns that. When it fails, or <paramref name="cancel"/>
    /// stops it, deletes what it made.
    /// </summary>
    public static Extraction Extract(string zip, string destination, IProgress<string>? progress, Cancel cancel)
    {
        using var archive = ZipFile.OpenRead(zip);
        var targets = new List<(ZipArchiveEntry Entry, string Path, bool IsFolder)>();
        var leftOut = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (Segments(entry.FullName) is not { } segments)
            {
                leftOut.Add(entry.FullName);
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
                leftOut.Add(entry.FullName);
                continue;
            }

            targets.Add((entry, path, IsFolderEntry(entry.FullName)));
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
        var created = new List<string>();
        foreach (var (_, path, _) in targets)
        {
            var top = TopOf(path, destination);
            if (!topLevel.Contains(top, StringComparer.OrdinalIgnoreCase))
            {
                topLevel.Add(top);
            }

            var highestNew = HighestNew(path, destination);
            if (highestNew != null &&
                !created.Any(_ => Locations.IsWithin(highestNew, _)))
            {
                created.RemoveAll(_ => Locations.IsWithin(_, highestNew));
                created.Add(highestNew);
            }
        }

        var files = targets.Count(_ => !_.IsFolder);
        var written = 0;
        try
        {
            foreach (var (entry, path, isFolder) in targets)
            {
                cancel.ThrowIfCancellationRequested();
                if (isFolder)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                written++;
                progress?.Report($"Extracting {written} of {files}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
        catch
        {
            foreach (var path in created)
            {
                TryDelete(path);
            }

            throw;
        }

        return new(topLevel, created, leftOut, []);
    }

    // The highest folder or file on the way from the destination down to path that is not there.
    static string? HighestNew(string path, string destination)
    {
        string? highest = null;
        for (var current = path;
             current != null && !Locations.Same(current, destination);
             current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current))
            {
                break;
            }

            highest = current;
        }

        return highest;
    }

    static string TopOf(string path, string destination)
    {
        var top = path;
        for (var parent = Path.GetDirectoryName(top);
             parent != null && !Locations.Same(parent, destination);
             parent = Path.GetDirectoryName(parent))
        {
            top = parent;
        }

        return top;
    }

    static bool IsFolderEntry(string name) =>
        name.EndsWith('/') || name.EndsWith('\\');

    static readonly char[] invalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// The folders and name of an entry, by its name in the zip, which may use either slash. Null
    /// for a name that would go outside where it is extracted to, or that Windows can't have:
    /// rooted, with a drive, "." or "..", a character no file name can have, or ending in a dot
    /// or a space, which Windows drops. Empty for the zip's own top.
    /// </summary>
    public static string[]? Segments(string name)
    {
        if (name.StartsWith('/') ||
            name.StartsWith('\\') ||
            name.Contains(':'))
        {
            return null;
        }

        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.IndexOfAny(invalidNameChars) >= 0 ||
                segment.EndsWith('.') ||
                segment.EndsWith(' '))
            {
                return null;
            }
        }

        return segments;
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

    static readonly ConcurrentDictionary<string, Listing> listings = new(StringComparer.OrdinalIgnoreCase);
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
        using var archive = ZipFile.OpenRead(path);
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
                var ownEntry = isFolder && index == folderCount - 1;
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
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
    /// the temp folder, under its own name. Returns where each is there. Each goes in a folder of
    /// its own for the zip as it is now and the item, so extracting it again goes to the same
    /// place, written again unless what is there is in use, an editor having it open say, when
    /// that is kept.
    /// </summary>
    public static IReadOnlyList<string> ExtractToTemp(IReadOnlyList<string> paths)
    {
        var extracted = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (Split(path) is not { Inner.Length: > 0 } location)
            {
                throw new FileNotFoundException($"'{path}' is not in a zip.");
            }

            var written = File.GetLastWriteTimeUtc(location.Archive);
            var key = $"{location.Archive}|{location.Inner}|{written.Ticks}".ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var folder = Path.Combine(TempRoot, hash);
            var target = Path.Combine(folder, Path.GetFileName(location.Inner));
            if (TryClear(target))
            {
                Directory.CreateDirectory(folder);
                using var archive = ZipFile.OpenRead(location.Archive);
                ExtractItem(archive, location.Inner, target);
            }

            extracted.Add(target);
        }

        return extracted;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Writes the item at inner, a file or a folder with all beneath it, to target.
    static void ExtractItem(ZipArchive archive, string inner, string target)
    {
        var innerSegments = inner.Split(Path.DirectorySeparatorChar);
        var any = false;
        foreach (var entry in archive.Entries)
        {
            if (Segments(entry.FullName) is not { } segments ||
                segments.Length < innerSegments.Length ||
                !innerSegments.AsSpan().SequenceEqual(segments.AsSpan(0, innerSegments.Length), StringComparer.OrdinalIgnoreCase))
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
            entry.ExtractToFile(path);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
