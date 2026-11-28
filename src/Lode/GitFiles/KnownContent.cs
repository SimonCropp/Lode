/// <summary>
/// The files of a repository the in-process reader found to be what is staged, though their
/// times weren't those staged: written again unchanged, by npm install, a formatter or a build
/// say. Git status would write what it found of them into the index, where git looks first, but
/// Lode runs it with --no-optional-locks and never takes git's lock itself, so nothing did, and
/// each was read and hashed whole on every read until something else wrote the index. Kept, by
/// repository, while its index and its config are as they were, and for each file while its size
/// and times, the blob staged for it, and its attributes are, as how it is hashed goes by them.
/// </summary>
sealed class KnownContent
{
    public readonly record struct Unchanged(long Length, long CreationTicks, long WriteTicks, ObjectId Id, Dictionary<string, string> Attributes);

    // By git folder, as a worktree has an index of its own. Each is replaced whole by the next
    // read, as two reads of a repository can run at once, and dropped where nothing is kept.
    static readonly ConcurrentDictionary<string, KnownContent> repositories = new(StringComparer.OrdinalIgnoreCase);

    readonly long indexLength;
    readonly (long, long) indexTimestamp;
    readonly GitConfig config;
    readonly Dictionary<string, Unchanged> files;

    KnownContent(IndexFile index, GitConfig config, Dictionary<string, Unchanged> files)
    {
        indexLength = index.Length;
        indexTimestamp = index.Timestamp;
        this.config = config;
        this.files = files;
    }

    /// <summary>
    /// What was found of the repository's files last read, where its index and config are as
    /// they were then. By path, from the top with forward slashes, as the index has them.
    /// </summary>
    public static IReadOnlyDictionary<string, Unchanged>? Of(string gitDir, IndexFile index, GitConfig config)
    {
        if (!repositories.TryGetValue(gitDir, out var known) ||
            known.indexLength != index.Length ||
            known.indexTimestamp != index.Timestamp ||
            !known.config.SameAs(config))
        {
            return null;
        }

        return known.files;
    }

    /// <summary>
    /// What was found in this read, in place of what was before.
    /// </summary>
    public static void Keep(string gitDir, IndexFile index, GitConfig config, Dictionary<string, Unchanged> files)
    {
        if (files.Count == 0)
        {
            repositories.TryRemove(gitDir, out _);
            return;
        }

        repositories[gitDir] = new(index, config, files);
    }

    /// <summary>
    /// Whether a file is as it was when found unchanged: the same size and times, the same blob
    /// staged for it, and the same attributes.
    /// </summary>
    public static bool IsAsItWas(Unchanged known, long length, long creationTicks, long writeTicks, ObjectId id, Dictionary<string, string> attributes) =>
        known.Length == length &&
        known.CreationTicks == creationTicks &&
        known.WriteTicks == writeTicks &&
        known.Id == id &&
        known.Attributes.Count == attributes.Count &&
        known.Attributes.All(_ => attributes.TryGetValue(_.Key, out var state) && state == _.Value);
}
