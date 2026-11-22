/// <summary>
/// The long-running commands started from Lode (git, NuGet updates), running or finished.
/// Every window's actions panel shows the same list. Used only on the UI thread.
/// </summary>
static class ActionLog
{
    public enum State
    {
        Running,
        Succeeded,
        Failed
    }

    /// <summary>
    /// How a command ended: a short summary for the panel, and everything it reported.
    /// </summary>
    public sealed record Outcome(bool Succeeded, string Summary, string Details)
    {
        /// <summary>
        /// Whether it stays in the list until cleared, though it went well: what it reported is
        /// something to come back for, the commit a deleted branch was at say.
        /// </summary>
        internal bool Stays { get; init; }
    }

    /// <param name="folders">What it acts on: one folder, or several selected together.</param>
    public sealed class Entry(string title, IReadOnlyList<string> folders)
    {
        public string Title { get; } = title;
        public IReadOnlyList<string> Folders { get; } = folders;
        public State State { get; internal set; } = State.Running;
        public string Summary { get; internal set; } = "Running…";
        public string Details { get; internal set; } = "";

        /// <summary>
        /// When it finished, while it is still running none.
        /// </summary>
        public DateTimeOffset? Finished { get; internal set; }

        /// <summary>
        /// Whether it leaves the list a while after it finished: it went well, or was
        /// cancelled. One that failed stays until cleared, to be seen, as does one whose
        /// outcome says it <see cref="Outcome.Stays"/>.
        /// </summary>
        public bool AgesOut { get; internal set; }

        // Set for work that can be stopped.
        internal CancelSource? Cancellation { get; init; }

        /// <summary>
        /// Whether it is running and can be stopped, which <see cref="Cancel"/> does.
        /// </summary>
        public bool CanCancel =>
            State == State.Running &&
            Cancellation is { IsCancellationRequested: false };

        /// <summary>
        /// Asks the work to stop. It ends as failed, "Cancelled", with what it did till then.
        /// </summary>
        public void Cancel()
        {
            if (!CanCancel)
            {
                return;
            }

            Cancellation!.Cancel();
            Summary = "Cancelling…";
            Progressed?.Invoke(this);
        }

        /// <summary>
        /// The folder's own name, or the whole path for a drive root. Of several, both names,
        /// or the first's and how many more.
        /// </summary>
        public string FolderName
        {
            get
            {
                var first = Name(Folders[0]);
                if (Folders.Count == 1)
                {
                    return first;
                }

                if (Folders.Count == 2)
                {
                    return $"{first} and {Name(Folders[1])}";
                }

                return $"{first} and {Folders.Count - 1} more";
            }
        }

        /// <summary>
        /// The folder's whole path, or the names of every one of several.
        /// </summary>
        public string Where
        {
            get
            {
                if (Folders.Count == 1)
                {
                    return Folders[0];
                }

                return string.Join(", ", Folders.Select(Name));
            }
        }

        static string Name(string folder)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0)
            {
                return folder;
            }

            return name;
        }

        /// <summary>
        /// What, where, and how it went, on one line.
        /// </summary>
        public override string ToString() =>
            $"{Title} – {FolderName}: {Summary}";
    }

    static List<Entry> entries = [];

    public static IReadOnlyList<Entry> Entries => entries;

    /// <summary>
    /// Raised on the UI thread whenever an entry is added, finishes, or is cleared.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// Raised on the UI thread when a running entry reports how far it has got, in its
    /// <see cref="Entry.Summary"/>.
    /// </summary>
    public static event Action<Entry>? Progressed;

    public static Task<Entry> Run(string title, string folder, Func<Task<Outcome>> work) =>
        Run(title, [folder], _ => work());

    public static Task<Entry> Run(string title, string folder, Func<IProgress<string>, Task<Outcome>> work) =>
        Run(title, [folder], work);

    /// <summary>
    /// Adds a running entry, then records how the work ended. Never throws: a failure becomes
    /// a failed entry. The work may report, from any thread, how far it has got; that is the
    /// entry's summary until it ends.
    /// </summary>
    public static Task<Entry> Run(string title, IReadOnlyList<string> folders, Func<IProgress<string>, Task<Outcome>> work) =>
        Run(title, folders, null, (progress, _) => work(progress));

    /// <summary>
    /// As <see cref="Run(string, IReadOnlyList{string}, Func{IProgress{string}, Task{Outcome}})"/>,
    /// for work that stops when the token it is given is cancelled, which the entry's
    /// <see cref="Entry.Cancel"/> does.
    /// </summary>
    public static Task<Entry> Run(string title, IReadOnlyList<string> folders, Func<IProgress<string>, Cancel, Task<Outcome>> work) =>
        Run(title, folders, new(), work);

    static async Task<Entry> Run(string title, IReadOnlyList<string> folders, CancelSource? cancellation, Func<IProgress<string>, Cancel, Task<Outcome>> work)
    {
        var entry = new Entry(title, folders)
        {
            Cancellation = cancellation
        };
        entries.Add(entry);
        Changed?.Invoke();

        var cancel = cancellation?.Token ?? Cancel.None;
        Outcome outcome;
        try
        {
            outcome = await work(new EntryProgress(entry), cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            outcome = new(false, "Cancelled", "");
        }
        catch (Exception exception)
        {
            outcome = new(false, "Failed", exception.Message);
        }

        // What it did before it stopped is in the details.
        var cancelled = cancel.IsCancellationRequested;
        if (cancelled)
        {
            outcome = outcome with
            {
                Succeeded = false,
                Summary = "Cancelled"
            };
        }

        cancellation?.Dispose();

        entry.State = outcome.Succeeded ? State.Succeeded : State.Failed;
        entry.Summary = outcome.Summary;
        entry.Details = outcome.Details;
        entry.Finished = Time.GetUtcNow();
        entry.AgesOut = (outcome.Succeeded || cancelled) &&
                        !outcome.Stays;
        if (entry.AgesOut)
        {
            ageing.Start();
        }

        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// How long an entry that went well, or was cancelled, stays once it has finished, so the
    /// list needn't be cleared by hand.
    /// </summary>
    internal static TimeSpan AgeOutAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The time, which tests set.
    /// </summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    // Goes over the entries every few seconds while any are to leave, so each goes close to its
    // time.
    static Timer ageing = CreateAgeing();

    static Timer CreateAgeing()
    {
        var timer = new Timer
        {
            Interval = 5000
        };
        timer.Tick += (_, _) => AgeOut();
        return timer;
    }

    /// <summary>
    /// Takes out the entries that went well, or were cancelled, and finished
    /// <see cref="AgeOutAfter"/> ago or longer.
    /// </summary>
    internal static void AgeOut()
    {
        var now = Time.GetUtcNow();
        var removed = entries
            .RemoveAll(_ => _ is
                            {
                                AgesOut: true,
                                Finished: { } finished
                            } &&
                            now - finished >= AgeOutAfter);
        if (!entries.Any(_ => _.AgesOut))
        {
            ageing.Stop();
        }

        if (removed > 0)
        {
            Changed?.Invoke();
        }
    }

    // Created on the UI thread, so it can post reports made on other threads back to it.
    sealed class EntryProgress(Entry entry) : IProgress<string>
    {
        SynchronizationContext? context = SynchronizationContext.Current;

        public void Report(string value)
        {
            if (context == null)
            {
                Show(value);
                return;
            }

            context.Post(_ => Show(value), null);
        }

        // A report can arrive after the work has ended; the outcome stands. Or after it was
        // asked to stop, which the entry says until it has.
        void Show(string value)
        {
            if (entry.State != State.Running ||
                entry.Cancellation is { IsCancellationRequested: true })
            {
                return;
            }

            entry.Summary = value;
            Progressed?.Invoke(entry);
        }
    }

    /// <summary>
    /// What to say of the entries still running, before Lode exits, which stops them part way:
    /// a git command, an update to a props file, a purge deleting. Null when none are.
    /// </summary>
    public static string? RunningMessage()
    {
        var running = entries
            .Where(_ => _.State == State.Running)
            .ToList();
        if (running.Count == 0)
        {
            return null;
        }

        const int listed = 5;
        var lines = running
            .Take(listed)
            .Select(_ => $"  {_.Title} – {_.FolderName}");
        var heading = $"These {running.Count} are still running:";
        var them = "them";
        if (running.Count == 1)
        {
            heading = "This is still running:";
            them = "it";
        }

        var message = $"{heading}\n\n{string.Join("\n", lines)}";
        if (running.Count > listed)
        {
            message += $"\n  and {running.Count - listed} more";
        }

        return $"{message}\n\nExiting now stops {them} part way, which can leave files half written or half deleted.";
    }

    /// <summary>
    /// Removes the finished entries, leaving those still running.
    /// </summary>
    public static void ClearFinished()
    {
        entries.RemoveAll(_ => _.State != State.Running);
        Changed?.Invoke();
    }

    public static void Remove(Entry entry)
    {
        if (entry.State == State.Running)
        {
            return;
        }

        entries.Remove(entry);
        Changed?.Invoke();
    }

    /// <summary>
    /// Empties the log, for tests.
    /// </summary>
    internal static void Reset()
    {
        entries.Clear();
        ageing.Stop();
        Changed?.Invoke();
    }
}
