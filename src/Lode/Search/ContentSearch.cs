/// <summary>
/// Find in Files' search, as Sublime's and ripgrep's go about it: no index, every file read and
/// looked through as it is, a file to each processor at once. The walk (<see cref="SearchWalk"/>)
/// lists the files on a thread of its own; workers read and search them; and what each finds is
/// handed on in the walk's order, so the results come out the same every time, each folder's
/// files together, whichever worker finished first.
/// </summary>
sealed class ContentSearch
{
    /// <summary>
    /// The most lines kept, across every file. Past it, files are still counted, as are their
    /// matches, so Replace and the summary are of everything, but their lines aren't kept.
    /// </summary>
    public const int MostLines = 200_000;

    // How many files a worker is handed at once.
    const int batchSize = 64;

    /// <summary>
    /// How far a search has got. Counted on the workers' threads, read on any.
    /// </summary>
    public sealed class Counts
    {
        int searched;
        int matched;
        int matches;
        int binary;
        int tooLarge;
        int unreadable;
        int timedOut;
        int lines;

        public int Searched => Volatile.Read(ref searched);
        public int Matched => Volatile.Read(ref matched);
        public int Matches => Volatile.Read(ref matches);
        public int Binary => Volatile.Read(ref binary);
        public int TooLarge => Volatile.Read(ref tooLarge);
        public int Unreadable => Volatile.Read(ref unreadable);
        public int TimedOut => Volatile.Read(ref timedOut);
        public int Lines => Volatile.Read(ref lines);

        /// <summary>
        /// Lines were left out, having kept <see cref="MostLines"/>.
        /// </summary>
        public bool LinesLeftOut { get; internal set; }

        internal void Searching() => Interlocked.Increment(ref searched);

        internal void Skipped(TextFile.Skip skip)
        {
            switch (skip)
            {
                case TextFile.Skip.Binary:
                    Interlocked.Increment(ref binary);
                    break;
                case TextFile.Skip.TooLarge:
                    Interlocked.Increment(ref tooLarge);
                    break;
                default:
                    Interlocked.Increment(ref unreadable);
                    break;
            }
        }

        internal void TookTooLong() => Interlocked.Increment(ref timedOut);

        internal void Found(FileResult result)
        {
            Interlocked.Increment(ref matched);
            Interlocked.Add(ref matches, result.MatchCount);
            Interlocked.Add(ref lines, result.Lines.Count);
        }
    }

    /// <summary>
    /// Searches what <paramref name="where"/> says for what <paramref name="matcher"/> finds,
    /// adding each file it finds it in to <paramref name="results"/>, in the walk's order, as it
    /// goes. Done once every file has been searched; cancelled, it throws.
    /// </summary>
    /// <param name="list">Lists what git doesn't ignore in a repository.</param>
    public static async Task Run(
        FindQuery query,
        SearchWhere where,
        Matcher matcher,
        SystemItems? hiddenSystem,
        Func<string, GitScope?> list,
        ConcurrentQueue<FileResult> results,
        Counts counts,
        SearchWalk.Report report,
        Cancel cancel)
    {
        // Each on a thread of its own, the walk and the workers, as each waits on the disk, and
        // the walk on git: on the thread pool's, they kept it short of threads, and each waited
        // on the others. Handed over a run of files at a time, which are mostly of one folder,
        // so the workers are mostly in different folders: all opening files in the one folder
        // at once, they wait on each other.
        using var batches = new BlockingCollection<(int First, List<SearchWalk.File> Files)>(64);
        var order = new InOrder(results, counts);

        var walking = OwnThread(
            () =>
            {
                try
                {
                    var index = 0;
                    var batch = new List<SearchWalk.File>(batchSize);
                    foreach (var file in SearchWalk.Files(where, query, hiddenSystem, list, report, cancel))
                    {
                        batch.Add(file);
                        if (batch.Count == batchSize)
                        {
                            batches.Add((index, batch), cancel);
                            index += batch.Count;
                            batch = new(batchSize);
                        }
                    }

                    if (batch.Count > 0)
                    {
                        batches.Add((index, batch), cancel);
                    }
                }
                finally
                {
                    batches.CompleteAdding();
                }
            });

        var workers = Enumerable
            .Range(0, Math.Clamp(Environment.ProcessorCount, 2, 16))
            .Select(_ => OwnThread(
                () =>
                {
                    var matches = new List<(int Start, int Length)>();
                    var buffers = new TextFile.Buffers();
                    foreach (var (first, files) in batches.GetConsumingEnumerable(cancel))
                    {
                        for (var offset = 0; offset < files.Count; offset++)
                        {
                            order.Done(first + offset, Search(files[offset].Path, matcher, query.ContextLines, matches, buffers, counts));
                        }
                    }
                }))
            .ToList();

        await Task.WhenAll([walking, .. workers]);
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Runs work that waits, on the disk or on git, on a thread of its own.
    /// </summary>
    internal static Task OwnThread(Action work) =>
        Task.Factory.StartNew(work, Cancel.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// What one file holds of it, or null when nothing, or it couldn't be read.
    /// </summary>
    internal static FileResult? Search(string path, Matcher matcher, int context, List<(int Start, int Length)> matches, TextFile.Buffers buffers, Counts counts)
    {
        try
        {
            return SearchIn(path, matcher, context, matches, buffers, counts);
        }
        finally
        {
            buffers.Release();
        }
    }

    static FileResult? SearchIn(string path, Matcher matcher, int context, List<(int Start, int Length)> matches, TextFile.Buffers buffers, Counts counts)
    {
        counts.Searching();
        var text = TextFile.Read(path, buffers, out var skip, matcher);
        if (text == null)
        {
            if (skip != TextFile.Skip.None)
            {
                counts.Skipped(skip);
            }

            return null;
        }

        matches.Clear();
        try
        {
            matcher.Find(text.Text, matches);
        }
        catch (RegexMatchTimeoutException)
        {
            counts.TookTooLong();
            return null;
        }

        if (matches.Count == 0)
        {
            return null;
        }

        return FileResult.From(path, text.Text, matches, context);
    }

    // Hands on what each file found in the walk's order, holding what comes early till what is
    // before it is done, and leaving the lines out of what comes once enough are kept.
    sealed class InOrder(ConcurrentQueue<FileResult> results, Counts counts)
    {
        Lock gate = new();
        Dictionary<int, FileResult?> early = [];
        int next;
        int lines;

        public void Done(int index, FileResult? result)
        {
            lock (gate)
            {
                early[index] = result;
                while (early.Remove(next, out var due))
                {
                    next++;
                    if (due == null)
                    {
                        continue;
                    }

                    if (lines + due.Lines.Count > MostLines)
                    {
                        due = due with
                        {
                            Lines = [],
                            LinesLeftOut = true
                        };
                        counts.LinesLeftOut = true;
                    }

                    lines += due.Lines.Count;
                    counts.Found(due);
                    results.Enqueue(due);
                }
            }
        }
    }
}
