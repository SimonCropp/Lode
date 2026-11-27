/// <summary>
/// A repository opened to read in process, from the files git keeps: where its git folder and
/// working tree are, its config, its refs and its objects. Opened per read and disposed after,
/// so none of its files stay open: Windows wouldn't let them, or the folder, be deleted.
/// What isn't read as git would read it is thrown as a <see cref="GitFilesException"/>.
/// </summary>
sealed class GitRepository : IDisposable
{
    /// <summary>
    /// The top of the working tree: a bare repository isn't read here.
    /// </summary>
    public string WorkTree { get; }

    /// <summary>
    /// The git folder: .git, or, for a worktree or submodule, the one its .git file names, where
    /// its HEAD and index are.
    /// </summary>
    public string GitDir { get; }

    /// <summary>
    /// Where the refs, objects and config shared by all of a repository's worktrees are.
    /// </summary>
    public string CommonDir { get; }

    public GitConfig Config { get; }

    /// <summary>
    /// What HEAD names: refs/heads/main, or null when detached.
    /// </summary>
    public string? HeadRef { get; }

    /// <summary>
    /// The branch checked out, refs/heads/ left off, or null when detached.
    /// </summary>
    public string? Branch { get; }

    /// <summary>
    /// The commit checked out, or null before the first.
    /// </summary>
    public ObjectId? Head { get; }

    ObjectDatabase? objects;
    CommitGraph? graph;
    bool graphRead;
    Dictionary<string, (ObjectId Id, ObjectId? Peeled)>? packed;
    bool packedPeeled;
    bool shallowRead;

    GitRepository(string workTree, string gitDir, string commonDir, string headText)
    {
        WorkTree = workTree;
        GitDir = gitDir;
        CommonDir = commonDir;
        if (headText.StartsWith("ref: ", StringComparison.Ordinal))
        {
            HeadRef = headText[5..].Trim();
            if (HeadRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                Branch = HeadRef["refs/heads/".Length..];
            }
        }

        Config = GitConfig.Read(gitDir, commonDir, Branch);
        if (Config.Uncertain)
        {
            throw new GitFilesException("The config includes a file on a condition only git can tell");
        }

        CheckFormat();
        if (HeadRef == null)
        {
            if (!ObjectId.TryParse(headText.Trim(), out var detached))
            {
                throw new GitFilesException("HEAD is neither a ref nor a commit");
            }

            Head = detached;
            return;
        }

        Head = Resolve(HeadRef);
    }

    /// <summary>
    /// The repository at <paramref name="folder"/>, the top of its working tree, or null where it
    /// has no .git, or that names no git folder.
    /// </summary>
    public static GitRepository? Open(string folder)
    {
        if (GitDirOf(folder) is not { } gitDir ||
            ReadSmall(Path.Combine(gitDir, "HEAD")) is not { } head)
        {
            return null;
        }

        var commonDir = gitDir;
        if (ReadSmall(Path.Combine(gitDir, "commondir")) is { } common)
        {
            commonDir = Path.GetFullPath(Path.Combine(gitDir, common.Trim()));
        }

        var repository = new GitRepository(folder, gitDir, commonDir, head);

        // A submodule's git folder names its working tree, which is this one; any other is
        // elsewhere.
        if (repository.Config.GetLocal("core.worktree") is { } configured &&
            !SameFolder(Path.Combine(gitDir, configured), folder))
        {
            repository.Dispose();
            throw new GitFilesException("The working tree is elsewhere, as core.worktree says");
        }

        if (GitConfig.ParseBool(repository.Config.GetLocal("core.bare")) == true)
        {
            repository.Dispose();
            throw new GitFilesException("The repository is bare");
        }

        return repository;
    }

    static bool SameFolder(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The git folder of a working tree: its .git folder, or the one its .git file names.
    /// </summary>
    public static string? GitDirOf(string folder)
    {
        var git = Path.Combine(folder, ".git");
        if (Directory.Exists(git))
        {
            return git;
        }

        if (ReadSmall(git) is not { } pointer)
        {
            return null;
        }

        const string prefix = "gitdir: ";
        pointer = pointer.Trim();
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(folder, pointer[prefix.Length..]));
    }

    /// <summary>
    /// A small file of git's, HEAD or a ref, shared for writing and deleting, as git replaces
    /// them by renaming. Null where it isn't there, or is a folder: refs/heads/feature, say, for
    /// refs/heads/feature/one.
    /// </summary>
    internal static string? ReadSmall(string path)
    {
        try
        {
            // A UTF-8 BOM, as Windows editors write one, git passes over in what it reads, and
            // StreamReader for a longer file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> buffer = stackalloc byte[512];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == buffer.Length)
            {
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            var text = buffer[..read];
            if (text.StartsWith(Encoding.UTF8.Preamble))
            {
                text = text[Encoding.UTF8.Preamble.Length..];
            }

            return Encoding.UTF8.GetString(text);
        }
        catch (Exception exception)
            when (
                exception is
                    FileNotFoundException or
                    DirectoryNotFoundException or
                    UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Version 0 or 1, and in 1 only the extensions known. Objects other than SHA-1, refs other
    // than files, and objects left to a promisor, all aren't read here.
    void CheckFormat()
    {
        var version = Config.GetLocal("core.repositoryformatversion") ?? "0";
        if (version is not ("0" or "1"))
        {
            throw new GitFilesException($"Repository format {version} is not known");
        }

        foreach (var key in Config.LocalKeys("extensions."))
        {
            var value = Config.GetLocal(key);
            switch (key)
            {
                case "extensions.objectformat" when value == null || value.Equals("sha1", StringComparison.OrdinalIgnoreCase):
                case "extensions.refstorage" when value == null || value.Equals("files", StringComparison.OrdinalIgnoreCase):
                case "extensions.worktreeconfig":
                case "extensions.noop":
                case "extensions.preciousobjects":
                    continue;
                default:
                    if (version == "1" ||
                        key is "extensions.objectformat" or "extensions.refstorage" or "extensions.partialclone")
                    {
                        throw new GitFilesException($"The repository has {key}, which isn't read here");
                    }

                    continue;
            }
        }
    }

    public ObjectDatabase Objects =>
        objects ??= new(Path.Combine(CommonDir, "objects"));

    /// <summary>
    /// The commit graph, unless git wouldn't use one: turned off, or a shallow clone, whose
    /// commits are cut off where the graph has them going on.
    /// </summary>
    public CommitGraph? Graph
    {
        get
        {
            if (graphRead)
            {
                return graph;
            }

            graphRead = true;
            if (Shallow == null &&
                Config.GetBool("core.commitgraph", true))
            {
                graph = CommitGraph.Open(Path.Combine(CommonDir, "objects"));
            }

            return graph;
        }
    }

    /// <summary>
    /// The commits a shallow clone has without their parents. Null for one that isn't shallow.
    /// </summary>
    public HashSet<ObjectId>? Shallow
    {
        get
        {
            if (shallowRead)
            {
                return field;
            }

            shallowRead = true;
            if (ReadSmall(Path.Combine(CommonDir, "shallow")) is not { } text)
            {
                return null;
            }

            field = [];
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ObjectId.TryParse(line, out var id))
                {
                    field.Add(id);
                }
            }

            return field;
        }
    }

    /// <summary>
    /// Throws where git would read commits otherwise than they are: replaced by refs/replace, or
    /// grafted. Neither is followed here.
    /// </summary>
    public void CheckHistoryIsAsWritten()
    {
        if (File.Exists(Path.Combine(CommonDir, "info", "grafts")))
        {
            throw new GitFilesException("The repository has grafts");
        }

        if (!Config.GetBool("core.usereplacerefs", true) ||
            Environment.GetEnvironmentVariable("GIT_NO_REPLACE_OBJECTS") != null)
        {
            return;
        }

        var replace = Path.Combine(CommonDir, "refs", "replace");
        if ((Directory.Exists(replace) && Directory.EnumerateFiles(replace, "*", SearchOption.AllDirectories).Any()) ||
            Packed().Keys.Any(_ => _.StartsWith("refs/replace/", StringComparison.Ordinal)))
        {
            throw new GitFilesException("The repository replaces objects");
        }
    }

    // Refs each worktree has its own of, in its git folder: HEAD and the like, and a few under
    // refs/. The rest are shared, in the common folder.
    string RefPath(string name)
    {
        if (!name.StartsWith("refs/", StringComparison.Ordinal) ||
            name.StartsWith("refs/bisect/", StringComparison.Ordinal) ||
            name.StartsWith("refs/worktree/", StringComparison.Ordinal) ||
            name.StartsWith("refs/rewritten/", StringComparison.Ordinal))
        {
            return Path.Combine(GitDir, name);
        }

        return Path.Combine(CommonDir, name);
    }

    /// <summary>
    /// What a ref names in the end, following refs that name refs, loose ones first, then
    /// packed-refs. Null where it isn't there.
    /// </summary>
    public ObjectId? Resolve(string name)
    {
        for (var depth = 0; depth < 5; depth++)
        {
            if (ReadSmall(RefPath(name)) is not { } text)
            {
                if (Packed().TryGetValue(name, out var packedRef))
                {
                    return packedRef.Id;
                }

                return null;
            }

            text = text.Trim();
            if (text.StartsWith("ref: ", StringComparison.Ordinal))
            {
                name = text[5..].Trim();
                continue;
            }

            if (!ObjectId.TryParse(text, out var id))
            {
                throw new GitFilesException($"Ref {name} is corrupt");
            }

            return id;
        }

        throw new GitFilesException($"Ref {name} names refs too deep");
    }

    // "# pack-refs with: peeled fully-peeled sorted", then an id and a name a line, and after an
    // annotated tag what it is of, its line starting ^.
    Dictionary<string, (ObjectId Id, ObjectId? Peeled)> Packed()
    {
        if (packed != null)
        {
            return packed;
        }

        packed = new(StringComparer.Ordinal);
        if (ReadSmall(Path.Combine(CommonDir, "packed-refs")) is not { } text)
        {
            return packed;
        }

        string? last = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("# pack-refs with:", StringComparison.Ordinal))
                {
                    var traits = line["# pack-refs with:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    packedPeeled = traits.Contains("peeled") || traits.Contains("fully-peeled");
                }

                continue;
            }

            if (line.StartsWith('^'))
            {
                if (last != null &&
                    ObjectId.TryParse(line.AsSpan(1).Trim(), out var peeled))
                {
                    packed[last] = (packed[last].Id, peeled);
                }

                continue;
            }

            if (line.Length < 42 ||
                !ObjectId.TryParse(line.AsSpan(0, 40), out var id))
            {
                throw new GitFilesException("packed-refs is corrupt");
            }

            last = line[41..];
            packed[last] = (id, null);
        }

        return packed;
    }

    /// <summary>
    /// The tags, by their full names in order, each with what it is: a commit for a light one,
    /// or the tag object of an annotated one, with what it is of where packed-refs says.
    /// </summary>
    public List<(string Name, ObjectId Id, ObjectId? Peeled, bool PeelKnown)> Tags()
    {
        var tags = new Dictionary<string, (ObjectId Id, ObjectId? Peeled, bool PeelKnown)>(StringComparer.Ordinal);
        foreach (var (name, (id, peeled)) in Packed())
        {
            if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                // With peeled in its header, a tag without a ^ line isn't annotated.
                tags[name] = (id, peeled, packedPeeled);
            }
        }

        var folder = Path.Combine(CommonDir, "refs", "tags");
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var name = "refs/tags/" + Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (name.EndsWith(".lock", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Resolve(name) is { } id)
                {
                    tags[name] = (id, null, false);
                }
            }
        }

        return tags
            .OrderBy(_ => _.Key, StringComparer.Ordinal)
            .Select(_ => (_.Key, _.Value.Id, _.Value.Peeled, _.Value.PeelKnown))
            .ToList();
    }

    /// <summary>
    /// What an object is in the end once each tag it is is followed: a commit, usually. With its
    /// type, and the first tag's date, for git describe to choose between annotated tags.
    /// </summary>
    public (ObjectId Id, GitObjectType Type) Peel(ObjectId id)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (Objects.TryReadHeader(id) is not { } header)
            {
                throw new GitFilesException($"Object {id} is missing");
            }

            if (header.Type != GitObjectType.Tag)
            {
                return (id, header.Type);
            }

            id = TagTarget(Objects.Read(id, GitObjectType.Tag)).Id;
        }

        throw new GitFilesException("Tags of tags go too deep");
    }

    /// <summary>
    /// What a tag object is of, and when it was tagged, 0 where it says nothing of when.
    /// </summary>
    public static (ObjectId Id, long Date) TagTarget(byte[] tag)
    {
        var text = tag.AsSpan();
        if (!text.StartsWith("object "u8) ||
            !ObjectId.TryParse(text["object ".Length..], out var target))
        {
            throw new GitFilesException("A tag is corrupt");
        }

        long date = 0;
        var end = text.IndexOf("\n\n"u8);
        if (end < 0)
        {
            end = text.Length;
        }

        foreach (var range in text[..end].Split((byte)'\n'))
        {
            var line = text[..end][range];
            if (line.StartsWith("tagger "u8))
            {
                date = Commits.IdentDate(line["tagger ".Length..]);
            }
        }

        return (target, date);
    }

    /// <summary>
    /// The ref the branch tracks, refs/remotes/origin/main say, as branch.name.remote and
    /// branch.name.merge give it, mapped through the remote's fetch refspecs; the branch itself
    /// for remote ".". Null where it tracks none, or one no refspec fetches.
    /// </summary>
    public string? Upstream(string branch)
    {
        var remote = Config.Get($"branch.{branch}.remote");
        var merge = Config.GetAll($"branch.{branch}.merge").FirstOrDefault();
        if (remote == null ||
            merge == null)
        {
            return null;
        }

        if (remote == ".")
        {
            if (merge.StartsWith("refs/", StringComparison.Ordinal))
            {
                return merge;
            }

            return $"refs/heads/{merge}";
        }

        string? mapped = null;
        foreach (var raw in Config.GetAll($"remote.{remote}.fetch"))
        {
            var refspec = raw.Trim().TrimStart('+');
            if (refspec.StartsWith('^'))
            {
                if (RefspecMatches(refspec[1..], merge) != null)
                {
                    return null;
                }

                continue;
            }

            var colon = refspec.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            if (mapped == null &&
                RefspecMatches(refspec[..colon], merge) is { } middle)
            {
                var destination = refspec[(colon + 1)..];
                var star = destination.IndexOf('*');
                if (star < 0)
                {
                    mapped = destination;
                }
                else
                {
                    mapped = destination[..star] + middle + destination[(star + 1)..];
                }
            }
        }

        return mapped;
    }

    // What a refspec's source matches of a name: the part its * stands for, or "" for one without
    // a *, matching all of it; null where it doesn't match.
    static string? RefspecMatches(string source, string name)
    {
        var star = source.IndexOf('*');
        if (star < 0)
        {
            if (source == name)
            {
                return "";
            }

            return null;
        }

        var prefix = source[..star];
        var suffix = source[(star + 1)..];
        if (name.Length >= prefix.Length + suffix.Length &&
            name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return name[prefix.Length..^suffix.Length];
        }

        return null;
    }

    /// <summary>
    /// The commit a submodule, or any repository inside the working tree, has checked out, read
    /// from its own git folder. Null where it has none: not checked out, or no commits yet.
    /// </summary>
    public static ObjectId? HeadOf(string folder)
    {
        if (GitDirOf(folder) is not { } gitDir ||
            ReadSmall(Path.Combine(gitDir, "HEAD")) is not { } head)
        {
            return null;
        }

        var commonDir = gitDir;
        if (ReadSmall(Path.Combine(gitDir, "commondir")) is { } common)
        {
            commonDir = Path.GetFullPath(Path.Combine(gitDir, common.Trim()));
        }

        for (var depth = 0; depth < 5; depth++)
        {
            head = head.Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                if (ObjectId.TryParse(head, out var id))
                {
                    return id;
                }

                return null;
            }

            var name = head[5..].Trim();
            var path = Path.Combine(commonDir, name);
            if (!name.StartsWith("refs/", StringComparison.Ordinal))
            {
                path = Path.Combine(gitDir, name);
            }

            if (ReadSmall(path) is { } loose)
            {
                head = loose;
                continue;
            }

            if (ReadSmall(Path.Combine(commonDir, "packed-refs")) is not { } packedRefs)
            {
                return null;
            }

            foreach (var line in packedRefs.Split('\n'))
            {
                if (line.Length > 41 &&
                    line[41..].TrimEnd('\r') == name &&
                    ObjectId.TryParse(line.AsSpan(0, 40), out var id))
                {
                    return id;
                }
            }

            return null;
        }

        return null;
    }

    public void Dispose()
    {
        objects?.Dispose();
        graph?.Dispose();
    }
}
