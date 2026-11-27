/// <summary>
/// Which files added were renamed from files deleted, as git's diffcore-rename finds them: the
/// same content first, then a file of the same name elsewhere at least three quarters alike, then
/// any at least half alike, the most alike first. How alike two files are is git's estimate: how
/// much of the one is in the other, by the lines, or 64 byte runs, they are made of.
/// </summary>
sealed class Renames(ObjectDatabase objects)
{
    public const int MaxScore = 60_000;
    const int minimumScore = 30_000;
    const int candidatesPerDestination = 4;
    const int hashBase = 107_927;

    public readonly record struct File(byte[] Path, int Mode, ObjectId Id);

    readonly Dictionary<ObjectId, long> sizes = [];
    readonly Dictionary<ObjectId, (uint[] Hashes, uint[] Counts)> hashes = [];

    /// <summary>
    /// For each of <paramref name="destinations"/>, the index of the source it was renamed from,
    /// or -1. Each source is renamed to one destination at most. Past <paramref name="limit"/>
    /// squared pairs to compare, only renames of the same content are looked for, as git does.
    /// Both lists are in the order of their paths.
    /// </summary>
    public int[] Find(IReadOnlyList<File> sources, IReadOnlyList<File> destinations, int limit)
    {
        var found = new int[destinations.Count];
        Array.Fill(found, -1);
        if (sources.Count == 0 ||
            destinations.Count == 0)
        {
            return found;
        }

        var used = new bool[sources.Count];
        FindExact(sources, destinations, found, used);
        FindByName(sources, destinations, found, used);

        var remainingSources = Enumerable.Range(0, sources.Count).Where(_ => !used[_]).ToList();
        var remainingDestinations = Enumerable.Range(0, destinations.Count).Where(_ => found[_] < 0).ToList();
        if (remainingSources.Count == 0 ||
            remainingDestinations.Count == 0)
        {
            return found;
        }

        if (limit > 0 &&
            (long)remainingSources.Count * remainingDestinations.Count > (long)limit * limit)
        {
            return found;
        }

        var matrix = new List<(int Score, int NameScore, int Destination, int Source)>();
        foreach (var destination in remainingDestinations)
        {
            var best = new (int Score, int NameScore, int Destination, int Source)[candidatesPerDestination];
            for (var index = 0; index < best.Length; index++)
            {
                best[index] = (0, 0, -1, -1);
            }

            foreach (var source in remainingSources)
            {
                var candidate = (
                    Estimate(sources[source], destinations[destination]),
                    SameName(sources[source].Path, destinations[destination].Path),
                    destination,
                    source);
                KeepIfBetter(best, candidate);
            }

            matrix.AddRange(best);
        }

        // OrderBy is stable, as git's sort of them is.
        var sorted = matrix
            .OrderBy(_ => _, Comparer<(int Score, int NameScore, int Destination, int Source)>.Create(ScoreCompare))
            .ToList();
        foreach (var (score, _, destination, source) in sorted)
        {
            if (destination < 0 ||
                score < minimumScore)
            {
                break;
            }

            if (found[destination] >= 0 ||
                used[source])
            {
                continue;
            }

            found[destination] = source;
            used[source] = true;
        }

        return found;
    }

    // As git's find_exact_renames: each destination, in order, from the first source not yet
    // used with the same content, one of the same name first. Other than files, the modes must
    // match too.
    static void FindExact(IReadOnlyList<File> sources, IReadOnlyList<File> destinations, int[] found, bool[] used)
    {
        var byId = new Dictionary<ObjectId, List<int>>();
        for (var index = 0; index < sources.Count; index++)
        {
            if (!byId.TryGetValue(sources[index].Id, out var list))
            {
                list = [];
                byId[sources[index].Id] = list;
            }

            list.Add(index);
        }

        for (var destination = 0; destination < destinations.Count; destination++)
        {
            var target = destinations[destination];
            if (!byId.TryGetValue(target.Id, out var candidates))
            {
                continue;
            }

            var best = -1;
            var bestScore = -1;
            var tries = 100;
            foreach (var source in candidates)
            {
                var file = sources[source];
                if ((!IsRegular(file.Mode) || !IsRegular(target.Mode)) &&
                    file.Mode != target.Mode)
                {
                    continue;
                }

                if (used[source])
                {
                    continue;
                }

                var score = 1 + SameName(file.Path, target.Path);
                if (score > bestScore)
                {
                    best = source;
                    bestScore = score;
                    if (score == 2)
                    {
                        break;
                    }
                }

                if (--tries == 0)
                {
                    break;
                }
            }

            if (best >= 0)
            {
                found[destination] = best;
                used[best] = true;
            }
        }
    }

    // As git's find_basename_matches: a source and a destination whose name, without its folder,
    // no other source or destination left has, renamed where they are alike enough.
    void FindByName(IReadOnlyList<File> sources, IReadOnlyList<File> destinations, int[] found, bool[] used)
    {
        const int byNameScore = minimumScore + (MaxScore - minimumScore) / 2;
        var sourceNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Count; index++)
        {
            if (used[index])
            {
                continue;
            }

            var name = BaseName(sources[index].Path);
            if (!sourceNames.TryAdd(name, index))
            {
                sourceNames[name] = -1;
            }
        }

        var destinationNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < destinations.Count; index++)
        {
            if (found[index] >= 0)
            {
                continue;
            }

            var name = BaseName(destinations[index].Path);
            if (!destinationNames.TryAdd(name, index))
            {
                destinationNames[name] = -1;
            }
        }

        for (var source = 0; source < sources.Count; source++)
        {
            if (used[source])
            {
                continue;
            }

            var name = BaseName(sources[source].Path);
            if (sourceNames[name] != source ||
                !destinationNames.TryGetValue(name, out var destination) ||
                destination < 0 ||
                found[destination] >= 0)
            {
                continue;
            }

            if (Estimate(sources[source], destinations[destination]) < byNameScore)
            {
                continue;
            }

            found[destination] = source;
            used[source] = true;
        }
    }

    static string BaseName(byte[] path)
    {
        var slash = path.AsSpan().LastIndexOf((byte)'/');
        return Encoding.UTF8.GetString(path.AsSpan(slash + 1));
    }

    static bool IsRegular(int mode) =>
        (mode & Trees.FileType) == Trees.Regular;

    // As git's basename_same: 1 where both have the same name after their last slash.
    static int SameName(byte[] first, byte[] second)
    {
        var i = first.Length;
        var j = second.Length;
        while (i > 0 &&
               j > 0)
        {
            var a = first[--i];
            var b = second[--j];
            if (a != b)
            {
                return 0;
            }

            if (a == '/')
            {
                return 1;
            }
        }

        if ((i == 0 || first[i - 1] == '/') &&
            (j == 0 || second[j - 1] == '/'))
        {
            return 1;
        }

        return 0;
    }

    static int ScoreCompare((int Score, int NameScore, int Destination, int Source) first, (int Score, int NameScore, int Destination, int Source) second)
    {
        if (first.Destination < 0)
        {
            if (second.Destination >= 0)
            {
                return 1;
            }

            return 0;
        }

        if (second.Destination < 0)
        {
            return -1;
        }

        if (first.Score == second.Score)
        {
            return second.NameScore - first.NameScore;
        }

        return second.Score - first.Score;
    }

    static void KeepIfBetter((int Score, int NameScore, int Destination, int Source)[] best, (int Score, int NameScore, int Destination, int Source) candidate)
    {
        var worst = 0;
        for (var index = 1; index < best.Length; index++)
        {
            if (ScoreCompare(best[index], best[worst]) > 0)
            {
                worst = index;
            }
        }

        if (ScoreCompare(best[worst], candidate) > 0)
        {
            best[worst] = candidate;
        }
    }

    // As git's estimate_similarity: only files, and none whose sizes are too far apart to be half
    // alike; then how much of the destination is copied from the source, against the bigger.
    int Estimate(File source, File destination)
    {
        if (!IsRegular(source.Mode) ||
            !IsRegular(destination.Mode))
        {
            return 0;
        }

        var sourceSize = Size(source.Id);
        var destinationSize = Size(destination.Id);
        var maxSize = Math.Max(sourceSize, destinationSize);
        var baseSize = Math.Min(sourceSize, destinationSize);
        var deltaSize = maxSize - baseSize;

        // Two empty files are the same, so renamed already, by their ids; this is for none.
        if (maxSize == 0 ||
            maxSize * (MaxScore - minimumScore) < deltaSize * MaxScore)
        {
            return 0;
        }

        var copied = Copied(Hashes(source.Id), Hashes(destination.Id));
        return (int)(copied * MaxScore / maxSize);
    }

    long Size(ObjectId id)
    {
        if (sizes.TryGetValue(id, out var size))
        {
            return size;
        }

        size = objects.TryReadHeader(id)?.Size ?? throw new GitFilesException($"Object {id} is missing");
        sizes[id] = size;
        return size;
    }

    (uint[] Hashes, uint[] Counts) Hashes(ObjectId id)
    {
        if (hashes.TryGetValue(id, out var known))
        {
            return known;
        }

        known = SpanHashes(objects.Read(id, GitObjectType.Blob));
        hashes[id] = known;
        return known;
    }

    /// <summary>
    /// As git's hash_chars: the content cut after each line break, or 64 bytes, a carriage return
    /// before a line break left out of text, each run hashed, and how many bytes have each hash.
    /// In order of the hashes.
    /// </summary>
    internal static (uint[] Hashes, uint[] Counts) SpanHashes(ByteSpan data)
    {
        // As git's buffer_is_binary: a NUL in the first 8,000 bytes.
        var text = data[..Math.Min(8000, data.Length)].IndexOf((byte)0) < 0;
        var counts = new Dictionary<uint, uint>();
        uint first = 0;
        uint second = 0;
        var length = 0;
        for (var index = 0; index < data.Length; index++)
        {
            uint c = data[index];
            var old = first;
            if (text &&
                c == '\r' &&
                index + 1 < data.Length &&
                data[index + 1] == '\n')
            {
                continue;
            }

            first = (first << 7) ^ (second >> 25);
            second = (second << 7) ^ (old >> 25);
            first += c;
            if (++length < 64 &&
                c != '\n')
            {
                continue;
            }

            Add(counts, (first + second * 0x61) % hashBase, length);
            length = 0;
            first = 0;
            second = 0;
        }

        if (length > 0)
        {
            Add(counts, (first + second * 0x61) % hashBase, length);
        }

        var keys = counts.Keys.ToArray();
        Array.Sort(keys);
        var values = new uint[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            values[index] = counts[keys[index]];
        }

        return (keys, values);

        static void Add(Dictionary<uint, uint> counts, uint hash, int length)
        {
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, hash, out _);
            count += (uint)length;
        }
    }

    // As git's diffcore_count_changes: how many bytes of the destination's runs the source has.
    static long Copied((uint[] Hashes, uint[] Counts) source, (uint[] Hashes, uint[] Counts) destination)
    {
        long copied = 0;
        var d = 0;
        for (var s = 0; s < source.Hashes.Length; s++)
        {
            while (d < destination.Hashes.Length &&
                   destination.Hashes[d] < source.Hashes[s])
            {
                d++;
            }

            var sourceCount = source.Counts[s];
            uint destinationCount = 0;
            if (d < destination.Hashes.Length &&
                destination.Hashes[d] == source.Hashes[s])
            {
                destinationCount = destination.Counts[d];
                d++;
            }

            copied += Math.Min(sourceCount, destinationCount);
        }

        return copied;
    }
}
