/// <summary>
/// The index, as git writes it, version 2, 3 or 4: a header, then an entry for each path staged,
/// in order of their paths' bytes, each with what its file was last seen to be and the blob
/// staged, then extensions, of which the cache tree is read: the tree each folder's entries
/// make, where still known. An extension git would need to understand, a split or sparse
/// index's, isn't read here.
/// </summary>
sealed class IndexFile
{
    public const ushort AssumeValid = 0x8000;
    const ushort extendedFlag = 0x4000;
    public const ushort SkipWorktree = 0x4000;
    public const ushort IntentToAdd = 0x2000;

    public struct Entry
    {
        public int PathStart;
        public int PathLength;
        public uint CtimeSeconds;
        public uint CtimeNanoseconds;
        public uint MtimeSeconds;
        public uint MtimeNanoseconds;
        public uint Mode;
        public uint Size;
        public ObjectId Id;
        public ushort Flags;
        public ushort Extended;

        public int Stage => (Flags >> 12) & 3;
    }

    public sealed class CacheTree
    {
        // -1 where git has let go of what the folder's tree is, since a file in it was staged.
        public int EntryCount { get; init; }
        public ObjectId Id { get; init; }
        public List<(byte[] Name, CacheTree Tree)> Children { get; } = [];

        public CacheTree? Child(ByteSpan name)
        {
            foreach (var (childName, tree) in Children)
            {
                if (name.SequenceEqual(childName))
                {
                    return tree;
                }
            }

            return null;
        }
    }

    public byte[] Paths { get; }
    public Entry[] Entries { get; }
    public CacheTree? Tree { get; }

    /// <summary>
    /// When the index was written, by its file: an entry for a file changed in that second or
    /// later may look unchanged when it isn't, so its content is looked at.
    /// </summary>
    public (long Seconds, long Nanoseconds) Timestamp { get; }

    /// <summary>
    /// How long the index's file is, which with <see cref="Timestamp"/> tells one written since
    /// from this one, as git writes it anew whatever it changes. 0 where there is none.
    /// </summary>
    public long Length { get; }

    IndexFile(byte[] paths, Entry[] entries, CacheTree? tree, (long, long) timestamp, long length)
    {
        Paths = paths;
        Entries = entries;
        Tree = tree;
        Timestamp = timestamp;
        Length = length;
    }

    public ByteSpan PathOf(int index) =>
        Paths.AsSpan(Entries[index].PathStart, Entries[index].PathLength);

    public string PathText(int index) =>
        Encoding.UTF8.GetString(PathOf(index));

    /// <summary>
    /// The index of the git folder, or an empty one where there is none yet. Read whole, shared
    /// for writing and deleting, as git replaces the index by renaming another over it.
    /// </summary>
    public static IndexFile Read(string gitDir)
    {
        var path = Path.Combine(gitDir, "index");
        byte[] bytes;
        DateTime written;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception)
            when (
                exception is
                    FileNotFoundException or
                    DirectoryNotFoundException)
        {
            return new([], [], null, (0, 0), 0);
        }

        return Parse(bytes, UnixTime(written));
    }

    /// <summary>
    /// A time as git for Windows has a file's: seconds and nanoseconds since 1970.
    /// </summary>
    public static (long Seconds, long Nanoseconds) UnixTime(DateTime time)
    {
        var ticks = time.Ticks - DateTime.UnixEpoch.Ticks;
        return (Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder), remainder * 100);
    }

    static IndexFile Parse(byte[] bytes, (long, long) timestamp)
    {
        var span = bytes.AsSpan();
        if (span.Length < 12 + ObjectId.Length ||
            !span[..4].SequenceEqual("DIRC"u8))
        {
            throw new GitFilesException("The index is corrupt");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        if (version is < 2 or > 4)
        {
            throw new GitFilesException($"Index version {version} isn't read here");
        }

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        var entries = new Entry[count];
        var end = span.Length - ObjectId.Length;
        var position = 12;
        var paths = bytes;
        byte[]? previous = null;
        var compressed = version == 4;
        List<byte>? built = null;
        if (compressed)
        {
            built = new(count * 32);
        }

        for (var index = 0; index < count; index++)
        {
            if (position + 62 > end)
            {
                throw new GitFilesException("The index is corrupt");
            }

            var entry = span[position..];
            ref var e = ref entries[index];
            e.CtimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(entry);
            e.CtimeNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            e.MtimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            e.MtimeNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            e.Mode = BinaryPrimitives.ReadUInt32BigEndian(entry[24..]);
            e.Size = BinaryPrimitives.ReadUInt32BigEndian(entry[36..]);
            e.Id = new(entry.Slice(40, ObjectId.Length));
            e.Flags = BinaryPrimitives.ReadUInt16BigEndian(entry[60..]);
            var header = 62;
            if ((e.Flags & extendedFlag) != 0)
            {
                if (version < 3)
                {
                    throw new GitFilesException("The index is corrupt");
                }

                e.Extended = BinaryPrimitives.ReadUInt16BigEndian(entry[62..]);
                header = 64;
            }

            var nameStart = position + header;
            if (!compressed)
            {
                var nul = span[nameStart..end].IndexOf((byte)0);
                if (nul < 0)
                {
                    throw new GitFilesException("The index is corrupt");
                }

                e.PathStart = nameStart;
                e.PathLength = nul;

                // Padded with one to eight NULs, to a multiple of eight.
                var length = header + nul;
                position += (length + 8) & ~7;
                continue;
            }

            // Version 4: how many bytes of the path before to drop, then the rest of this one.
            var at = nameStart;
            var drop = DecodeVarint(span, ref at);
            var suffixEnd = span[at..end].IndexOf((byte)0);
            if (suffixEnd < 0)
            {
                throw new GitFilesException("The index is corrupt");
            }

            // Of the first, there is no path before to drop any of.
            var kept = (previous?.Length ?? 0) - drop;
            if (kept < 0)
            {
                throw new GitFilesException("The index is corrupt");
            }

            var keep = (int)kept;

            var full = new byte[keep + suffixEnd];
            previous?.AsSpan(0, keep).CopyTo(full);
            span.Slice(at, suffixEnd).CopyTo(full.AsSpan(keep));
            e.PathStart = built!.Count;
            e.PathLength = full.Length;
            built.AddRange(full);
            previous = full;
            position = at + suffixEnd + 1;
        }

        if (compressed)
        {
            paths = built!.ToArray();
        }

        CacheTree? tree = null;
        while (position + 8 <= end)
        {
            var signature = span.Slice(position, 4);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
            var data = span.Slice(position + 8, Math.Min(size, end - position - 8));
            if (signature.SequenceEqual("TREE"u8))
            {
                var at = 0;
                (_, tree) = ReadCacheTree(data, ref at);
            }
            else if (signature[0] is < (byte)'A' or > (byte)'Z')
            {
                // Lower case: one git must understand to read the index right, a split index's
                // link or a sparse index's sdir.
                throw new GitFilesException($"The index has a {Encoding.ASCII.GetString(signature)} extension, which isn't read here");
            }

            position += 8 + size;
        }

        return new(paths, entries, tree, timestamp, bytes.Length);
    }

    static long DecodeVarint(ByteSpan span, ref int position)
    {
        var c = span[position++];
        long value = c & 0x7f;
        while ((c & 0x80) != 0)
        {
            value++;
            c = span[position++];
            value = (value << 7) + (c & 0x7f);
        }

        return value;
    }

    // Each folder's: its name, NUL, how many entries it covers, -1 where let go of, a space, how
    // many folders in it, a line break, then its tree's id where known, then each folder's in it.
    static (byte[] Name, CacheTree Tree) ReadCacheTree(ByteSpan data, ref int position)
    {
        var nul = data[position..].IndexOf((byte)0);
        var name = data.Slice(position, nul).ToArray();
        position += nul + 1;
        var space = data[position..].IndexOf((byte)' ');
        var entryCount = int.Parse(data.Slice(position, space), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        position += space + 1;
        var newline = data[position..].IndexOf((byte)'\n');
        var subtrees = int.Parse(data.Slice(position, newline), NumberStyles.None, CultureInfo.InvariantCulture);
        position += newline + 1;
        ObjectId id = default;
        if (entryCount >= 0)
        {
            id = new(data.Slice(position, ObjectId.Length));
            position += ObjectId.Length;
        }

        var tree = new CacheTree
        {
            EntryCount = entryCount,
            Id = id
        };
        for (var index = 0; index < subtrees; index++)
        {
            tree.Children.Add(ReadCacheTree(data, ref position));
        }

        return (name, tree);
    }
}
