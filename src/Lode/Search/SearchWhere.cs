/// <summary>
/// Where Find in Files looks, from what its Where box says, as Sublime's does: entries split by
/// commas, each a folder or file to look in, absolute or from the folder open, a pattern a file
/// must match (<c>*.cs</c>), or, after a minus, one it, or a folder it is in, mustn't
/// (<c>-*.min.js</c>, <c>-node_modules\</c>). <c>&lt;open folders&gt;</c> is the folders the
/// window's tabs show. A pattern with a slash in it is matched against the path from the folder
/// looked in, its * going across slashes, and its **\ any number of folders, none among them
/// (<c>src\**\*.cs</c>), and one ending in a slash only against folders; any other against
/// names. Braces are alternatives, as in VS Code's files to include: <c>*.{ts,tsx}</c> is
/// <c>*.ts</c> and <c>*.tsx</c>. With no folder given, the folder open.
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
        // ? any one, **\ as a part of its own any number of folders, none among them, and
        // anything else itself, in either case.
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
                    // As in VS Code and .gitignore, src\**\*.cs takes the files in src itself,
                    // and **\node_modules the folder at the top, which a run of characters
                    // ending in a slash, as **\ is otherwise, left out. Tried first as no
                    // folders; then as that run, which any number of folders is.
                    if (IsAnyFolders(pattern, at) &&
                        MatchesPath(pattern[(at + 3)..], path[position..]))
                    {
                        return true;
                    }

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

        // Whether the pattern has **\ at a place, as a part of its own: at its start or after a
        // slash. Elsewhere, as in a**\, the stars are any run of characters, as any other.
        static bool IsAnyFolders(CharSpan pattern, int at) =>
            at + 2 < pattern.Length &&
            pattern[at + 1] == '*' &&
            pattern[at + 2] == '\\' &&
            (at == 0 || pattern[at - 1] == '\\');

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

        foreach (var entry in Entries(text).SelectMany(_ => _.Alternatives))
        {
            if (IsOpenFolders(entry))
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

            if (IsPattern(entry))
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
        var entries = Entries(text);
        if (entries.Any(_ => _.Alternatives.Any(IsOpenFolders)))
        {
            return string.Join(", ", entries.Select(_ => _.Text));
        }

        var kept = entries
            .Where(_ => _.Alternatives.All(IsPattern))
            .Select(_ => _.Text);
        return string.Join(", ", [Quote(folder), .. kept]);
    }

    static bool IsOpenFolders(string entry) =>
        entry.Equals(OpenFolders, StringComparison.OrdinalIgnoreCase);

    // A pattern to include or, after a minus, to leave out, rather than a folder or file.
    static bool IsPattern(string entry) =>
        entry[0] == '-' ||
        entry.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>
    /// An entry of Where's text: as typed, quotes and braces and all, and what it stands for,
    /// each of the alternatives its braces give, without the quotes.
    /// </summary>
    readonly record struct Entry(string Text, List<string> Alternatives);

    // The entries Where's text holds, between its commas, none of them standing for nothing. A
    // folder's name can have a comma in it, though, so what is in quotes is one entry, commas
    // and all, as Copy as path gives a path: taken without the quotes, which no name has in it.
    // And the commas in braces are between alternatives, as in VS Code's files to include, so
    // *.{ts,tsx} is one entry, rather than *.{ts and a folder tsx} that isn't there.
    static List<Entry> Entries(string text)
    {
        var pairs = BracePairs(text);
        var entries = new List<Entry>();
        var quoted = false;
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                quoted = !quoted;
            }
            else if (pairs[index] >= 0)
            {
                depth += pairs[index] > index ? 1 : -1;
            }
            else if (text[index] == ',' &&
                     !quoted &&
                     depth == 0)
            {
                Add(start, index);
                start = index + 1;
            }
        }

        Add(start, text.Length);
        return entries;

        void Add(int from, int to)
        {
            var entry = text[from..to].Trim();
            var alternatives = Alternatives(entry);
            if (alternatives.Count > 0)
            {
                entries.Add(new(entry, alternatives));
            }
        }
    }

    // For each brace of text that is one of a pair, outside quotes, where the other is; -1 for
    // every other character. A brace with no other is taken as itself, as a folder's name can
    // have one, which is then no reason to keep the entries after it together.
    static int[] BracePairs(string text)
    {
        var pairs = new int[text.Length];
        Array.Fill(pairs, -1);
        var opened = new Stack<int>();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '{' when !quoted:
                    opened.Push(index);
                    break;
                case '}' when !quoted && opened.Count > 0:
                    var opening = opened.Pop();
                    pairs[opening] = index;
                    pairs[index] = opening;
                    break;
            }
        }

        return pairs;
    }

    // What an entry stands for: each pair of braces with commas in them any one of what is
    // between the commas, braces in braces too, and the rest of the entry around it, without its
    // quotes, and what is in quotes as itself; those that come to nothing left out.
    static List<string> Alternatives(string entry)
    {
        var alternatives = new List<string>();
        foreach (var alternative in Expanded(entry, BracePairs(entry), 0, entry.Length))
        {
            var trimmed = alternative.Trim();
            if (trimmed.Length > 0)
            {
                alternatives.Add(trimmed);
            }
        }

        return alternatives;
    }

    // What the entry's characters from start to before end stand for.
    static List<string> Expanded(string entry, int[] pairs, int start, int end)
    {
        List<string> expanded = [""];
        var plain = new StringBuilder();
        for (var index = start; index < end; index++)
        {
            if (entry[index] == '"')
            {
                continue;
            }

            var close = pairs[index];
            if (close <= index)
            {
                plain.Append(entry[index]);
                continue;
            }

            var between = plain.ToString();
            plain.Clear();
            var options = Options(entry, pairs, index + 1, close);
            var combined = new List<string>(expanded.Count * options.Count);
            foreach (var before in expanded)
            {
                foreach (var option in options)
                {
                    combined.Add(before + between + option);
                }
            }

            expanded = combined;
            index = close;
        }

        var after = plain.ToString();
        for (var index = 0; index < expanded.Count; index++)
        {
            expanded[index] += after;
        }

        return expanded;
    }

    // What the braces around the entry's characters from start to before end stand for: each of
    // what is between their commas, outside quotes and braces in them; with no commas, as bash
    // has it, the braces themselves around what is in them.
    static List<string> Options(string entry, int[] pairs, int start, int end)
    {
        var parts = new List<(int Start, int End)>();
        var quoted = false;
        var from = start;
        for (var index = start; index < end; index++)
        {
            if (entry[index] == '"')
            {
                quoted = !quoted;
            }
            else if (pairs[index] > index)
            {
                index = pairs[index];
            }
            else if (entry[index] == ',' &&
                     !quoted)
            {
                parts.Add((from, index));
                from = index + 1;
            }
        }

        parts.Add((from, end));
        var options = new List<string>();
        if (parts.Count == 1)
        {
            foreach (var inside in Expanded(entry, pairs, start, end))
            {
                options.Add($"{{{inside}}}");
            }

            return options;
        }

        foreach (var (partStart, partEnd) in parts)
        {
            options.AddRange(Expanded(entry, pairs, partStart, partEnd));
        }

        return options;
    }

    /// <summary>
    /// An entry as Where's text holds it: in quotes when it has a comma in it, as a folder's
    /// name can, which would otherwise end it there, or, in braces, make alternatives of it.
    /// </summary>
    public static string Quote(string entry)
    {
        if (entry.Contains(','))
        {
            return $"\"{entry}\"";
        }

        return entry;
    }
}
