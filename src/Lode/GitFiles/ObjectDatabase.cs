/// <summary>
/// What git keeps an object as. The numbers are a pack's.
/// </summary>
enum GitObjectType
{
    None = 0,
    Commit = 1,
    Tree = 2,
    Blob = 3,
    Tag = 4
}

/// <summary>
/// A repository's objects, loose and packed, in its objects folder and any its alternates name,
/// read as git reads them: inflated, and, in a pack, a delta put back together on its base.
/// One inflater is kept for all of them. Not for more than one thread at once.
/// </summary>
sealed class ObjectDatabase : IDisposable
{
    readonly List<string> folders = [];
    List<PackFile>? packs;
    readonly ZLibDecoder decoder = new();

    public ObjectDatabase(string objects) =>
        AddFolder(objects, 0);

    // As git follows objects/info/alternates, the folders it names in turn, five deep.
    void AddFolder(string folder, int depth)
    {
        var full = Path.GetFullPath(folder);
        if (folders.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        folders.Add(full);
        if (depth >= 5)
        {
            return;
        }

        if (GitRepository.ReadSmall(Path.Combine(full, "info", "alternates")) is not { } alternates)
        {
            return;
        }

        foreach (var line in alternates.Split('\n'))
        {
            var alternate = line.Trim();
            if (alternate.Length == 0 ||
                alternate.StartsWith('#'))
            {
                continue;
            }

            if (!Path.IsPathRooted(alternate))
            {
                alternate = Path.Combine(full, alternate);
            }

            AddFolder(alternate, depth + 1);
        }
    }

    List<PackFile> Packs()
    {
        if (packs != null)
        {
            return packs;
        }

        packs = [];
        foreach (var folder in folders)
        {
            var pack = Path.Combine(folder, "pack");
            if (!Directory.Exists(pack))
            {
                continue;
            }

            foreach (var index in Directory.EnumerateFiles(pack, "*.idx"))
            {
                if (PackFile.Open(index, this) is { } file)
                {
                    packs.Add(file);
                }
            }
        }

        return packs;
    }

    /// <summary>
    /// The object, or null where the repository hasn't got it.
    /// </summary>
    public (GitObjectType Type, byte[] Data)? TryRead(ObjectId id)
    {
        foreach (var pack in Packs())
        {
            if (pack.TryFind(id, out var offset))
            {
                return pack.Read(offset);
            }
        }

        foreach (var folder in folders)
        {
            if (ReadLoose(folder, id) is { } loose)
            {
                return loose;
            }
        }

        return null;
    }

    public (GitObjectType Type, byte[] Data) Read(ObjectId id)
    {
        if (TryRead(id) is not { } read)
        {
            throw new GitFilesException($"Object {id} is missing");
        }

        return read;
    }

    /// <summary>
    /// The object, which must be of <paramref name="type"/>, as a tag that is a tree's isn't a
    /// commit.
    /// </summary>
    public byte[] Read(ObjectId id, GitObjectType type)
    {
        var (found, data) = Read(id);
        if (found != type)
        {
            throw new GitFilesException($"Object {id} is a {found}, not a {type}");
        }

        return data;
    }

    /// <summary>
    /// What an object is and how big, without inflating all of it.
    /// </summary>
    public (GitObjectType Type, long Size)? TryReadHeader(ObjectId id)
    {
        foreach (var pack in Packs())
        {
            if (pack.TryFind(id, out var offset))
            {
                return pack.ReadHeader(offset);
            }
        }

        foreach (var folder in folders)
        {
            if (ReadLooseHeader(folder, id) is { } loose)
            {
                return loose;
            }
        }

        return null;
    }

    static string LoosePath(string folder, ObjectId id)
    {
        var hex = id.ToString();
        return Path.Combine(folder, hex[..2], hex[2..]);
    }

    static byte[]? ReadAll(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception exception)
            when (
                exception is
                    FileNotFoundException or
                    DirectoryNotFoundException)
        {
            return null;
        }
    }

    // A loose object is "blob 1234", a NUL, then the object, all of it deflated.
    (GitObjectType Type, byte[] Data)? ReadLoose(string folder, ObjectId id)
    {
        if (ReadAll(LoosePath(folder, id)) is not { } compressed)
        {
            return null;
        }

        decoder.Reset();
        Span<byte> start = stackalloc byte[64];
        var status = decoder.Decompress(compressed, start, out var consumed, out var written);
        if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall))
        {
            throw new GitFilesException($"Loose object {id} is corrupt");
        }

        var (type, size, headerLength) = ParseLooseHeader(start[..written], id);
        var data = new byte[size];
        var already = written - headerLength;
        start.Slice(headerLength, already).CopyTo(data);
        if (already < size)
        {
            status = decoder.Decompress(compressed.AsSpan(consumed), data.AsSpan(already), out _, out var rest);
            if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) ||
                already + rest != size)
            {
                throw new GitFilesException($"Loose object {id} is corrupt");
            }
        }

        return (type, data);
    }

    (GitObjectType Type, long Size)? ReadLooseHeader(string folder, ObjectId id)
    {
        if (ReadAll(LoosePath(folder, id)) is not { } compressed)
        {
            return null;
        }

        decoder.Reset();
        Span<byte> start = stackalloc byte[64];
        var status = decoder.Decompress(compressed, start, out _, out var written);
        if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall))
        {
            throw new GitFilesException($"Loose object {id} is corrupt");
        }

        var (type, size, _) = ParseLooseHeader(start[..written], id);
        return (type, size);
    }

    static (GitObjectType Type, int Size, int HeaderLength) ParseLooseHeader(ByteSpan header, ObjectId id)
    {
        var space = header.IndexOf((byte)' ');
        var nul = header.IndexOf((byte)0);
        if (space < 0 ||
            nul < space)
        {
            throw new GitFilesException($"Loose object {id} is corrupt");
        }

        var type = TypeNamed(header[..space]);
        if (type == GitObjectType.None)
        {
            throw new GitFilesException($"Loose object {id} is of no type known");
        }

        if (!int.TryParse(header[(space + 1)..nul], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            throw new GitFilesException($"Loose object {id} is too big");
        }

        return (type, size, nul + 1);
    }

    /// <summary>
    /// The type a loose object's header, or a tag's type line, names.
    /// </summary>
    internal static GitObjectType TypeNamed(ByteSpan name)
    {
        if (name.SequenceEqual("commit"u8))
        {
            return GitObjectType.Commit;
        }

        if (name.SequenceEqual("tree"u8))
        {
            return GitObjectType.Tree;
        }

        if (name.SequenceEqual("blob"u8))
        {
            return GitObjectType.Blob;
        }

        if (name.SequenceEqual("tag"u8))
        {
            return GitObjectType.Tag;
        }

        return GitObjectType.None;
    }

    /// <summary>
    /// Inflates zlib data into all of <paramref name="into"/>, as big as git said the object is.
    /// </summary>
    internal void Inflate(ByteSpan from, Span<byte> into)
    {
        decoder.Reset();
        var status = decoder.Decompress(from, into, out _, out var written);
        if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) ||
            written != into.Length)
        {
            throw new GitFilesException("A packed object is corrupt");
        }
    }

    /// <summary>
    /// Inflates the start of zlib data, as far as <paramref name="into"/> goes or the data ends.
    /// How much it inflated to.
    /// </summary>
    internal int InflateStart(ByteSpan from, Span<byte> into)
    {
        decoder.Reset();
        var status = decoder.Decompress(from, into, out _, out var written);
        if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall))
        {
            throw new GitFilesException("A packed object is corrupt");
        }

        return written;
    }

    public void Dispose()
    {
        if (packs != null)
        {
            foreach (var pack in packs)
            {
                pack.Dispose();
            }
        }

        decoder.Dispose();
    }
}
