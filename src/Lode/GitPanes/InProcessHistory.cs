/// <summary>
/// Reads a repository's history in Lode's own process, from the files git keeps, rather than by
/// running git as <see cref="GitHistory"/> does: the same commits, the same ones not pushed, and
/// the same files each changed, renames found as git finds them. Quicker for what History reads
/// as it opens, as no git is started, and walked by the commit graph where git has written one:
/// see HistoryBenchmarks. Each Faster reads in process, and runs git where the repository can't
/// be read here, or can't be read as git would. The repository is let go of before each
/// returns, so none of its files stay open, as Windows would then not let them, or the folder, be
/// deleted.
/// </summary>
static class InProcessHistory
{
    /// <summary>
    /// The commits from HEAD back, newest first, after the first <paramref name="skip"/>, as
    /// <see cref="GitHistory.Read(string, int, int, Cancel)"/> reads them: in process, by git
    /// where that can't.
    /// </summary>
    public static List<GitCommit>? ReadFaster(string repo, int skip, int count, Cancel cancel) =>
        ReadFaster(repo, skip, count, new(), cancel);

    /// <summary>
    /// As <see cref="ReadFaster(string, int, int, Cancel)"/>, going on from where
    /// <paramref name="walk"/> stopped where that is <paramref name="skip"/> commits down, as it
    /// is for the next page: see <see cref="HistoryWalk"/>.
    /// </summary>
    public static List<GitCommit>? ReadFaster(string repo, int skip, int count, HistoryWalk walk, Cancel cancel) =>
        Read(repo, skip, count, walk, cancel) ?? GitHistory.Read(repo, skip, count, cancel);

    /// <summary>
    /// The commits not pushed, as <see cref="GitHistory.Unpushed"/> finds them, in process.
    /// </summary>
    public static HashSet<string> UnpushedFaster(string repo, Cancel cancel) =>
        Unpushed(repo, cancel) ?? GitHistory.Unpushed(repo, cancel);

    /// <summary>
    /// Whether the commit checked out has never been pushed, as
    /// <see cref="GitHistory.NeverPushed"/> finds it, in process.
    /// </summary>
    public static bool NeverPushedFaster(string repo, Cancel cancel) =>
        NeverPushed(repo, cancel) ?? GitHistory.NeverPushed(repo, cancel);

    /// <summary>
    /// The files a commit changed, as <see cref="GitHistory.FilesOf"/> lists them, in process.
    /// </summary>
    public static List<Change>? FilesOfFaster(string repo, GitCommit commit, Cancel cancel) =>
        FilesOf(repo, commit, cancel) ?? GitHistory.FilesOf(repo, commit, cancel);

    /// <summary>
    /// The commits from HEAD back, newest first, after the first <paramref name="skip"/>, as
    /// <see cref="GitHistory.Read(string, int, int, Cancel)"/> reads them. Empty before the first
    /// commit. Authors named as a .mailmap says, as git log's %aN names them. Null when the
    /// repository can't be read, or not as git log would read it: a commit message not in UTF-8,
    /// which git turns into UTF-8, say.
    /// </summary>
    public static List<GitCommit>? Read(string repo, int skip, int count, Cancel cancel) =>
        Read(repo, skip, count, new(), cancel);

    /// <summary>
    /// As <see cref="Read(string, int, int, Cancel)"/>, going on from where
    /// <paramref name="walk"/> stopped where that is <paramref name="skip"/> commits down, and
    /// leaving it where this stops.
    /// </summary>
    public static List<GitCommit>? Read(string repo, int skip, int count, HistoryWalk walk, Cancel cancel) =>
        Open(repo, cancel, _ => Read(_, skip, count, walk));

    /// <summary>
    /// The commits on the branch that the branch it tracks hasn't got yet, as
    /// <see cref="GitHistory.Unpushed"/> finds them. None without a branch tracked. Null when the
    /// repository can't be read.
    /// </summary>
    public static HashSet<string>? Unpushed(string repo, Cancel cancel) =>
        Open(repo, cancel, Unpushed);

    /// <summary>
    /// Whether the commit checked out has never been pushed, as
    /// <see cref="GitHistory.NeverPushed"/> finds it. Null when the repository can't be read.
    /// </summary>
    public static bool? NeverPushed(string repo, Cancel cancel) =>
        Open(repo, cancel, _ => Tuple.Create(NeverPushed(_)))?.Item1;

    /// <summary>
    /// The files <paramref name="commit"/> changed, against its first parent, as
    /// <see cref="GitHistory.FilesOf"/> lists them. Null when the repository can't be read.
    /// </summary>
    public static List<Change>? FilesOf(string repo, GitCommit commit, Cancel cancel) =>
        Open(repo, cancel, _ => FilesOf(_, commit));

    static T? Open<T>(string repo, Cancel cancel, Func<GitRepository, T?> read)
        where T : class
    {
        if (cancel.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var repository = GitRepository.Open(repo);
            if (repository == null)
            {
                return null;
            }

            return read(repository);
        }
        catch (Exception exception)
            when (InProcessState.IsUnreadable(exception))
        {
            return null;
        }
    }

    // Newest first, as git log has them, with the tags on each.
    static List<GitCommit>? Read(GitRepository repository, int skip, int count, HistoryWalk walk)
    {
        // Taken from the walk while this reads, so one that fails part way, at a commit not in
        // UTF-8 say, leaves nothing to go on from, and the next page walks from HEAD again.
        LogWalk log;
        Mailmap mailmap;
        Dictionary<ObjectId, List<string>> tags;
        Commits commits;
        if (walk.TakeAt(skip) is { } stopped)
        {
            (log, mailmap, tags) = stopped;
            commits = new(repository);
        }
        else
        {
            // No commits yet, so nothing to log from.
            if (repository.Head is not { } head)
            {
                return [];
            }

            mailmap = Mailmap.Read(repository);
            commits = new(repository);
            log = new(head);
            if (skip > 0)
            {
                commits.Log(log, skip);
            }

            tags = TagsByCommit(repository);
        }

        var nodes = commits.Log(log, count);
        var read = new List<GitCommit>(nodes.Count);
        foreach (var node in nodes)
        {
            var info = commits.Info(node);
            if (!IsUtf8(info.Encoding))
            {
                return null;
            }

            var id = commits.IdOf(node);
            var author = info.AuthorName;
            if (!mailmap.IsEmpty)
            {
                author = mailmap.NameOf(author, info.AuthorEmail);
            }

            read.Add(
                new(
                    id.ToString(),
                    commits.Parents(node).Select(_ => commits.IdOf(_).ToString()).ToList(),
                    author,
                    DateTimeOffset.FromUnixTimeSeconds(info.AuthorDate),
                    tags.GetValueOrDefault(id) ?? [],
                    info.Subject));
        }

        walk.StoppedAt(skip + read.Count, new(log, mailmap, tags), commits.Read);
        return read;
    }

    // As a commit's encoding header names it, UTF-8 when it has none.
    static bool IsUtf8(string? encoding) =>
        encoding == null ||
        encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase);

    // The tags on each commit, as git log's %D lists them: an annotated tag on the commit it is
    // of, the last by name first.
    static Dictionary<ObjectId, List<string>> TagsByCommit(GitRepository repository)
    {
        var tags = new Dictionary<ObjectId, List<string>>();
        foreach (var (name, id, peeled, peelKnown) in repository.Tags())
        {
            var target = id;
            if (peeled is { } known)
            {
                target = known;
            }
            else if (!peelKnown)
            {
                target = repository.Peel(id).Id;
            }

            if (!tags.TryGetValue(target, out var names))
            {
                names = [];
                tags[target] = names;
            }

            names.Insert(0, name["refs/tags/".Length..]);
        }

        return tags;
    }

    static HashSet<string> Unpushed(GitRepository repository)
    {
        if (repository.Head is not { } head ||
            repository.Branch is not { } branch ||
            repository.Upstream(branch) is not { } upstreamRef ||
            repository.Resolve(upstreamRef) is not { } upstream)
        {
            return [];
        }

        var commits = new Commits(repository);
        var (ahead, _) = commits.Divergence(commits.Node(head), commits.Node(upstream));
        return ahead
            .Select(_ => commits.IdOf(_).ToString())
            .ToHashSet(StringComparer.Ordinal);
    }

    // On a branch tracking none, or one whose remote branch is gone, the commit checked out in
    // none of the remotes' branches' history: with no remote, in none at all.
    static bool NeverPushed(GitRepository repository)
    {
        if (repository.Head is not { } head ||
            repository.Branch is not { } branch ||
            (repository.Upstream(branch) is { } upstreamRef &&
             repository.Resolve(upstreamRef) != null))
        {
            return false;
        }

        var remotes = repository.RemoteBranches();
        if (remotes.Count == 0)
        {
            return true;
        }

        var commits = new Commits(repository);
        var start = commits.Node(head);
        var (headOnly, _) = commits.Divergence(start, remotes.Select(commits.Node).ToList());
        return headOnly.Contains(start);
    }

    static List<Change>? FilesOf(GitRepository repository, GitCommit commit)
    {
        if (!ObjectId.TryParse(commit.Sha, out var id) ||
            repository.Objects.TryRead(id) is not { Type: GitObjectType.Commit } read)
        {
            return null;
        }

        var tree = new CommitInfo(read.Data).Tree;

        // Against nothing for the first commit, so everything in it is added, as diff-tree --root
        // has it.
        ObjectId? parentTree = null;
        if (commit.Parents.Count > 0)
        {
            if (!ObjectId.TryParse(commit.Parents[0], out var parent))
            {
                return null;
            }

            parentTree = new CommitInfo(repository.Objects.Read(parent, GitObjectType.Commit)).Tree;
        }

        var changes = Trees.Diff(repository.Objects, parentTree, tree);
        var deleted = new List<Renames.File>();
        var added = new List<Renames.File>();
        foreach (var change in changes)
        {
            if (change.NewMode == 0)
            {
                deleted.Add(new(change.Path, change.OldMode, change.OldId));
            }
            else if (change.OldMode == 0)
            {
                added.Add(new(change.Path, change.NewMode, change.NewId));
            }
        }

        // As diff-tree -M: renames, as many files looked at for them as diff.renameLimit says.
        var limit = repository.Config.GetInt("diff.renamelimit", 1000);
        var renamedFrom = new Renames(repository.Objects).Find(deleted, added, limit);
        var renamed = new HashSet<byte[]>();
        var files = new List<Change>(changes.Count);
        for (var index = 0; index < added.Count; index++)
        {
            if (renamedFrom[index] < 0)
            {
                continue;
            }

            var source = deleted[renamedFrom[index]].Path;
            renamed.Add(source);
            renamed.Add(added[index].Path);
            files.Add(new(Encoding.UTF8.GetString(added[index].Path), Encoding.UTF8.GetString(source), GitChange.Renamed));
        }

        foreach (var change in changes)
        {
            if (renamed.Contains(change.Path))
            {
                continue;
            }

            var kind = GitChange.Modified;
            if (change.OldMode == 0)
            {
                kind = GitChange.Added;
            }
            else if (change.NewMode == 0)
            {
                kind = GitChange.Deleted;
            }

            files.Add(new(change.Text, null, kind));
        }

        files.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return files;
    }
}

/// <summary>
/// Where the commits History has listed were read to, kept by the pane between pages, so the
/// next page is read on from there: walked from HEAD again, past every commit listed, the page
/// 5,000 commits down walked 5,000, and each further down took longer. With the .mailmap and the
/// tags read for the first page, which the next have as they were: a change of either, or of
/// HEAD, reads History again from the top, with a walk of its own.
/// </summary>
sealed class HistoryWalk
{
    internal sealed record Stop(LogWalk Log, Mailmap Mailmap, Dictionary<ObjectId, List<string>> Tags);

    // How many commits were listed when it stopped, and where. None before the first page, and
    // none once a page failed part way.
    int listed;
    Stop? stop;

    /// <summary>
    /// How many commits the last page read, from the commit graph or their objects. For tests.
    /// </summary>
    public int CommitsRead { get; private set; }

    /// <summary>
    /// Where it stopped, for a page read on from <paramref name="skip"/> commits down to go on
    /// from, taken from it until the page is read. Null where it stopped elsewhere, or nowhere.
    /// </summary>
    internal Stop? TakeAt(int skip)
    {
        var taken = stop;
        stop = null;
        if (listed != skip)
        {
            return null;
        }

        return taken;
    }

    /// <summary>
    /// A page was read: <paramref name="listed"/> commits have been, to where it stopped, and
    /// that page read <paramref name="read"/> commits to do it.
    /// </summary>
    internal void StoppedAt(int listed, Stop stop, int read)
    {
        this.listed = listed;
        this.stop = stop;
        CommitsRead = read;
    }
}
