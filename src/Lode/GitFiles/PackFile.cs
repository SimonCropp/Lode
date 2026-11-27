/// <summary>
/// A pack and its index, as git gc and fetch leave them: the index's ids sorted, fanned out by
/// their first byte, each with where in the pack its object is. Version 2 of the index, git's
/// since 1.5.2, has the ids, then their CRCs, then where each is; version 1, before it, has each
/// where then id together. An object in the pack is whole, or a delta on another, by where it is
/// in the pack or by its id. The bases deltas are put back together on are kept, as trees of one
/// commit after another are deltas on the same ones.
/// </summary>
sealed class PackFile : IDisposable
{
    const int offsetDelta = 6;
    const int referenceDelta = 7;

    /// <summary>
    /// How much of the bases are kept, then all let go, as git's delta base cache is. Tests make
    /// it small, to see them let go.
    /// </summary>
    internal static long CacheLimit { get; set; } = 64 * 1024 * 1024;

    readonly MappedFile index;
    readonly string packPath;
    readonly ObjectDatabase database;
    readonly int count;
    readonly bool version1;
    MappedFile? pack;
    readonly Dictionary<long, (GitObjectType Type, byte[] Data)> bases = [];
    long cached;

    // Where the fan out starts, and the ids, each so far after the one before.
    readonly long fanoutStart;
    readonly long namesStart;
    readonly int nameStride;

    PackFile(MappedFile index, string packPath, ObjectDatabase database, bool version1)
    {
        this.index = index;
        this.packPath = packPath;
        this.database = database;
        this.version1 = version1;
        if (version1)
        {
            namesStart = 256 * 4 + 4;
            nameStride = 4 + ObjectId.Length;
        }
        else
        {
            fanoutStart = 8;
            namesStart = 8 + 256 * 4;
            nameStride = ObjectId.Length;
        }

        count = (int)Fanout(255);
    }

    static readonly byte[] version2Magic = [0xff, (byte)'t', (byte)'O', (byte)'c'];

    /// <summary>
    /// The pack for an index, or null where its pack isn't there, half written say.
    /// </summary>
    public static PackFile? Open(string indexPath, ObjectDatabase database)
    {
        var packPath = Path.ChangeExtension(indexPath, ".pack");
        if (!File.Exists(packPath) ||
            MappedFile.Open(indexPath) is not { } index)
        {
            return null;
        }

        var header = index.Span(0, 8 + 256 * 4);
        var version2 = header.Length >= 8 + 256 * 4 &&
                       header[..4].SequenceEqual(version2Magic);
        if (version2 &&
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]) == 2)
        {
            return new(index, packPath, database, false);
        }

        if (!version2 &&
            header.Length >= 256 * 4)
        {
            return new(index, packPath, database, true);
        }

        index.Dispose();
        throw new GitFilesException($"Pack index {indexPath} is of a version not known");
    }

    uint Fanout(int value) =>
        BinaryPrimitives.ReadUInt32BigEndian(index.Span(fanoutStart + value * 4, 4));

    public bool TryFind(ObjectId id, out long offset)
    {
        var first = id.FirstByte;
        var low = 0;
        if (first > 0)
        {
            low = (int)Fanout(first - 1);
        }

        var high = (int)Fanout(first);
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            var compared = id.CompareTo(index.Span(namesStart + (long)middle * nameStride, ObjectId.Length));
            if (compared == 0)
            {
                offset = OffsetAt(middle);
                return true;
            }

            if (compared < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        offset = 0;
        return false;
    }

    // Version 1: before each id. Version 2: after the ids, a CRC for each, then a four byte
    // offset for each, the top bit set for one past 2 GB, which is then an index into eight byte
    // offsets after them.
    long OffsetAt(int position)
    {
        if (version1)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(index.Span(256 * 4 + (long)position * nameStride, 4));
        }

        var offsets = namesStart + (long)count * (ObjectId.Length + 4);
        var small = BinaryPrimitives.ReadUInt32BigEndian(index.Span(offsets + position * 4L, 4));
        if ((small & 0x8000_0000) == 0)
        {
            return small;
        }

        var large = offsets + count * 4L + (small & 0x7fff_ffff) * 8L;
        return (long)BinaryPrimitives.ReadUInt64BigEndian(index.Span(large, 8));
    }

    MappedFile Pack()
    {
        if (pack != null)
        {
            return pack;
        }

        pack = MappedFile.Open(packPath) ?? throw new GitFilesException($"Pack {packPath} is missing");
        var header = pack.Span(0, 12);
        if (header.Length < 12 ||
            !header[..4].SequenceEqual("PACK"u8))
        {
            throw new GitFilesException($"Pack {packPath} is corrupt");
        }

        return pack;
    }

    // An entry starts with its type and size, seven bits at a time after the first four, then,
    // for a delta, where its base is: back from here, or by id.
    (int Type, long Size, long DataStart, long BaseOffset, ObjectId BaseId) Entry(long offset)
    {
        var bytes = Pack().Span(offset, 32);
        var position = 0;
        var c = bytes[position++];
        var type = (c >> 4) & 7;
        long size = c & 15;
        var shift = 4;
        while ((c & 0x80) != 0)
        {
            c = bytes[position++];
            size |= (long)(c & 0x7f) << shift;
            shift += 7;
        }

        long baseOffset = 0;
        ObjectId baseId = default;
        if (type == offsetDelta)
        {
            c = bytes[position++];
            long back = c & 0x7f;
            while ((c & 0x80) != 0)
            {
                back++;
                c = bytes[position++];
                back = (back << 7) + (c & 0x7f);
            }

            baseOffset = offset - back;
        }
        else if (type == referenceDelta)
        {
            baseId = new(Pack().Span(offset + position, ObjectId.Length));
            position += ObjectId.Length;
        }

        return (type, size, offset + position, baseOffset, baseId);
    }

    byte[] Inflate(long start, long size)
    {
        if (size > Array.MaxLength)
        {
            throw new GitFilesException("A packed object is too big");
        }

        var data = new byte[size];
        database.Inflate(Pack().From(start), data);
        return data;
    }

    public (GitObjectType Type, byte[] Data) Read(long offset)
    {
        // Down the chain of deltas to a whole object, or a base already put back together,
        // then each delta applied on the way back up.
        List<(long Offset, long DataStart, long Size)>? deltas = null;
        var current = offset;
        (GitObjectType Type, byte[] Data) whole;

        // Where the whole object at the bottom is in this pack, to be kept as a base: not when
        // kept already, nor when it is another pack's, found by its id.
        long? wholeAt = null;
        while (true)
        {
            if (bases.TryGetValue(current, out var known))
            {
                whole = known;
                break;
            }

            var entry = Entry(current);
            if (entry.Type is >= 1 and <= 4)
            {
                whole = ((GitObjectType)entry.Type, Inflate(entry.DataStart, entry.Size));
                wholeAt = current;
                break;
            }

            deltas ??= [];
            deltas.Add((current, entry.DataStart, entry.Size));
            if (entry.Type == offsetDelta)
            {
                current = entry.BaseOffset;
                continue;
            }

            if (entry.Type == referenceDelta)
            {
                whole = database.Read(entry.BaseId);
                break;
            }

            throw new GitFilesException($"Pack {packPath} has an entry of no type known");
        }

        if (deltas == null)
        {
            return whole;
        }

        if (wholeAt is { } at)
        {
            Keep(at, whole);
        }

        for (var index = deltas.Count - 1; index >= 0; index--)
        {
            var (deltaAt, start, size) = deltas[index];
            whole = (whole.Type, Delta.Apply(whole.Data, Inflate(start, size)));
            if (index > 0)
            {
                Keep(deltaAt, whole);
            }
        }

        return whole;
    }

    void Keep(long offset, (GitObjectType Type, byte[] Data) value)
    {
        if (cached + value.Data.Length > CacheLimit)
        {
            bases.Clear();
            cached = 0;
        }

        if (bases.TryAdd(offset, value))
        {
            cached += value.Data.Length;
        }
    }

    public (GitObjectType Type, long Size) ReadHeader(long offset)
    {
        var entry = Entry(offset);
        if (entry.Type is >= 1 and <= 4)
        {
            return ((GitObjectType)entry.Type, entry.Size);
        }

        // A delta's size after it is applied is its second number, after its base's. Its type is
        // its base's, however far down.
        Span<byte> start = stackalloc byte[32];
        var written = database.InflateStart(Pack().From(entry.DataStart), start);
        var position = 0;
        Delta.ReadSize(start[..written], ref position);
        var size = Delta.ReadSize(start[..written], ref position);
        var type = BaseType(entry);
        return (type, size);
    }

    GitObjectType BaseType((int Type, long Size, long DataStart, long BaseOffset, ObjectId BaseId) entry)
    {
        while (true)
        {
            if (entry.Type is >= 1 and <= 4)
            {
                return (GitObjectType)entry.Type;
            }

            if (entry.Type == referenceDelta)
            {
                return database.TryReadHeader(entry.BaseId)?.Type ?? throw new GitFilesException($"Object {entry.BaseId} is missing");
            }

            entry = Entry(entry.BaseOffset);
        }
    }

    public void Dispose()
    {
        index.Dispose();
        pack?.Dispose();
    }
}

/// <summary>
/// A pack's deltas: the base's size, the result's, then instructions, each a copy of part of the
/// base or bytes of its own.
/// </summary>
static class Delta
{
    public static long ReadSize(ByteSpan delta, ref int position)
    {
        long size = 0;
        var shift = 0;
        byte c;
        do
        {
            if (position >= delta.Length)
            {
                throw new GitFilesException("A delta is corrupt");
            }

            c = delta[position++];
            size |= (long)(c & 0x7f) << shift;
            shift += 7;
        } while ((c & 0x80) != 0);

        return size;
    }

    public static byte[] Apply(byte[] source, ByteSpan delta)
    {
        var position = 0;
        var sourceSize = ReadSize(delta, ref position);
        var resultSize = ReadSize(delta, ref position);
        if (sourceSize != source.Length ||
            resultSize > Array.MaxLength)
        {
            throw new GitFilesException("A delta doesn't fit its base");
        }

        var result = new byte[resultSize];
        var written = 0;
        while (position < delta.Length)
        {
            var command = delta[position++];
            if ((command & 0x80) != 0)
            {
                long offset = 0;
                long size = 0;
                for (var bit = 0; bit < 4; bit++)
                {
                    if ((command & (1 << bit)) != 0)
                    {
                        offset |= (long)delta[position++] << (8 * bit);
                    }
                }

                for (var bit = 0; bit < 3; bit++)
                {
                    if ((command & (0x10 << bit)) != 0)
                    {
                        size |= (long)delta[position++] << (8 * bit);
                    }
                }

                if (size == 0)
                {
                    size = 0x10000;
                }

                if (offset + size > source.Length ||
                    written + size > result.Length)
                {
                    throw new GitFilesException("A delta is corrupt");
                }

                source.AsSpan((int)offset, (int)size).CopyTo(result.AsSpan(written));
                written += (int)size;
                continue;
            }

            if (command == 0 ||
                position + command > delta.Length ||
                written + command > result.Length)
            {
                throw new GitFilesException("A delta is corrupt");
            }

            delta.Slice(position, command).CopyTo(result.AsSpan(written));
            position += command;
            written += command;
        }

        if (written != result.Length)
        {
            throw new GitFilesException("A delta is corrupt");
        }

        return result;
    }
}
