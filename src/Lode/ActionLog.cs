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
    public sealed record Outcome(bool Succeeded, string Summary, string Details);

    public sealed class Entry(string title, string folder)
    {
        public string Title { get; } = title;
        public string Folder { get; } = folder;
        public State State { get; internal set; } = State.Running;
        public string Summary { get; internal set; } = "Running…";
        public string Details { get; internal set; } = "";

        /// <summary>
        /// The folder's own name, or the whole path for a drive root.
        /// </summary>
        public string FolderName
        {
            get
            {
                var name = Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));
                if (name.Length == 0)
                {
                    return Folder;
                }

                return name;
            }
        }

        /// <summary>
        /// What, where, and how it went, on one line.
        /// </summary>
        public override string ToString() =>
            $"{Title} – {FolderName}: {Summary}";
    }

    static readonly List<Entry> entries = [];

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
        Run(title, folder, _ => work());

    /// <summary>
    /// Adds a running entry, then records how the work ended. Never throws: a failure becomes
    /// a failed entry. The work may report, from any thread, how far it has got; that is the
    /// entry's summary until it ends.
    /// </summary>
    public static async Task<Entry> Run(string title, string folder, Func<IProgress<string>, Task<Outcome>> work)
    {
        var entry = new Entry(title, folder);
        entries.Add(entry);
        Changed?.Invoke();

        Outcome outcome;
        try
        {
            outcome = await work(new EntryProgress(entry));
        }
        catch (Exception exception)
        {
            outcome = new(false, "Failed", exception.Message);
        }

        entry.State = outcome.Succeeded ? State.Succeeded : State.Failed;
        entry.Summary = outcome.Summary;
        entry.Details = outcome.Details;
        Changed?.Invoke();
        return entry;
    }

    // Created on the UI thread, so it can post reports made on other threads back to it.
    sealed class EntryProgress(Entry entry) : IProgress<string>
    {
        readonly SynchronizationContext? context = SynchronizationContext.Current;

        public void Report(string value)
        {
            if (context == null)
            {
                Show(value);
                return;
            }

            context.Post(_ => Show(value), null);
        }

        // A report can arrive after the work has ended; the outcome stands.
        void Show(string value)
        {
            if (entry.State != State.Running)
            {
                return;
            }

            entry.Summary = value;
            Progressed?.Invoke(entry);
        }
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
        Changed?.Invoke();
    }
}
