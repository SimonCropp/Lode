/// <summary>
/// What git status --porcelain=v2 --ignore-submodules=dirty lists, read in process: for each
/// path, what is staged, against HEAD's tree, and what isn't, the working tree against the
/// index, then what git doesn't track, a folder of it one path unless status.showUntrackedFiles
/// says all. A folder's files are looked at by one listing of it, which gives each file's size
/// and times, as git for Windows' fscache does; only a file whose times aren't those staged is
/// read, and hashed as git would stage it.
/// </summary>
sealed class WorkingTree
{
    readonly GitRepository repository;
    readonly string workTree;
    readonly IndexFile index;
    readonly ObjectDatabase objects;
    readonly Cancel cancel;
    readonly IgnoreRules ignore;
    readonly Untracked untracked;
    readonly bool checkStat;
    readonly bool trustCtime;
    readonly bool symlinks;
    readonly Dictionary<string, Status> statuses = new(StringComparer.Ordinal);
    readonly List<Renames.File> deleted = [];
    readonly List<Renames.File> added = [];
    readonly List<(int Entry, string Path)> toHash = [];
    readonly List<string> untrackedPaths = [];

    enum Untracked
    {
        No,
        Normal,
        All
    }

    enum RenameDetection
    {
        None,
        Renames,
        Copies
    }

    sealed class Status
    {
        public char Staged = '.';
        public char Unstaged = '.';
        public bool Conflicted;
    }

    readonly record struct DiskEntry(string Name, bool IsFolder, bool IsLink, long Length, long CreationTicks, long WriteTicks);

    WorkingTree(GitRepository repository, string workTree, Cancel cancel)
    {
        this.repository = repository;
        this.workTree = workTree;
        this.cancel = cancel;
        var config = repository.Config;

        // On unless it is set off, as git has it: git for Windows' git init sets it off, as
        // Windows can't say whether a file runs, but a repository made otherwise may not have it
        // set, and there git takes every file staged as one that runs as changed.
        if (config.GetBool("core.filemode", true))
        {
            throw new GitFilesException("core.fileMode needs git");
        }

        index = IndexFile.Read(repository.GitDir);
        objects = repository.Objects;
        ignore = new(repository, workTree);
        checkStat = !string.Equals(config.Get("core.checkstat"), "minimal", StringComparison.OrdinalIgnoreCase);
        trustCtime = config.GetBool("core.trustctime", true);
        // On unless it is set off, as git has it: git for Windows' installer sets it off unless
        // told to make links, and git init does where Windows makes it none, so where Windows
        // does, for an administrator say, a repository can have it on.
        symlinks = config.GetBool("core.symlinks", true);
        untracked = (config.Get("status.showuntrackedfiles") ?? "normal").ToLowerInvariant() switch
        {
            "no" or "false" or "off" or "0" => Untracked.No,
            "all" => Untracked.All,
            _ => Untracked.Normal
        };
    }

    /// <summary>
    /// The paths that have changed, in order of their paths.
    /// </summary>
    public static List<GitFile> Read(GitRepository repository, Cancel cancel) =>
        new WorkingTree(repository, repository.WorkTree, cancel).Read();

    List<GitFile> Read()
    {
        if (repository.Head is { } head)
        {
            CompareStaged(HeadTree(head), index.Tree, 0, index.Entries.Length, []);
        }
        else
        {
            CompareStaged(null, null, 0, index.Entries.Length, []);
        }

        FindRenames();
        ListTracked();
        Scan(workTree, new(null, "", this), 0, index.Entries.Length, 0);
        cancel.ThrowIfCancellationRequested();
        foreach (var (entry, path) in toHash)
        {
            if (!SameContent(entry, path))
            {
                StatusOf(index.PathText(entry)).Unstaged = 'M';
            }
        }

        var files = new List<GitFile>(statuses.Count + untrackedPaths.Count);
        foreach (var (path, status) in statuses)
        {
            // Each is there for a change found, staged or not.
            if (status.Conflicted)
            {
                files.Add(new(path, GitChange.Conflicted));
                continue;
            }

            files.Add(new(path, GitState.ChangeOf(status.Staged, status.Unstaged)));
        }

        foreach (var path in untrackedPaths)
        {
            files.Add(new(path, GitChange.Untracked));
        }

        return GitState.InOrder(files);
    }

    ObjectId HeadTree(ObjectId head)
    {
        if (repository.Graph is { } graph &&
            graph.TryFind(head, out var position))
        {
            return graph.TreeAt(position);
        }

        return new CommitInfo(objects.Read(head, GitObjectType.Commit)).Tree;
    }

    Status StatusOf(string path)
    {
        if (!statuses.TryGetValue(path, out var status))
        {
            status = new();
            statuses[path] = status;
        }

        return status;
    }

    // The name of the part of an entry's path after the folder it is in, and whether that is a
    // folder of its own, with where the entries in that folder end.
    (int NameStart, int NameLength, bool IsFolder, int End) Child(int entry, int prefixLength, int end)
    {
        var path = index.PathOf(entry);
        var rest = path[prefixLength..];
        var slash = rest.IndexOf((byte)'/');
        if (slash < 0)
        {
            // A path staged more than once, in a merge's conflict, is one child.
            var last = entry + 1;
            while (last < end &&
                   index.PathOf(last).SequenceEqual(path))
            {
                last++;
            }

            return (prefixLength, rest.Length, false, last);
        }

        var folder = path[..(prefixLength + slash + 1)];
        var after = entry + 1;
        while (after < end &&
               index.PathOf(after).StartsWith(folder))
        {
            after++;
        }

        return (prefixLength, slash, true, after);
    }

    ByteSpan NameOf(int entry, int start, int length) =>
        index.PathOf(entry).Slice(start, length);

    // HEAD's tree against the index, a folder at a time: none gone into whose tree the cache
    // tree says is the same as HEAD's. The folder's path, with a slash after it, but for the top.
    void CompareStaged(ObjectId? tree, IndexFile.CacheTree? cache, int start, int end, byte[] prefix)
    {
        var prefixLength = prefix.Length;
        if (tree is { } id &&
            cache is { EntryCount: >= 0 } &&
            cache.Id == id &&
            cache.EntryCount == end - start)
        {
            return;
        }

        var treeData = Array.Empty<byte>();
        if (tree is { } treeId)
        {
            treeData = objects.Read(treeId, GitObjectType.Tree);
        }

        var entries = Trees.Read(treeData);
        var i = 0;
        var k = start;
        while (i < entries.Count || k < end)
        {
            int compared;
            var child = (NameStart: 0, NameLength: 0, IsFolder: false, End: 0);
            if (k < end)
            {
                child = Child(k, prefixLength, end);
            }

            if (i >= entries.Count)
            {
                compared = 1;
            }
            else if (k >= end)
            {
                compared = -1;
            }
            else
            {
                compared = Trees.Compare(entries[i].Name(treeData), entries[i].IsFolder, NameOf(k, child.NameStart, child.NameLength), child.IsFolder);
            }

            if (compared < 0)
            {
                DeletedFromIndex(entries[i], treeData, prefix);
                i++;
                continue;
            }

            if (compared > 0)
            {
                for (var entry = k; entry < child.End; entry++)
                {
                    AddedToIndex(entry);
                }

                k = child.End;
                continue;
            }

            var treeEntry = entries[i];
            i++;
            if (child.IsFolder)
            {
                var name = NameOf(k, child.NameStart, child.NameLength);
                CompareStaged(treeEntry.Id, cache?.Child(name), k, child.End, index.PathOf(k)[..(prefixLength + child.NameLength + 1)].ToArray());
                k = child.End;
                continue;
            }

            ChangedInIndex(treeEntry, k, child.End);
            k = child.End;
        }
    }

    // A file HEAD has that the index hasn't, or all of a folder's.
    void DeletedFromIndex(Trees.Entry entry, byte[] treeData, byte[] prefix)
    {
        var name = entry.Name(treeData);
        var path = new byte[prefix.Length + name.Length];
        prefix.CopyTo(path, 0);
        name.CopyTo(path.AsSpan(prefix.Length));
        if (!entry.IsFolder)
        {
            Deleted(path, entry.Mode, entry.Id);
            return;
        }

        foreach (var change in Trees.Diff(objects, entry.Id, null))
        {
            Deleted(Trees.Join(path, change.Path), change.OldMode, change.OldId);
        }
    }

    void Deleted(byte[] path, int mode, ObjectId id)
    {
        StatusOf(Encoding.UTF8.GetString(path)).Staged = 'D';
        deleted.Add(new(path, mode, id));
    }

    // A path the index has that HEAD hasn't: conflicted, or added, unless only intended to be.
    void AddedToIndex(int entry)
    {
        ref var e = ref index.Entries[entry];
        if (e.Stage != 0)
        {
            StatusOf(index.PathText(entry)).Conflicted = true;
            return;
        }

        if ((e.Extended & IndexFile.IntentToAdd) != 0)
        {
            return;
        }

        StatusOf(index.PathText(entry)).Staged = 'A';
        added.Add(new(index.PathOf(entry).ToArray(), Trees.Canonical((int)e.Mode), e.Id));
    }

    // A file in both: changed where its blob or its mode isn't HEAD's, a change of type where it
    // isn't a file where it was, or the other way round.
    void ChangedInIndex(Trees.Entry treeEntry, int start, int end)
    {
        for (var entry = start; entry < end; entry++)
        {
            if (index.Entries[entry].Stage != 0)
            {
                StatusOf(index.PathText(entry)).Conflicted = true;
                return;
            }
        }

        ref var e = ref index.Entries[start];
        if ((e.Extended & IndexFile.IntentToAdd) != 0)
        {
            StatusOf(index.PathText(start)).Staged = 'D';
            return;
        }

        var mode = Trees.Canonical((int)e.Mode);
        if (e.Id == treeEntry.Id &&
            mode == treeEntry.Mode)
        {
            return;
        }

        var change = 'M';
        if ((mode & Trees.FileType) != (treeEntry.Mode & Trees.FileType))
        {
            change = 'T';
        }

        StatusOf(index.PathText(start)).Staged = change;
    }

    // Staged renames, as git status finds them, unless status.renames or diff.renames turns them
    // off. Copies, which git finds with either set to copies, aren't looked for here, so where a
    // file is added, which one could be, the repository is left to git, as it is, added or not,
    // where either is set to what git status refuses, and fails on.
    void FindRenames()
    {
        var config = repository.Config;
        var detection = RenameDetectionOf(config);
        if (added.Count == 0)
        {
            return;
        }

        if (detection == RenameDetection.Copies)
        {
            throw new GitFilesException("Copies need git");
        }

        if (detection == RenameDetection.None ||
            deleted.Count == 0)
        {
            return;
        }

        var limit = config.GetInt("status.renamelimit", config.GetInt("diff.renamelimit", 1000));
        var found = new Renames(objects).Find(deleted, added, limit);
        for (var destination = 0; destination < found.Length; destination++)
        {
            if (found[destination] < 0)
            {
                continue;
            }

            StatusOf(Encoding.UTF8.GetString(added[destination].Path)).Staged = 'R';
            statuses.Remove(Encoding.UTF8.GetString(deleted[found[destination]].Path));
        }
    }

    // As git status reads status.renames and diff.renames, in the order they are set: every
    // status.renames, the last winning, but diff.renames only where neither has been read
    // before it, so only the first, and none after a status.renames. Renames where neither is.
    static RenameDetection RenameDetectionOf(GitConfig config)
    {
        RenameDetection? detection = null;
        foreach (var (key, value) in config.InOrder("status.renames", "diff.renames"))
        {
            if (key == "diff.renames" &&
                detection != null)
            {
                continue;
            }

            detection = RenameDetectionOf(value);
        }

        return detection ?? RenameDetection.Renames;
    }

    // As git_config_rename: copies or copy, in any case, for copies, and set without a value for
    // renames; otherwise a bool, what isn't one failing git status.
    static RenameDetection RenameDetectionOf(string? value)
    {
        if (value == null)
        {
            return RenameDetection.Renames;
        }

        if (value.Equals("copies", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            return RenameDetection.Copies;
        }

        return GitConfig.ParseBool(value) switch
        {
            true => RenameDetection.Renames,
            false => RenameDetection.None,
            null => throw new GitFilesException("A renames setting git refuses")
        };
    }

    // A folder's place in what git ignores, only worked out where something in it, or in a
    // folder in it, isn't tracked.
    sealed class LazyFolder(LazyFolder? parent, string name, WorkingTree tree)
    {
        IgnoreRules.Folder? folder;

        public bool? HasIgnoreFile { get; set; }

        public IgnoreRules.Folder Get()
        {
            if (folder != null)
            {
                return folder;
            }

            if (parent == null)
            {
                folder = tree.ignore.Top(HasIgnoreFile);
                return folder;
            }

            folder = tree.ignore.Enter(parent.Get(), name, HasIgnoreFile);
            return folder;
        }
    }

    // A folder Lode is denied listing throws, as one in use does, rather than listing nothing,
    // which had every file tracked in it deleted: git with core.fscache off opens each file by
    // its path, which an ACL on the folder doesn't stop, and finds them as they were, and only
    // with it on lists the folder, as Lode does, and finds them gone.
    static readonly EnumerationOptions listing = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        BufferSize = 16 * 1024
    };

    // A folder's entries, with what NTFS keeps of each in the folder itself: no file opened. A
    // link is a file, as git for Windows has one, not a folder to go into; a junction, or any
    // other reparse point, is what its attributes say, as git for Windows takes those. None
    // for a folder since gone, listed ahead and deleted before it is looked at; one that can't be
    // listed, in use or denied, is left to git.
    static List<DiskEntry> List(string folder)
    {
        try
        {
            var enumerable = new FileSystemEnumerable<DiskEntry>(
                folder,
                static (ref entry) =>
                {
                    var link = (entry.Attributes & FileAttributes.ReparsePoint) != 0 &&
                               IsSymbolicLink(entry.ToFullPath());
                    return new(
                        entry.FileName.ToString(),
                        entry.IsDirectory && !link,
                        link,
                        entry.Length,
                        entry.CreationTimeUtc.UtcTicks,
                        entry.LastWriteTimeUtc.UtcTicks);
                },
                listing);
            return enumerable.ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    // Whether a reparse point is a symbolic link, by its tag, which only opening it as itself
    // tells.
    static bool IsSymbolicLink(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);
        return !handle.IsInvalid &&
               GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var tag, (uint)Marshal.SizeOf<FileAttributeTag>()) &&
               tag.ReparseTag == ReparseTagSymlink;
    }

    // A listing costs some 35 µs, nearly all of it opening the folder, and a repository of many
    // folders spends most of its read on them. Listed on four threads, OfficeIMO's 2,900 folders
    // take 64 ms rather than 119; more threads gain little, as opening folders doesn't go much
    // faster side by side, and for few folders the threads cost more than they save. A folder of
    // repositories, read six at a time, is a little slower so, 310 ms for 187 rather than 280:
    // the repository looked at is the one waited on.
    const int listedApart = 500;
    const int listingThreads = 4;

    // Each folder the index has files in, listed before they are looked at, where there are
    // enough of them. By its path from the top, as the index has it. One that couldn't be listed
    // ahead isn't here, and is listed when it is looked at, as where there are fewer folders: a
    // file now where it was isn't looked at as a folder, so what was in it is gone, and one that
    // can't be listed then either leaves the repository to git. Failing on the threads, it came
    // out wrapped, which nothing took as a read that couldn't be made here, and the read failed.
    Dictionary<string, List<DiskEntry>>? listed;

    void ListTracked()
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previous = ByteSpan.Empty;
        for (var entry = 0; entry < index.Entries.Length; entry++)
        {
            var path = index.PathOf(entry);
            var slash = path.LastIndexOf((byte)'/');
            if (slash < 0)
            {
                continue;
            }

            var folder = path[..slash];
            if (folder.SequenceEqual(previous))
            {
                continue;
            }

            previous = folder;

            // The folder, and those it is in, each once: once one is known, so are those it is in.
            var text = Encoding.UTF8.GetString(folder);
            var end = text.Length;
            while (end > 0)
            {
                var parent = text[..end];
                if (!seen.Add(parent))
                {
                    break;
                }

                folders.Add(parent);
                end = text.LastIndexOf('/', end - 1);
            }
        }

        if (folders.Count < listedApart)
        {
            return;
        }

        folders.Add("");
        var results = new List<DiskEntry>?[folders.Count];
        Parallel.For(
            0,
            folders.Count,
            new()
            {
                CancellationToken = cancel,
                MaxDegreeOfParallelism = listingThreads
            },
            _ => results[_] = ListAhead(Path.Combine(workTree, folders[_])));
        listed = new(folders.Count, StringComparer.Ordinal);
        for (var at = 0; at < folders.Count; at++)
        {
            if (results[at] is { } entries)
            {
                listed[folders[at]] = entries;
            }
        }
    }

    static List<DiskEntry>? ListAhead(string folder)
    {
        try
        {
            return List(folder);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A folder the index has entries in: each of them against what the listing says of its file,
    // folders in it gone into, then what the listing has that the index hasn't.
    void Scan(string folder, LazyFolder ignoreFolder, int start, int end, int prefixLength)
    {
        cancel.ThrowIfCancellationRequested();
        List<DiskEntry>? disk = null;
        if (listed != null)
        {
            var relative = "";
            if (prefixLength > 0)
            {
                relative = Encoding.UTF8.GetString(index.PathOf(start)[..(prefixLength - 1)]);
            }

            listed.TryGetValue(relative, out disk);
        }

        disk ??= List(folder);
        var byName = new Dictionary<string, int>(disk.Count, StringComparer.OrdinalIgnoreCase);
        for (var at = 0; at < disk.Count; at++)
        {
            byName.TryAdd(disk[at].Name, at);
        }

        ignoreFolder.HasIgnoreFile = byName.ContainsKey(".gitignore");
        var lookup = byName.GetAlternateLookup<CharSpan>();
        var matched = new bool[disk.Count];

        // Folders where the index has a file: what is in one isn't tracked, but git lists it only
        // file by file, as untracked files all would, the folder itself being a path it tracks.
        bool[]? wasFile = null;
        Span<char> name = stackalloc char[256];
        var k = start;
        while (k < end)
        {
            var child = Child(k, prefixLength, end);
            var nameBytes = NameOf(k, child.NameStart, child.NameLength);
            var chars = name;
            if (Encoding.UTF8.GetMaxCharCount(nameBytes.Length) > chars.Length)
            {
                chars = new char[Encoding.UTF8.GetMaxCharCount(nameBytes.Length)];
            }

            var length = Encoding.UTF8.GetChars(nameBytes, chars);
            var found = -1;
            if (lookup.TryGetValue(chars[..length], out var at))
            {
                found = at;
            }

            if (child.IsFolder)
            {
                if (found >= 0 &&
                    disk[found].IsFolder)
                {
                    matched[found] = true;

                    // On disk by the name it has there, in the rules by the index's, as git has it.
                    Scan(Path.Combine(folder, disk[found].Name), new(ignoreFolder, chars[..length].ToString(), this), k, child.End, prefixLength + child.NameLength + 1);
                }
                else
                {
                    for (var entry = k; entry < child.End; entry++)
                    {
                        Gone(entry);
                    }
                }

                k = child.End;
                continue;
            }

            if (found >= 0)
            {
                // Tracked, so not untracked, but for a folder where a file was: git lists the
                // file as gone, and what is in the folder.
                if (!disk[found].IsFolder ||
                    (index.Entries[k].Mode & 0xF000) == Trees.Gitlink)
                {
                    matched[found] = true;
                }
                else
                {
                    wasFile ??= new bool[disk.Count];
                    wasFile[found] = true;
                }
            }

            CompareFile(k, child.End, found, disk, folder);
            k = child.End;
        }

        if (untracked == Untracked.No)
        {
            return;
        }

        for (var at = 0; at < disk.Count; at++)
        {
            if (matched[at])
            {
                continue;
            }

            var entry = disk[at];
            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rules = ignoreFolder.Get();
            if (ignore.IsIgnored(rules, entry.Name, entry.IsFolder))
            {
                continue;
            }

            var path = rules.PathOf(entry.Name);
            if (!entry.IsFolder)
            {
                untrackedPaths.Add(path);
                continue;
            }

            var full = Path.Combine(folder, entry.Name);
            var fileWasHere = wasFile != null && wasFile[at];
            if (CodeCommands.IsRepo(full))
            {
                if (!fileWasHere)
                {
                    untrackedPaths.Add(path);
                }

                continue;
            }

            if (untracked == Untracked.All)
            {
                AllUntracked(full, ignore.Enter(rules, entry.Name));
                continue;
            }

            if (!fileWasHere &&
                HasUntracked(full, ignore.Enter(rules, entry.Name)))
            {
                untrackedPaths.Add(path);
            }
        }
    }

    // A file the index has whose file isn't there, or isn't a file: deleted, unless the index
    // says not to look, or the file was only meant to be added.
    void Gone(int entry)
    {
        ref var e = ref index.Entries[entry];
        if (e.Stage != 0)
        {
            StatusOf(index.PathText(entry)).Conflicted = true;
            return;
        }

        if ((e.Flags & IndexFile.AssumeValid) != 0 ||
            (e.Extended & IndexFile.SkipWorktree) != 0)
        {
            return;
        }

        StatusOf(index.PathText(entry)).Unstaged = 'D';
    }

    // As git's ie_match_stat and ie_modified: unchanged where the listing has the file as it was
    // staged, and the index was written after it was; changed where its size isn't; otherwise
    // hashed to see.
    void CompareFile(int start, int end, int found, List<DiskEntry> disk, string folder)
    {
        for (var entry = start; entry < end; entry++)
        {
            if (index.Entries[entry].Stage != 0)
            {
                StatusOf(index.PathText(entry)).Conflicted = true;
                return;
            }
        }

        ref var e = ref index.Entries[start];
        if ((e.Flags & IndexFile.AssumeValid) != 0 ||
            (e.Extended & IndexFile.SkipWorktree) != 0)
        {
            return;
        }

        if (found < 0)
        {
            Gone(start);
            return;
        }

        var file = disk[found];
        var type = (int)e.Mode & 0xF000;
        if (type == Trees.Gitlink)
        {
            if (!file.IsFolder)
            {
                StatusOf(index.PathText(start)).Unstaged = 'T';
                return;
            }

            if (GitRepository.HeadOf(Path.Combine(folder, file.Name)) is { } submodule &&
                submodule != e.Id)
            {
                StatusOf(index.PathText(start)).Unstaged = 'M';
            }

            return;
        }

        // A folder where a file was is the file gone, as git's check_removed has it, but for a
        // repository with a commit checked out, which is a change of type.
        if (file.IsFolder)
        {
            if (GitRepository.HeadOf(Path.Combine(folder, file.Name)) != null)
            {
                StatusOf(index.PathText(start)).Unstaged = 'T';
                return;
            }

            Gone(start);
            return;
        }

        if ((e.Extended & IndexFile.IntentToAdd) != 0)
        {
            StatusOf(index.PathText(start)).Unstaged = 'A';
            return;
        }

        if (file.IsLink &&
            type != Trees.Link)
        {
            StatusOf(index.PathText(start)).Unstaged = 'T';
            return;
        }

        // A file where a link is staged is a change of type where git makes links, as git's
        // ce_match_stat_basic has it; where it doesn't, the link is checked out as a file
        // holding the name it points at, and compared by that, as any file is.
        if (type == Trees.Link &&
            !file.IsLink &&
            symlinks)
        {
            StatusOf(index.PathText(start)).Unstaged = 'T';
            return;
        }

        // A link where one is staged: git compares the name it holds, its length and then what
        // it says, where the listing gives a link no length, and reading it would read what it
        // points at, so it is left to git.
        if (file.IsLink)
        {
            throw new GitFilesException("A link staged as one needs git");
        }

        var (mtimeSeconds, mtimeNanoseconds) = IndexFile.UnixTime(new(file.WriteTicks, DateTimeKind.Utc));
        var (ctimeSeconds, ctimeNanoseconds) = IndexFile.UnixTime(new(file.CreationTicks, DateTimeKind.Utc));
        var changed = (uint)mtimeSeconds != e.MtimeSeconds;
        var sizeChanged = (uint)file.Length != e.Size;
        if (checkStat)
        {
            changed |= (uint)mtimeNanoseconds != e.MtimeNanoseconds;
            if (trustCtime)
            {
                changed |= (uint)ctimeSeconds != e.CtimeSeconds ||
                           (uint)ctimeNanoseconds != e.CtimeNanoseconds;
            }
        }

        if (sizeChanged &&
            e.Size != 0)
        {
            StatusOf(index.PathText(start)).Unstaged = 'M';
            return;
        }

        // Changed in the same second as the index was written, or after, it may yet look as it
        // did when staged.
        var (indexSeconds, indexNanoseconds) = index.Timestamp;
        var racy = indexSeconds != 0 &&
                   (indexSeconds < e.MtimeSeconds ||
                    (indexSeconds == e.MtimeSeconds && indexNanoseconds <= e.MtimeNanoseconds));
        if (!changed &&
            !sizeChanged &&
            !racy)
        {
            return;
        }

        toHash.Add((start, Path.Combine(folder, file.Name)));
    }

    // Whether the file's content, staged as git would stage it, is the blob staged. One deleted
    // since it was listed is left to git, to say it is gone.
    bool SameContent(int entry, string path) =>
        CleanFilter.BlobId(CleanContent(entry, File.ReadAllBytes(path))) == index.Entries[entry].Id;

    AttributeRules? attributes;

    ByteSpan CleanContent(int entry, byte[] content)
    {
        attributes ??= new(repository, workTree, StagedBlob);
        var path = index.PathText(entry);
        var id = index.Entries[entry].Id;
        return CleanFilter.ToGit(
            content,
            attributes.For(path),
            repository.Config,
            () => objects.TryRead(id) is { Type: GitObjectType.Blob } blob && CleanFilter.IsTextWithCrlf(blob.Data));
    }

    // A file staged, by its path, for a .gitattributes not in the working tree.
    // In a merge's conflict, ours, as git's read_blob_data_from_index takes it.
    byte[]? StagedBlob(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var low = 0;
        var high = index.Entries.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (index.PathOf(middle).SequenceCompareTo(bytes) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var entry = low; entry < index.Entries.Length && index.PathOf(entry).SequenceEqual(bytes); entry++)
        {
            if (index.Entries[entry].Stage is 0 or 2)
            {
                return objects.TryRead(index.Entries[entry].Id)?.Data;
            }
        }

        return null;
    }

    // As git status, untracked files normal: a folder not tracked is listed, as one, only where
    // it has something in it, however far in, that isn't ignored: a file, or a repository. No
    // folder is listed here that is a repository, so none has a .git in it.
    bool HasUntracked(string folder, IgnoreRules.Folder rules)
    {
        cancel.ThrowIfCancellationRequested();
        foreach (var entry in List(folder))
        {
            if (ignore.IsIgnored(rules, entry.Name, entry.IsFolder))
            {
                continue;
            }

            if (!entry.IsFolder)
            {
                return true;
            }

            var full = Path.Combine(folder, entry.Name);
            if (CodeCommands.IsRepo(full) ||
                HasUntracked(full, ignore.Enter(rules, entry.Name)))
            {
                return true;
            }
        }

        return false;
    }

    // As git status, untracked files all: each file, but a repository inside, which is one.
    void AllUntracked(string folder, IgnoreRules.Folder rules)
    {
        cancel.ThrowIfCancellationRequested();
        foreach (var entry in List(folder))
        {
            if (ignore.IsIgnored(rules, entry.Name, entry.IsFolder))
            {
                continue;
            }

            var path = rules.PathOf(entry.Name);
            var full = Path.Combine(folder, entry.Name);
            if (!entry.IsFolder ||
                CodeCommands.IsRepo(full))
            {
                untrackedPaths.Add(path);
                continue;
            }

            AllUntracked(full, ignore.Enter(rules, entry.Name));
        }
    }
}
