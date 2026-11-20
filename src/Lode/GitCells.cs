/// <summary>
/// What the Git column says of each item. In a folder of repositories, the code folder say, each
/// repository's state. In a repository, what has happened to a file, or how many changes a
/// folder holds. Made afresh each time the states change, and not changed after, so a folder can
/// be sorted by it off the UI thread.
/// </summary>
sealed class GitCells
{
    public static readonly GitCells None = new(new Dictionary<string, GitState>(), null, null);

    // The repositories' states, by their paths.
    readonly Dictionary<string, GitState> repos;

    // The repository the folder is in, and, by their paths from its top, what has happened to
    // each path that changed, and how many changes each folder above one holds.
    readonly string? root;
    readonly Dictionary<string, GitChange> files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> folders = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="repos">The states of the repositories the folder lists, by their paths.</param>
    /// <param name="root">The repository the folder is in, if it is in one.</param>
    /// <param name="state">That repository's state, once it has been read.</param>
    public GitCells(IReadOnlyDictionary<string, GitState> repos, string? root, GitState? state)
    {
        this.repos = new(repos, StringComparer.OrdinalIgnoreCase);
        this.root = root;
        if (state == null)
        {
            return;
        }

        foreach (var file in state.Files)
        {
            files[file.Path] = file.Change;
            for (var slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
            {
                var folder = file.Path[..slash];
                folders[folder] = folders.GetValueOrDefault(folder) + 1;
            }
        }
    }

    /// <summary>
    /// What the column says of the item: a repository's state, a file's change, or how many
    /// changes a folder holds. Nothing for one that hasn't changed, or that isn't known yet.
    /// </summary>
    public string Text(FileEntry entry)
    {
        if (repos.TryGetValue(entry.FullPath, out var state))
        {
            return state.Describe();
        }

        var path = FromRoot(entry);
        if (path == null)
        {
            return "";
        }

        if (files.TryGetValue(path, out var change))
        {
            return change.ToString();
        }

        if (entry.IsDirectory &&
            folders.TryGetValue(path, out var count))
        {
            return GitState.CountOf(count, "change");
        }

        return "";
    }

    /// <summary>
    /// The change the item's cell is coloured for: its own, or Modified for a folder holding
    /// changes. Null for one with none, and for a repository, whose cell is a state.
    /// </summary>
    public GitChange? Change(FileEntry entry)
    {
        var path = FromRoot(entry);
        if (path == null)
        {
            return null;
        }

        if (files.TryGetValue(path, out var change))
        {
            return change;
        }

        if (entry.IsDirectory &&
            folders.ContainsKey(path))
        {
            return GitChange.Modified;
        }

        return null;
    }

    // Where the item is from the top of the repository, as git has it: with forward slashes.
    // Null outside a repository, and for the top itself.
    string? FromRoot(FileEntry entry)
    {
        if (root == null ||
            Locations.Same(entry.FullPath, root) ||
            !Locations.IsWithin(entry.FullPath, root))
        {
            return null;
        }

        return Path.GetRelativePath(root, entry.FullPath).Replace('\\', '/');
    }
}
