/// <summary>
/// The commit graph git gc and fetch write, one file or a chain of them: for each commit its
/// tree, parents and date, so a history is walked without inflating a commit at all. A commit is
/// known by its position, counted across the chain from its first file; a parent is given by its
/// position too.
/// </summary>
sealed class CommitGraph : IDisposable
{
    const uint parentNone = 0x7000_0000;
    const uint extraEdges = 0x8000_0000;
    const int dataWidth = ObjectId.Length + 16;

    sealed class Layer(MappedFile file, int start, int count, long fanout, long names, long data, long edges) :
        IDisposable
    {
        public MappedFile File { get; } = file;
        public int Start { get; } = start;
        public int Count { get; } = count;
        public long Fanout { get; } = fanout;
        public long Names { get; } = names;
        public long Data { get; } = data;
        public long Edges { get; } = edges;

        public void Dispose() =>
            File.Dispose();
    }

    readonly List<Layer> layers;

    public int Count { get; }

    CommitGraph(List<Layer> layers)
    {
        this.layers = layers;
        Count = layers.Sum(_ => _.Count);
    }

    /// <summary>
    /// The graph in <paramref name="objects"/>, or null where none is written, or it is of a
    /// version not known or cut short, as git then walks without one. A chain is read as far as
    /// its first such file.
    /// </summary>
    public static CommitGraph? Open(string objects)
    {
        var layers = new List<Layer>();
        var graphs = Path.Combine(objects, "info", "commit-graphs");
        if (GitRepository.ReadSmall(Path.Combine(graphs, "commit-graph-chain")) is { } chain)
        {
            var start = 0;
            foreach (var line in chain.Split('\n'))
            {
                var hash = line.Trim();
                if (hash.Length == 0)
                {
                    continue;
                }

                if (OpenLayer(Path.Combine(graphs, $"graph-{hash}.graph"), start) is not { } layer)
                {
                    break;
                }

                layers.Add(layer);
                start += layer.Count;
            }
        }
        else if (OpenLayer(Path.Combine(objects, "info", "commit-graph"), 0) is { } layer)
        {
            layers.Add(layer);
        }

        if (layers.Count == 0)
        {
            return null;
        }

        return new(layers);
    }

    // "CGPH", version 1, SHA-1, the number of chunks, then a table of where each chunk starts.
    static Layer? OpenLayer(string path, int start)
    {
        if (MappedFile.Open(path) is not { } file)
        {
            return null;
        }

        if (ReadLayer(file, start) is { } layer)
        {
            return layer;
        }

        file.Dispose();
        return null;
    }

    static Layer? ReadLayer(MappedFile file, int start)
    {
        var header = file.Span(0, 8);
        if (header.Length < 8 ||
            !header[..4].SequenceEqual("CGPH"u8) ||
            header[4] != 1 ||
            header[5] != 1 ||
            file.Length < 8 + (header[6] + 1) * 12)
        {
            return null;
        }

        var chunks = header[6];
        long fanout = 0;
        long names = 0;
        long data = 0;
        long edges = 0;
        var table = file.Span(8, (chunks + 1) * 12);
        for (var index = 0; index < chunks; index++)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(table[(index * 12)..]);
            var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(table[(index * 12 + 4)..]);
            switch (id)
            {
                case 0x4f49_4446:
                    fanout = offset;
                    break;
                case 0x4f49_444c:
                    names = offset;
                    break;
                case 0x4344_4154:
                    data = offset;
                    break;
                case 0x4544_4745:
                    edges = offset;
                    break;
            }
        }

        if (fanout == 0 ||
            names == 0 ||
            data == 0 ||
            fanout + 256 * 4 > file.Length)
        {
            return null;
        }

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.Span(fanout + 255 * 4, 4));
        if (names + (long)count * ObjectId.Length > file.Length ||
            data + (long)count * dataWidth > file.Length)
        {
            return null;
        }

        return new(file, start, count, fanout, names, data, edges);
    }

    public bool TryFind(ObjectId id, out int position)
    {
        var first = id.FirstByte;
        foreach (var layer in layers)
        {
            var low = 0;
            if (first > 0)
            {
                low = (int)BinaryPrimitives.ReadUInt32BigEndian(layer.File.Span(layer.Fanout + (first - 1) * 4, 4));
            }

            var high = (int)BinaryPrimitives.ReadUInt32BigEndian(layer.File.Span(layer.Fanout + first * 4, 4));
            var names = layer.File.Span(layer.Names, layer.Count * ObjectId.Length);
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                var compared = id.CompareTo(names.Slice(middle * ObjectId.Length, ObjectId.Length));
                if (compared == 0)
                {
                    position = layer.Start + middle;
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
        }

        position = -1;
        return false;
    }

    // The layer a position is in: one TryFind gave, or a parent Parents checked.
    (Layer Layer, int Local) Locate(int position)
    {
        var at = 0;
        while (position >= layers[at].Start + layers[at].Count)
        {
            at++;
        }

        return (layers[at], position - layers[at].Start);
    }

    public ObjectId IdAt(int position)
    {
        var (layer, local) = Locate(position);
        return new(layer.File.Span(layer.Names + (long)local * ObjectId.Length, ObjectId.Length));
    }

    ByteSpan DataAt(int position, out Layer layer)
    {
        (layer, var local) = Locate(position);
        return layer.File.Span(layer.Data + (long)local * dataWidth, dataWidth);
    }

    public ObjectId TreeAt(int position) =>
        new(DataAt(position, out _)[..ObjectId.Length]);

    /// <summary>
    /// The commit's committer date: 34 bits, after 30 of its generation.
    /// </summary>
    public long DateAt(int position)
    {
        var data = DataAt(position, out _);
        var high = BinaryPrimitives.ReadUInt32BigEndian(data[(ObjectId.Length + 8)..]) & 3;
        var low = BinaryPrimitives.ReadUInt32BigEndian(data[(ObjectId.Length + 12)..]);
        return (long)high << 32 | low;
    }

    /// <summary>
    /// The commit's parents, by position, in order: the first two in its data, any more, an
    /// octopus merge's, in the edges after, the last marked.
    /// </summary>
    public void Parents(int position, List<int> into)
    {
        var data = DataAt(position, out var layer);
        var first = BinaryPrimitives.ReadUInt32BigEndian(data[ObjectId.Length..]);
        var second = BinaryPrimitives.ReadUInt32BigEndian(data[(ObjectId.Length + 4)..]);
        if (first == parentNone)
        {
            return;
        }

        Add(into, first);
        if (second == parentNone)
        {
            return;
        }

        if ((second & extraEdges) == 0)
        {
            Add(into, second);
            return;
        }

        if (layer.Edges == 0)
        {
            throw new GitFilesException("A commit graph is missing its edges");
        }

        var edge = (long)(second & 0x7fff_ffff);
        while (true)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(layer.File.Span(layer.Edges + edge * 4, 4));
            Add(into, value & 0x7fff_ffff);
            if ((value & 0x8000_0000) != 0)
            {
                return;
            }

            edge++;
        }
    }

    void Add(List<int> into, uint parent)
    {
        if (parent >= Count)
        {
            throw new GitFilesException("A commit graph names a commit it hasn't got");
        }

        into.Add((int)parent);
    }

    public void Dispose()
    {
        foreach (var layer in layers)
        {
            layer.Dispose();
        }
    }
}
