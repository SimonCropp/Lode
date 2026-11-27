/// <summary>
/// A tree's entries, each its mode in octal, a space, its name, a NUL and its id, sorted as git
/// sorts them: by name, a folder's as though a slash followed it.
/// </summary>
static class Trees
{
    public const int FileType = 0xF000;
    public const int Folder = 0x4000;
    public const int Regular = 0x8000;
    public const int Link = 0xA000;
    public const int Gitlink = 0xE000;

    public readonly record struct Entry(int Mode, int NameStart, int NameLength, ObjectId Id)
    {
        public bool IsFolder => (Mode & FileType) == Folder;

        public ByteSpan Name(byte[] tree) =>
            tree.AsSpan(NameStart, NameLength);
    }

    public static List<Entry> Read(byte[] tree)
    {
        var entries = new List<Entry>();
        var position = 0;
        while (position < tree.Length)
        {
            var mode = 0;
            while (position < tree.Length &&
                   tree[position] != ' ')
            {
                var digit = tree[position] - '0';
                if (digit is < 0 or > 7)
                {
                    throw new GitFilesException("A tree is corrupt");
                }

                mode = mode * 8 + digit;
                position++;
            }

            position++;
            var nameStart = position;
            var nul = tree.AsSpan(position).IndexOf((byte)0);
            if (nul < 0 ||
                position + nul + 1 + ObjectId.Length > tree.Length)
            {
                throw new GitFilesException("A tree is corrupt");
            }

            position += nul + 1;
            entries.Add(new(Canonical(mode), nameStart, nul, new(tree.AsSpan(position, ObjectId.Length))));
            position += ObjectId.Length;
        }

        return entries;
    }

    /// <summary>
    /// A mode as git takes it whatever was written: a file 644 or 755, by whether its owner may
    /// run it; a link; a folder; anything else a submodule's commit.
    /// </summary>
    public static int Canonical(int mode)
    {
        switch (mode & FileType)
        {
            case Regular:
                if ((mode & 0x40) != 0)
                {
                    return Regular | 0x1ED;
                }

                return Regular | 0x1A4;
            case Link:
                return Link;
            case Folder:
                return Folder;
            default:
                return Gitlink;
        }
    }

    /// <summary>
    /// As git's base_name_compare: by the bytes of the names, a folder's taken as having a slash
    /// after it.
    /// </summary>
    public static int Compare(ByteSpan first, bool firstFolder, ByteSpan second, bool secondFolder)
    {
        var common = Math.Min(first.Length, second.Length);
        var compared = first[..common].SequenceCompareTo(second[..common]);
        if (compared != 0)
        {
            return compared;
        }

        var firstNext = NextAfter(first, common, firstFolder);
        var secondNext = NextAfter(second, common, secondFolder);
        return firstNext.CompareTo(secondNext);
    }

    static int NextAfter(ByteSpan name, int at, bool folder)
    {
        if (at < name.Length)
        {
            return name[at];
        }

        if (folder)
        {
            return '/';
        }

        return 0;
    }

    /// <summary>
    /// A file in one tree and not the other, or in both but changed, with its mode and id in each,
    /// mode 0 where it isn't there.
    /// </summary>
    public sealed record Change(byte[] Path, int OldMode, ObjectId OldId, int NewMode, ObjectId NewId)
    {
        public string Text => Encoding.UTF8.GetString(Path);
    }

    /// <summary>
    /// As git diff-tree -r: every file that differs between the trees, in the order of their
    /// paths, folders gone into, those the same in both passed over. Either tree may be none: the
    /// first commit's parent, say.
    /// </summary>
    public static List<Change> Diff(ObjectDatabase objects, ObjectId? before, ObjectId? after)
    {
        var changes = new List<Change>();
        Diff(objects, before, after, [], changes);
        return changes;
    }

    static readonly byte[] empty = [];

    static void Diff(ObjectDatabase objects, ObjectId? before, ObjectId? after, byte[] prefix, List<Change> changes)
    {
        var oldTree = empty;
        if (before is { } oldId)
        {
            oldTree = objects.Read(oldId, GitObjectType.Tree);
        }

        var newTree = empty;
        if (after is { } newId)
        {
            newTree = objects.Read(newId, GitObjectType.Tree);
        }

        var oldEntries = Read(oldTree);
        var newEntries = Read(newTree);
        var i = 0;
        var j = 0;
        while (i < oldEntries.Count || j < newEntries.Count)
        {
            int compared;
            if (i >= oldEntries.Count)
            {
                compared = 1;
            }
            else if (j >= newEntries.Count)
            {
                compared = -1;
            }
            else
            {
                compared = Compare(oldEntries[i].Name(oldTree), oldEntries[i].IsFolder, newEntries[j].Name(newTree), newEntries[j].IsFolder);
            }

            if (compared < 0)
            {
                Gone(objects, oldEntries[i], Join(prefix, oldEntries[i].Name(oldTree)), changes, true);
                i++;
                continue;
            }

            if (compared > 0)
            {
                Gone(objects, newEntries[j], Join(prefix, newEntries[j].Name(newTree)), changes, false);
                j++;
                continue;
            }

            var oldEntry = oldEntries[i];
            var newEntry = newEntries[j];
            i++;
            j++;
            if (oldEntry.Id == newEntry.Id &&
                oldEntry.Mode == newEntry.Mode)
            {
                continue;
            }

            var path = Join(prefix, newEntry.Name(newTree));
            if (oldEntry.IsFolder)
            {
                Diff(objects, oldEntry.Id, newEntry.Id, path, changes);
                continue;
            }

            changes.Add(new(path, oldEntry.Mode, oldEntry.Id, newEntry.Mode, newEntry.Id));
        }
    }

    // A file only one tree has, or every file in a folder only one has.
    static void Gone(ObjectDatabase objects, Entry entry, byte[] path, List<Change> changes, bool deleted)
    {
        if (!entry.IsFolder)
        {
            if (deleted)
            {
                changes.Add(new(path, entry.Mode, entry.Id, 0, default));
                return;
            }

            changes.Add(new(path, 0, default, entry.Mode, entry.Id));
            return;
        }

        if (deleted)
        {
            Diff(objects, entry.Id, null, path, changes);
            return;
        }

        Diff(objects, null, entry.Id, path, changes);
    }

    public static byte[] Join(byte[] prefix, ByteSpan name)
    {
        if (prefix.Length == 0)
        {
            return name.ToArray();
        }

        var path = new byte[prefix.Length + 1 + name.Length];
        prefix.CopyTo(path, 0);
        path[prefix.Length] = (byte)'/';
        name.CopyTo(path.AsSpan(prefix.Length + 1));
        return path;
    }
}
