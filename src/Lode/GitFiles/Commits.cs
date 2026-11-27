/// <summary>
/// A commit as its object has it: its tree and parents, then who made it and committed it, then
/// any other headers, a blank line, and its message.
/// </summary>
sealed class CommitInfo
{
    readonly byte[] data;
    readonly int authorStart;
    readonly int authorEnd;
    readonly int messageStart;

    public ObjectId Tree { get; }
    public ObjectId[] Parents { get; }
    public long CommitterDate { get; }

    /// <summary>
    /// What its encoding header names, or null where it has none, for UTF-8.
    /// </summary>
    public string? Encoding { get; }

    public CommitInfo(byte[] data)
    {
        this.data = data;
        var text = data.AsSpan();
        var end = text.IndexOf("\n\n"u8);
        if (end < 0)
        {
            end = text.Length;
            messageStart = text.Length;
        }
        else
        {
            messageStart = end + 2;
        }

        var parents = new List<ObjectId>(2);
        var position = 0;
        var sawTree = false;
        var lineNumber = 0;
        var afterParents = -1;
        while (position < end)
        {
            var lineEnd = text[position..end].IndexOf((byte)'\n');
            if (lineEnd < 0)
            {
                lineEnd = end;
            }
            else
            {
                lineEnd += position;
            }

            var line = text[position..lineEnd];
            if (lineNumber == 0)
            {
                if (!line.StartsWith("tree "u8) ||
                    !ObjectId.TryParse(line[5..], out var tree))
                {
                    throw new GitFilesException("A commit is corrupt");
                }

                Tree = tree;
                sawTree = true;
            }
            else if (afterParents < 0 &&
                     line.StartsWith("parent "u8))
            {
                if (!ObjectId.TryParse(line[7..], out var parent))
                {
                    throw new GitFilesException("A commit is corrupt");
                }

                parents.Add(parent);
            }
            else
            {
                if (afterParents < 0)
                {
                    afterParents = position;
                }

                if (line.StartsWith("author "u8) &&
                    authorEnd == 0)
                {
                    authorStart = position + 7;
                    authorEnd = lineEnd;
                }
                else if (line.StartsWith("encoding "u8))
                {
                    Encoding = System.Text.Encoding.UTF8.GetString(line[9..]);
                }
            }

            lineNumber++;
            position = lineEnd + 1;
        }

        if (!sawTree)
        {
            throw new GitFilesException("A commit is corrupt");
        }

        Parents = parents.ToArray();
        if (afterParents < 0)
        {
            afterParents = end;
        }

        CommitterDate = ParseCommitDate(text[afterParents..end]);
    }

    // As git's parse_commit_date: the author line, then the committer's, its date after the last >.
    static long ParseCommitDate(ByteSpan header)
    {
        if (!header.StartsWith("author"u8))
        {
            return 0;
        }

        var newline = header.IndexOf((byte)'\n');
        if (newline < 0)
        {
            return 0;
        }

        var rest = header[(newline + 1)..];
        if (!rest.StartsWith("committer"u8))
        {
            return 0;
        }

        var eol = rest.IndexOf((byte)'\n');
        if (eol < 0)
        {
            eol = rest.Length;
        }

        var line = rest[..eol];
        var bracket = line.LastIndexOf((byte)'>');
        if (bracket < 0)
        {
            return 0;
        }

        return Commits.ParseNumber(line[(bracket + 1)..]);
    }

    /// <summary>
    /// The author's name, as git log's %an has it, without a .mailmap: where the commit has no
    /// author it can read, "%aN" itself, as git log leaves it.
    /// </summary>
    public string AuthorName
    {
        get
        {
            if (authorEnd == 0 ||
                Commits.IdentName(data.AsSpan(authorStart, authorEnd - authorStart)) is not { } name)
            {
                return "%aN";
            }

            return name;
        }
    }

    /// <summary>
    /// The author's email, between the angle brackets, for a .mailmap to map by.
    /// </summary>
    public string AuthorEmail
    {
        get
        {
            if (authorEnd == 0)
            {
                return "";
            }

            var ident = data.AsSpan(authorStart, authorEnd - authorStart);
            var left = ident.IndexOf((byte)'<');
            if (left < 0)
            {
                return "";
            }

            var right = ident[(left + 1)..].IndexOf((byte)'>');
            if (right < 0)
            {
                return "";
            }

            return System.Text.Encoding.UTF8.GetString(ident.Slice(left + 1, right));
        }
    }

    public long AuthorDate
    {
        get
        {
            if (authorEnd == 0)
            {
                return 0;
            }

            return Commits.IdentDate(data.AsSpan(authorStart, authorEnd - authorStart));
        }
    }

    /// <summary>
    /// As git log's %s: blank lines at the start passed over, then the first paragraph's lines,
    /// each without the whitespace it ends with, joined by a space.
    /// </summary>
    public string Subject
    {
        get
        {
            var message = data.AsSpan(messageStart);
            var builder = new StringBuilder();
            var first = true;
            var skipping = true;
            while (message.Length > 0)
            {
                var length = message.IndexOf((byte)'\n');
                var lineLength = length;
                if (length < 0)
                {
                    length = message.Length;
                    lineLength = length;
                }
                else
                {
                    length++;
                }

                var line = message[..lineLength];
                var trimmed = TrimEnd(line);
                message = message[length..];
                if (trimmed.Length == 0)
                {
                    if (skipping)
                    {
                        continue;
                    }

                    break;
                }

                skipping = false;
                if (!first)
                {
                    builder.Append(' ');
                }

                builder.Append(System.Text.Encoding.UTF8.GetString(trimmed));
                first = false;
            }

            return builder.ToString();
        }
    }

    static ByteSpan TrimEnd(ByteSpan line)
    {
        var length = line.Length;
        while (length > 0 &&
               Commits.IsSpace(line[length - 1]))
        {
            length--;
        }

        return line[..length];
    }
}

/// <summary>
/// Commits walked as git walks them: by committer date, newest first, those of the same date in
/// the order found, each once. Read from the commit graph where it has them, otherwise from their
/// objects. Each walk is made for one question, its marks its own.
/// </summary>
sealed class Commits
{
    readonly GitRepository repository;
    readonly CommitGraph? graph;
    readonly HashSet<ObjectId>? shallow;
    readonly Dictionary<ObjectId, int> byId = [];
    int[]? byPosition;
    CommitNode[] nodes = new CommitNode[64];
    int count;
    readonly List<int> graphParents = [];

    struct CommitNode
    {
        public ObjectId Id;
        public long Date;
        public int Position;
        public int[]? Parents;
        public CommitInfo? Info;
        public uint Flags;
    }

    public Commits(GitRepository repository)
    {
        this.repository = repository;
        repository.CheckHistoryIsAsWritten();
        graph = repository.Graph;
        shallow = repository.Shallow;
    }


    public ObjectId IdOf(int node) =>
        nodes[node].Id;

    public long DateOf(int node) =>
        nodes[node].Date;

    public ref uint Flags(int node) =>
        ref nodes[node].Flags;

    /// <summary>
    /// Every mark taken off, for the next walk, the commits already read kept.
    /// </summary>
    public void ClearFlags()
    {
        for (var node = 0; node < count; node++)
        {
            nodes[node].Flags = 0;
        }
    }

    /// <summary>
    /// The commit, which must be one: what a ref names, peeled.
    /// </summary>
    public int Node(ObjectId id)
    {
        if (byId.TryGetValue(id, out var node))
        {
            return node;
        }

        if (graph != null &&
            graph.TryFind(id, out var position))
        {
            return AddFromGraph(id, position);
        }

        var info = new CommitInfo(repository.Objects.Read(id, GitObjectType.Commit));
        node = Add(id, info.CommitterDate, -1);
        nodes[node].Info = info;
        return node;
    }

    int NodeAt(int position)
    {
        var commitGraph = graph!;
        byPosition ??= new int[commitGraph.Count];
        var mapped = byPosition[position];
        if (mapped > 0)
        {
            return mapped - 1;
        }

        return AddFromGraph(commitGraph.IdAt(position), position);
    }

    int AddFromGraph(ObjectId id, int position)
    {
        var node = Add(id, graph!.DateAt(position), position);
        byPosition ??= new int[graph.Count];
        byPosition[position] = node + 1;
        return node;
    }

    int Add(ObjectId id, long date, int position)
    {
        if (count == nodes.Length)
        {
            Array.Resize(ref nodes, count * 2);
        }

        var node = count++;
        nodes[node] = new()
        {
            Id = id,
            Date = date,
            Position = position
        };
        byId[id] = node;
        return node;
    }

    /// <summary>
    /// The commit's parents, in order. None for a shallow clone's commit cut off there.
    /// </summary>
    public int[] Parents(int node)
    {
        if (nodes[node].Parents is { } known)
        {
            return known;
        }

        int[] parents;
        if (nodes[node].Position >= 0)
        {
            graphParents.Clear();
            graph!.Parents(nodes[node].Position, graphParents);
            parents = new int[graphParents.Count];
            for (var index = 0; index < parents.Length; index++)
            {
                parents[index] = NodeAt(graphParents[index]);
            }
        }
        else if (shallow != null &&
                 shallow.Contains(nodes[node].Id))
        {
            parents = [];
        }
        else
        {
            var ids = nodes[node].Info!.Parents;
            parents = new int[ids.Length];
            for (var index = 0; index < ids.Length; index++)
            {
                parents[index] = Node(ids[index]);
            }
        }

        nodes[node].Parents = parents;
        return parents;
    }

    /// <summary>
    /// The commit as its object has it, author and message and all, read once.
    /// </summary>
    public CommitInfo Info(int node) =>
        nodes[node].Info ??= new(repository.Objects.Read(nodes[node].Id, GitObjectType.Commit));

    /// <summary>
    /// The commits from <paramref name="start"/> back, as git log lists them, after the first
    /// <paramref name="skip"/>.
    /// </summary>
    public List<int> Log(int start, int skip, int take)
    {
        const uint seen = 1;
        var found = new List<int>(Math.Min(take, 1024));
        var queue = new DateQueue(this);
        Flags(start) |= seen;
        queue.Push(start);
        var index = 0;
        while (found.Count < take &&
               queue.TryPop(out var node))
        {
            if (index++ >= skip)
            {
                found.Add(node);
            }

            foreach (var parent in Parents(node))
            {
                if ((Flags(parent) & seen) == 0)
                {
                    Flags(parent) |= seen;
                    queue.Push(parent);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The commits <paramref name="left"/> has that <paramref name="right"/> hasn't, and those
    /// <paramref name="right"/> has that <paramref name="left"/> hasn't: what git counts as ahead
    /// and behind. Painted down from both by date until all that is left to look at both have,
    /// as git does.
    /// </summary>
    public (List<int> LeftOnly, List<int> RightOnly) Divergence(int left, int right)
    {
        const uint leftFlag = 1;
        const uint rightFlag = 2;
        const uint stale = 4;
        var done = new Dictionary<int, uint>();
        var queue = new DateQueue(this);
        var interesting = 0;
        Flags(left) |= leftFlag;
        Flags(right) |= rightFlag;
        queue.Push(left, 1);
        queue.Push(right, 1);
        interesting += 2;
        while (interesting > 0 &&
               queue.TryPop(out var node, out var counted))
        {
            interesting -= counted;
            var flags = Flags(node);
            if (done.TryGetValue(node, out var already) &&
                already == flags)
            {
                continue;
            }

            if ((flags & (leftFlag | rightFlag)) == (leftFlag | rightFlag))
            {
                flags |= stale;
                Flags(node) = flags;
            }

            done[node] = flags;
            foreach (var parent in Parents(node))
            {
                ref var parentFlags = ref Flags(parent);
                if ((parentFlags & flags) == flags)
                {
                    continue;
                }

                parentFlags |= flags;
                var interestingParent = 0;
                if ((parentFlags & stale) == 0)
                {
                    interestingParent = 1;
                }

                interesting += interestingParent;
                queue.Push(parent, interestingParent);
            }
        }

        var leftOnly = new List<int>();
        var rightOnly = new List<int>();
        for (var node = 0; node < count; node++)
        {
            var flags = nodes[node].Flags & (leftFlag | rightFlag);
            if (flags == leftFlag)
            {
                leftOnly.Add(node);
            }
            else if (flags == rightFlag)
            {
                rightOnly.Add(node);
            }
        }

        return (leftOnly, rightOnly);
    }

    /// <summary>
    /// As git describe --tags --abbrev=0 finds it: the commit's own tag, otherwise, of the first
    /// ten tags found walking back by date, the one fewest commits back that it doesn't have in
    /// its history, the first found of those as far. Null where none is reached.
    /// </summary>
    public string? Describe(int start, IReadOnlyDictionary<ObjectId, (string Name, bool Annotated)> names)
    {
        const uint seen = 1;
        const int maxCandidates = 10;
        if (names.TryGetValue(IdOf(start), out var own))
        {
            return own.Name;
        }

        var candidates = new List<(string Name, int Depth, uint Within, int Order)>();
        var queue = new DateQueue(this);
        Flags(start) |= seen;
        queue.Push(start);
        var seenCommits = 0;
        var annotated = 0;
        while (queue.TryPop(out var node))
        {
            seenCommits++;
            if (names.TryGetValue(IdOf(node), out var name))
            {
                if (candidates.Count >= maxCandidates)
                {
                    break;
                }

                var within = 1u << (candidates.Count + 1);
                Flags(node) |= within;
                candidates.Add((name.Name, seenCommits - 1, within, candidates.Count + 1));
                if (name.Annotated)
                {
                    annotated++;
                }
            }

            var flags = Flags(node);
            for (var index = 0; index < candidates.Count; index++)
            {
                if ((flags & candidates[index].Within) == 0)
                {
                    var candidate = candidates[index];
                    candidates[index] = candidate with
                    {
                        Depth = candidate.Depth + 1
                    };
                }
            }

            if (annotated > 0 &&
                queue.Count == 0)
            {
                break;
            }

            foreach (var parent in Parents(node))
            {
                if ((Flags(parent) & seen) == 0)
                {
                    queue.Push(parent);
                }

                Flags(parent) |= flags;
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(_ => _.Depth)
            .ThenBy(_ => _.Order)
            .First()
            .Name;
    }

    /// <summary>
    /// A number as git's parse_timestamp reads one, whitespace before it passed over.
    /// </summary>
    internal static long ParseNumber(ByteSpan text)
    {
        var position = 0;
        while (position < text.Length &&
               IsSpace(text[position]))
        {
            position++;
        }

        long value = 0;
        var any = false;
        while (position < text.Length &&
               text[position] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (text[position] - '0');
            position++;
            any = true;
        }

        if (!any)
        {
            return 0;
        }

        return value;
    }

    internal static bool IsSpace(byte c) =>
        c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    /// <summary>
    /// The name of "Name &lt;mail&gt; 1234567890 +0000", as git's split_ident_line has it: up to
    /// the first &lt;, without the whitespace before it. Null for one without both brackets,
    /// which git can't split.
    /// </summary>
    internal static string? IdentName(ByteSpan ident)
    {
        var bracket = ident.IndexOf((byte)'<');
        if (bracket < 0 ||
            ident[bracket..].IndexOf((byte)'>') < 0)
        {
            return null;
        }

        var end = bracket;
        while (end > 0 &&
               IsSpace(ident[end - 1]))
        {
            end--;
        }

        return Encoding.UTF8.GetString(ident[..end]);
    }

    /// <summary>
    /// The date of an ident, the digits after its last &gt;. 0 where it has none.
    /// </summary>
    internal static long IdentDate(ByteSpan ident)
    {
        var bracket = ident.LastIndexOf((byte)'>');
        if (bracket < 0)
        {
            return 0;
        }

        return ParseNumber(ident[(bracket + 1)..]);
    }

    /// <summary>
    /// Commits newest first by committer date, those of one date in the order they were put in,
    /// as git's commit list by date has them. Each entry may carry a count, for the walk to know
    /// how many still waiting are of interest.
    /// </summary>
    sealed class DateQueue(Commits commits)
    {
        (long Date, long Order, int Node, int Counted)[] heap = new (long, long, int, int)[64];
        long order;

        public int Count { get; private set; }

        public void Push(int node, int counted = 0)
        {
            if (Count == heap.Length)
            {
                Array.Resize(ref heap, Count * 2);
            }

            var entry = (commits.DateOf(node), order++, node, counted);
            var index = Count++;
            while (index > 0)
            {
                var parent = (index - 1) >> 1;
                if (!Before(entry, heap[parent]))
                {
                    break;
                }

                heap[index] = heap[parent];
                index = parent;
            }

            heap[index] = entry;
        }

        static bool Before((long Date, long Order, int Node, int Counted) first, (long Date, long Order, int Node, int Counted) second)
        {
            if (first.Date != second.Date)
            {
                return first.Date > second.Date;
            }

            return first.Order < second.Order;
        }

        public bool TryPop(out int node) =>
            TryPop(out node, out _);

        public bool TryPop(out int node, out int counted)
        {
            if (Count == 0)
            {
                node = -1;
                counted = 0;
                return false;
            }

            var top = heap[0];
            node = top.Node;
            counted = top.Counted;
            var last = heap[--Count];
            var index = 0;
            while (true)
            {
                var child = index * 2 + 1;
                if (child >= Count)
                {
                    break;
                }

                if (child + 1 < Count &&
                    Before(heap[child + 1], heap[child]))
                {
                    child++;
                }

                if (!Before(heap[child], last))
                {
                    break;
                }

                heap[index] = heap[child];
                index = child;
            }

            if (Count > 0)
            {
                heap[index] = last;
            }

            return true;
        }
    }
}
