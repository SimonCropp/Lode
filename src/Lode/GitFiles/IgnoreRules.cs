/// <summary>
/// What git ignores in a working tree: each folder's .gitignore, the deepest first, the last
/// line of each that matches deciding; then the repository's info/exclude, then the user's
/// core.excludesFile. Inside a folder ignored, everything is, whatever a .gitignore further in
/// would say, as git doesn't look there.
/// </summary>
sealed class IgnoreRules
{
    readonly string workTree;
    readonly bool ignoreCase;
    readonly PathPattern[] info;
    readonly PathPattern[] user;

    /// <summary>
    /// A folder's place in the rules: its own .gitignore's patterns, those of the folders it is
    /// in, and whether it, or one it is in, is ignored. Made once a folder, going down, and
    /// shared by whatever is in it.
    /// </summary>
    public sealed class Folder
    {
        public Folder? Parent { get; init; }

        /// <summary>
        /// From the top, with no slash at the end: "" for the top itself.
        /// </summary>
        public required string Path { get; init; }

        public PathPattern[] Patterns { get; init; } = [];

        public bool Ignored { get; init; }

        public string PathOf(string name)
        {
            if (Path.Length == 0)
            {
                return name;
            }

            return $"{Path}/{name}";
        }
    }

    public IgnoreRules(GitRepository repository, string workTree)
    {
        this.workTree = workTree;
        ignoreCase = repository.Config.GetBool("core.ignorecase", false);
        info = ReadPatterns(Path.Combine(repository.CommonDir, "info", "exclude"), "");
        var excludes = repository.Config.Get("core.excludesfile");
        if (excludes == null)
        {
            user = ReadPatterns(GitConfig.XdgPath("ignore"), "");
            return;
        }

        user = ReadPatterns(GitConfig.ExpandPath(excludes), "");
    }

    static PathPattern[] ReadPatterns(string file, string @base)
    {
        if (GitRepository.ReadSmall(file) is not { } text)
        {
            return [];
        }

        return PathPattern.Lines(text)
            .Select(_ => PathPattern.Parse(_, @base))
            .ToArray();
    }

    /// <summary>
    /// The top of the working tree. <paramref name="hasIgnoreFile"/> where it is known whether it
    /// has a .gitignore, from looking in it, else it is looked for.
    /// </summary>
    public Folder Top(bool? hasIgnoreFile = null) =>
        new()
        {
            Path = "",
            Patterns = FolderPatterns("", hasIgnoreFile)
        };

    /// <summary>
    /// A folder in <paramref name="parent"/>: ignored where it, or one it is in, is; otherwise
    /// with its own .gitignore read.
    /// </summary>
    public Folder Enter(Folder parent, string name, bool? hasIgnoreFile = null)
    {
        var path = parent.PathOf(name);
        if (parent.Ignored ||
            IsIgnored(parent, name, true))
        {
            return new()
            {
                Parent = parent,
                Path = path,
                Ignored = true
            };
        }

        return new()
        {
            Parent = parent,
            Path = path,
            Patterns = FolderPatterns(path, hasIgnoreFile)
        };
    }

    PathPattern[] FolderPatterns(string path, bool? hasIgnoreFile)
    {
        if (hasIgnoreFile == false)
        {
            return [];
        }

        var file = Path.Combine(workTree, path, ".gitignore");
        return ReadPatterns(file, path);
    }

    /// <summary>
    /// Whether git ignores <paramref name="name"/> in <paramref name="folder"/>.
    /// </summary>
    public bool IsIgnored(Folder folder, string name, bool isFolder)
    {
        if (folder.Ignored)
        {
            return true;
        }

        var path = folder.PathOf(name);
        var nameStart = path.Length - name.Length;
        for (var level = folder; level != null; level = level.Parent)
        {
            if (Decide(level.Patterns, path, nameStart, isFolder) is { } decided)
            {
                return decided;
            }
        }

        if (Decide(info, path, nameStart, isFolder) is { } fromInfo)
        {
            return fromInfo;
        }

        return Decide(user, path, nameStart, isFolder) ?? false;
    }

    // The last pattern that matches decides; null where none does.
    bool? Decide(PathPattern[] patterns, string path, int nameStart, bool isFolder)
    {
        for (var index = patterns.Length - 1; index >= 0; index--)
        {
            var pattern = patterns[index];
            if (pattern.Matches(path, nameStart, isFolder, ignoreCase))
            {
                return (pattern.Flags & PathPattern.Negative) == 0;
            }
        }

        return null;
    }
}
