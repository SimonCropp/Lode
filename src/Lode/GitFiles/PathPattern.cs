/// <summary>
/// A pattern of a .gitignore or .gitattributes line, as git's parse_path_pattern reads one: ! for
/// one that un-ignores, a trailing slash for one that only matches a folder, one without a slash
/// matching a name in any folder, one with matching from the folder of the file it is in.
/// </summary>
sealed class PathPattern
{
    public const int Negative = 1;
    public const int MustBeFolder = 2;
    public const int NoFolder = 4;
    public const int EndsWith = 8;

    public string Text { get; }
    public int Flags { get; }

    // How much of the pattern is before its first wildcard, matched as it is.
    readonly int literal;

    /// <summary>
    /// The folder of the file the pattern is from, from the top of the repository, with no slash
    /// at the end: "" for the top, or a file outside the working tree.
    /// </summary>
    public string Base { get; }

    PathPattern(string text, int flags, int literal, string @base)
    {
        Text = text;
        Flags = flags;
        this.literal = literal;
        Base = @base;
    }

    public static PathPattern Parse(string pattern, string @base)
    {
        var flags = 0;
        var text = pattern.AsSpan();
        if (text.StartsWith('!'))
        {
            flags |= Negative;
            text = text[1..];
        }

        var length = text.Length;
        if (length > 0 &&
            text[length - 1] == '/')
        {
            length--;
            flags |= MustBeFolder;
        }

        if (!text[..length].Contains('/'))
        {
            flags |= NoFolder;
        }

        var literal = Math.Min(SimpleLength(text), length);
        if (text.Length > 0 &&
            text[0] == '*' &&
            SimpleLength(text[1..]) == text.Length - 1)
        {
            flags |= EndsWith;
        }

        return new(text[..length].ToString(), flags, literal, @base);
    }

    // How many characters come before the first a glob takes as its own.
    static int SimpleLength(CharSpan text)
    {
        var index = text.IndexOfAny("*?[\\");
        if (index < 0)
        {
            return text.Length;
        }

        return index;
    }

    /// <summary>
    /// Whether the pattern matches <paramref name="path"/>, from the top of the repository, its
    /// name the part after <paramref name="nameStart"/>.
    /// </summary>
    public bool Matches(string path, int nameStart, bool folder, bool ignoreCase)
    {
        if ((Flags & MustBeFolder) != 0 &&
            !folder)
        {
            return false;
        }

        if ((Flags & NoFolder) != 0)
        {
            return MatchName(path.AsSpan(nameStart), ignoreCase);
        }

        return MatchPath(path, ignoreCase);
    }

    // As git's match_basename.
    bool MatchName(CharSpan name, bool ignoreCase)
    {
        var comparison = Comparison(ignoreCase);
        if (literal == Text.Length)
        {
            return name.Equals(Text, comparison);
        }

        if ((Flags & EndsWith) != 0)
        {
            return name.EndsWith(Text.AsSpan(1), comparison);
        }

        return Wildmatch.Match(Text, name, CaseFold(ignoreCase));
    }

    // As git's match_pathname: below the pattern's folder, its part before a wildcard as it is,
    // then the rest by wildmatch, * not crossing a slash.
    bool MatchPath(string path, bool ignoreCase)
    {
        var pattern = Text.AsSpan();
        var prefix = literal;
        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
            prefix--;
        }

        var comparison = Comparison(ignoreCase);
        var name = path.AsSpan();
        // A pattern of a folder's file is only ever tried on what is in that folder.
        if (Base.Length > 0)
        {
            name = name[(Base.Length + 1)..];
        }

        if (prefix > 0)
        {
            if (prefix > name.Length ||
                !name[..prefix].Equals(pattern[..prefix], comparison))
            {
                return false;
            }

            pattern = pattern[prefix..];
            name = name[prefix..];
            if (pattern.Length == 0 &&
                name.Length == 0)
            {
                return true;
            }
        }

        return Wildmatch.Match(pattern, name, Wildmatch.PathName | CaseFold(ignoreCase));
    }

    static StringComparison Comparison(bool ignoreCase)
    {
        if (ignoreCase)
        {
            return StringComparison.OrdinalIgnoreCase;
        }

        return StringComparison.Ordinal;
    }

    static int CaseFold(bool ignoreCase)
    {
        if (ignoreCase)
        {
            return Wildmatch.CaseFold;
        }

        return 0;
    }

    /// <summary>
    /// The lines of a .gitignore, as git's add_patterns_from_buffer takes them, its BOM passed
    /// over as it was read: none blank or starting #, a carriage return before the line break
    /// dropped, and spaces at the end unless escaped.
    /// </summary>
    public static IEnumerable<string> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 ||
                raw[0] == '#')
            {
                continue;
            }

            var line = TrimTrailingSpaces(raw.TrimEnd('\r'));
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    static string TrimTrailingSpaces(string line)
    {
        var lastSpace = -1;
        for (var index = 0; index < line.Length; index++)
        {
            switch (line[index])
            {
                case ' ':
                    if (lastSpace < 0)
                    {
                        lastSpace = index;
                    }

                    break;
                case '\\':
                    index++;
                    if (index >= line.Length)
                    {
                        return line;
                    }

                    lastSpace = -1;
                    break;
                default:
                    lastSpace = -1;
                    break;
            }
        }

        if (lastSpace >= 0)
        {
            return line[..lastSpace];
        }

        return line;
    }
}
