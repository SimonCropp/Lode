/// <summary>
/// Where Find in Files looks, from what its Where box says, as Sublime's does: entries split by
/// commas, each a folder or file to look in, absolute or from the folder open, a pattern a file
/// must match (<c>*.cs</c>), or, after a minus, one it, or a folder it is in, mustn't
/// (<c>-*.min.js</c>, <c>-node_modules\</c>). <c>&lt;open folders&gt;</c> is the folders the
/// window's tabs show. A pattern with a slash in it is matched against the path from the folder
/// looked in, its * going across slashes, and one ending in a slash only against folders; any
/// other against names. With no folder given, the folder open.
/// </summary>
sealed class SearchWhere
{
    /// <summary>
    /// The token for the folders the window's tabs show.
    /// </summary>
    public const string OpenFolders = "<open folders>";

    /// <param name="Text">The pattern, with forward slashes made back ones, and any trailing
    /// one taken off.</param>
    /// <param name="IsPath">Matched against the path from the folder looked in, as it has a
    /// slash in it; otherwise against the name.</param>
    /// <param name="IsFolder">Only for folders, as it ended in a slash.</param>
    public sealed record Pattern(string Text, bool IsPath, bool IsFolder)
    {
        public static Pattern Parse(string text)
        {
            text = text.Replace('/', '\\');
            var isFolder = text.EndsWith('\\');
            if (isFolder)
            {
                text = text.TrimEnd('\\');
            }

            return new(text, text.Contains('\\'), isFolder);
        }

        public bool Matches(string relative, string name)
        {
            if (IsPath)
            {
                return MatchesPath(Text, relative);
            }

            return FileSystemName.MatchesSimpleExpression(Text, name);
        }

        // Not MatchesSimpleExpression, to which a backslash is no separator: it says the
        // character after it is meant as itself, so src\*.cs matched nothing, and bin\Debug
        // only a name with no slash in it. Here * is any run of characters, slashes among them,
        // ? any one, and anything else itself, in either case.
        static bool MatchesPath(CharSpan pattern, CharSpan path)
        {
            var at = 0;
            var position = 0;

            // The last * come to, and how much of the path it has been tried against.
            var star = -1;
            var taken = 0;
            while (position < path.Length)
            {
                if (at < pattern.Length &&
                    pattern[at] == '*')
                {
                    star = at;
                    taken = position;
                    at++;
                    continue;
                }

                if (at < pattern.Length &&
                    (pattern[at] == '?' ||
                     char.ToUpperInvariant(pattern[at]) == char.ToUpperInvariant(path[position])))
                {
                    at++;
                    position++;
                    continue;
                }

                if (star < 0)
                {
                    return false;
                }

                // The * takes one more character, and what follows it is tried from there.
                taken++;
                position = taken;
                at = star + 1;
            }

            return pattern[at..].TrimStart('*').IsEmpty;
        }

        public override string ToString()
        {
            if (IsFolder)
            {
                return $"{Text}\\";
            }

            return Text;
        }
    }

    /// <summary>
    /// The folders and files to look in, each once, in the order given.
    /// </summary>
    public IReadOnlyList<string> Roots { get; }

    public IReadOnlyList<Pattern> Includes { get; }
    public IReadOnlyList<Pattern> Excludes { get; }

    SearchWhere(IReadOnlyList<string> roots, IReadOnlyList<Pattern> includes, IReadOnlyList<Pattern> excludes)
    {
        Roots = roots;
        Includes = includes;
        Excludes = excludes;
    }

    /// <summary>
    /// What <paramref name="text"/> says, or, as <paramref name="error"/>, why it can't be
    /// searched: a folder that isn't there, say.
    /// </summary>
    /// <param name="folder">The folder open, which a folder not given in full is in, and which
    /// is searched when no folder is given. Null where there is none, This PC say.</param>
    /// <param name="openFolders">The folders the window's tabs show.</param>
    public static SearchWhere? Parse(string text, string? folder, IReadOnlyList<string> openFolders, out string? error)
    {
        error = null;
        var roots = new List<string>();
        var includes = new List<Pattern>();
        var excludes = new List<Pattern>();

        void AddRoot(string root)
        {
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        foreach (var part in text.Split(','))
        {
            var entry = part.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.Equals(OpenFolders, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var open in openFolders)
                {
                    AddRoot(open);
                }

                continue;
            }

            if (entry[0] == '-')
            {
                var excluded = entry[1..].Trim();
                if (excluded.Length > 0)
                {
                    excludes.Add(Pattern.Parse(excluded));
                }

                continue;
            }

            if (entry.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                includes.Add(Pattern.Parse(entry));
                continue;
            }

            var path = Expand(entry, folder);
            if (path == null ||
                !Path.Exists(path))
            {
                error = $"There's no folder or file {entry}.";
                return null;
            }

            AddRoot(path);
        }

        if (roots.Count == 0)
        {
            if (folder == null)
            {
                error = "Say which folder to search in Where.";
                return null;
            }

            roots.Add(folder);
        }

        return new(roots, includes, excludes);
    }

    // In full: as it is, or from the folder open.
    static string? Expand(string entry, string? folder)
    {
        try
        {
            if (Path.IsPathFullyQualified(entry))
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            }

            if (folder == null)
            {
                return null;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(folder, entry)));
        }
        catch (Exception exception)
            when (
                exception is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a file is searched: it matches an include, when there are any, and no exclude.
    /// </summary>
    /// <param name="relative">Its path from the folder looked in.</param>
    public bool IncludesFile(string relative, string name)
    {
        foreach (var exclude in Excludes)
        {
            if (!exclude.IsFolder &&
                exclude.Matches(relative, name))
            {
                return false;
            }
        }

        if (Includes.Count == 0)
        {
            return true;
        }

        foreach (var include in Includes)
        {
            if (!include.IsFolder &&
                include.Matches(relative, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a folder is gone into: no exclude matches it.
    /// </summary>
    public bool IncludesFolder(string relative, string name)
    {
        foreach (var exclude in Excludes)
        {
            if (exclude.Matches(relative, name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The entries for <paramref name="text"/> with its folders replaced by
    /// <paramref name="folder"/>, its patterns kept: what Where becomes when the strip opens on
    /// another folder. Left as it is when it searches the open folders, which that one is of.
    /// </summary>
    public static string WithFolder(string text, string folder)
    {
        var entries = text
            .Split(',')
            .Select(_ => _.Trim())
            .Where(_ => _.Length > 0)
            .ToList();
        if (entries.Any(_ => _.Equals(OpenFolders, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Join(", ", entries);
        }

        var kept = entries.Where(_ => _[0] == '-' ||
                                      _.AsSpan().IndexOfAny('*', '?') >= 0);
        return string.Join(", ", [folder, .. kept]);
    }
}
