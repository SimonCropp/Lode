/// <summary>
/// Finds what Find in Files looks for in a file's text: plain text, in any case unless asked
/// otherwise, looked for with what the processor can do many characters of at once, or a .NET
/// regular expression, with ^ and $ at each line's start and end, and \n either line ending,
/// however the file ends its lines. Whole word wants neither a letter, a digit nor an underscore
/// either side.
/// </summary>
sealed class Matcher
{
    // How long a regular expression may take over one file before it is let go of, as one that
    // backtracks without end would never finish.
    static TimeSpan regexTimeout = TimeSpan.FromSeconds(2);

    string find;
    bool wholeWord;
    SearchValues<string>? anyCase;

    // Plain text that is all ASCII, as bytes, for looking through a file's bytes before it is
    // decoded: and its first letter in either case.
    byte[]? asciiFind;
    SearchValues<byte>? asciiFirst;

    // Whether it has an i or an s, which, in any case, ı and ſ match: in .NET's ordinal any-case
    // comparison, those are the only characters beyond ASCII whose capitals are in it.
    bool hasIOrS;

    /// <summary>
    /// The regular expression, for a regex query: Replace uses it, for $1 and the like.
    /// </summary>
    public Regex? Regex { get; }

    Matcher(string find, bool wholeWord, SearchValues<string>? anyCase, Regex? regex)
    {
        this.find = find;
        this.wholeWord = wholeWord;
        this.anyCase = anyCase;
        Regex = regex;
        if (regex == null &&
            Ascii.IsValid(find))
        {
            asciiFind = Encoding.ASCII.GetBytes(find);
            var first = (byte) find[0];
            asciiFirst = SearchValues.Create((byte) char.ToLowerInvariant((char) first), (byte) char.ToUpperInvariant((char) first));
            hasIOrS = find.AsSpan().IndexOfAny("iIsS") >= 0;
        }
    }

    /// <summary>
    /// Whether <see cref="MayBeIn"/> can ever say a file holds no match: only for plain text
    /// that is all ASCII.
    /// </summary>
    public bool CanRuleOut => asciiFind != null;

    /// <summary>
    /// How far apart the parts a big file is looked through in must overlap, so a match across
    /// two is seen, and ı or ſ across two too.
    /// </summary>
    public int Overlap => Math.Max(1, (asciiFind?.Length ?? 0) - 1);

    // ı and ſ in UTF-8.
    static ByteSpan DotlessI => "ı"u8;
    static ByteSpan LongS => "ſ"u8;

    /// <summary>
    /// Whether a file's bytes could hold a match, looked through before they are decoded, as
    /// most files hold none, and decoding is most of the work of looking. False only where
    /// that is certain: for plain ASCII text, in a file in UTF-8 or Latin-1, in each of which an
    /// ASCII character is the one byte, and none of a character beyond it is one. In any case,
    /// beyond ASCII only ı matches an i, and ſ an s, so a file with either is decoded.
    /// </summary>
    /// <param name="atStart">The bytes are from the file's start, where a UTF-16 mark would be:
    /// a big file is looked through a part at a time.</param>
    public bool MayBeIn(ByteSpan bytes, bool atStart = true)
    {
        if (asciiFind == null ||
            (atStart &&
             (bytes.StartsWith(TextFile.Utf16Mark) ||
              bytes.StartsWith(TextFile.Utf16BigEndianMark))))
        {
            return true;
        }

        if (anyCase == null)
        {
            return bytes.IndexOf(asciiFind) >= 0;
        }

        if (hasIOrS &&
            (bytes.IndexOf(DotlessI) >= 0 ||
             bytes.IndexOf(LongS) >= 0))
        {
            return true;
        }

        var from = 0;
        while (from <= bytes.Length - asciiFind.Length)
        {
            var found = bytes[from..].IndexOfAny(asciiFirst!);
            if (found < 0)
            {
                return false;
            }

            var start = from + found;
            if (start + asciiFind.Length <= bytes.Length &&
                Ascii.EqualsIgnoreCase(bytes.Slice(start, asciiFind.Length), asciiFind))
            {
                return true;
            }

            from = start + 1;
        }

        return false;
    }

    /// <summary>
    /// What finds <paramref name="query"/>'s text, or null and, as <paramref name="error"/>,
    /// why there is none: nothing to find, or a pattern .NET can't read.
    /// </summary>
    public static Matcher? Create(FindQuery query, out string? error)
    {
        error = null;
        if (query.Find.Length == 0)
        {
            error = "Type what to find.";
            return null;
        }

        if (query.Regex)
        {
            var pattern = EitherLineEnding(query.Find);
            if (query.WholeWord)
            {
                pattern = $@"\b(?:{pattern})\b";
            }

            // Compiled, as it is made once a search and run over every file searched, whole.
            // AnyNewLine, as most files here end their lines as Windows does: without it $ is
            // only before a line feed, so after the carriage return, where nothing typed ends,
            // and . takes the carriage return, which Replace would then take from the line.
            var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.AnyNewLine | RegexOptions.Compiled;
            if (!query.CaseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            try
            {
                return new(query.Find, false, null, new(pattern, options, regexTimeout));
            }
            catch (ArgumentException exception)
            {
                error = $"The pattern can't be read: {exception.Message}";
                return null;
            }
        }

        SearchValues<string>? anyCase = null;
        if (!query.CaseSensitive)
        {
            anyCase = SearchValues.Create([query.Find], StringComparison.OrdinalIgnoreCase);
        }

        return new(query.Find, query.WholeWord, anyCase, null);
    }

    /// <summary>
    /// <paramref name="pattern"/> with each \n in it matching a line ended as Windows ends it as
    /// well as one ended with a line feed alone, as VS Code's search does: \r?\n, so foo\nbar
    /// finds foo at the end of one line and bar at the start of the next, however the file ends
    /// its lines. In a class, [^\n] say, a carriage return goes beside it, so [^\n]* stops
    /// before a Windows line ending rather than taking its carriage return. \\n is a backslash
    /// and an n, and is left as it is.
    /// </summary>
    internal static string EitherLineEnding(string pattern)
    {
        if (!pattern.Contains(@"\n", StringComparison.Ordinal))
        {
            return pattern;
        }

        var builder = new StringBuilder(pattern.Length + 16);

        // How deep in classes it is: .NET's subtraction, [a-z-[aeiou]], puts one in another.
        var depth = 0;
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\' &&
                index + 1 < pattern.Length)
            {
                index++;
                var escaped = pattern[index];
                if (escaped != 'n')
                {
                    builder.Append(character).Append(escaped);
                }
                else if (depth > 0)
                {
                    builder.Append(@"\r\n");
                }
                else
                {
                    // In a group of its own, so a quantifier after it, \n+ say, is of both.
                    builder.Append(@"(?:\r?\n)");
                }

                continue;
            }

            builder.Append(character);
            if (character == '[' &&
                (depth == 0 || pattern[index - 1] == '-'))
            {
                depth++;

                // A ] first in a class, or first after its ^, is one of its characters rather
                // than its end.
                if (index + 1 < pattern.Length &&
                    pattern[index + 1] == '^')
                {
                    index++;
                    builder.Append('^');
                }

                if (index + 1 < pattern.Length &&
                    pattern[index + 1] == ']')
                {
                    index++;
                    builder.Append(']');
                }
            }
            else if (character == ']' &&
                     depth > 0)
            {
                depth--;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds where each match is in <paramref name="text"/> to <paramref name="matches"/>, in
    /// order. A regular expression taking too long throws
    /// <see cref="RegexMatchTimeoutException"/>.
    /// </summary>
    public void Find(CharSpan text, List<(int Start, int Length)> matches)
    {
        if (Regex != null)
        {
            foreach (var match in Regex.EnumerateMatches(text))
            {
                matches.Add((match.Index, match.Length));
            }

            return;
        }

        var from = 0;
        while (from <= text.Length - find.Length)
        {
            var rest = text[from..];
            int found;
            if (anyCase != null)
            {
                found = rest.IndexOfAny(anyCase);
            }
            else
            {
                found = rest.IndexOf(find, StringComparison.Ordinal);
            }

            if (found < 0)
            {
                return;
            }

            var start = from + found;
            if (wholeWord &&
                !IsWholeWord(text, start, find.Length))
            {
                from = start + 1;
                continue;
            }

            matches.Add((start, find.Length));
            from = start + find.Length;
        }
    }

    static bool IsWholeWord(CharSpan text, int start, int length)
    {
        if (start > 0 &&
            IsWordCharacter(text[start - 1]))
        {
            return false;
        }

        var end = start + length;
        return end >= text.Length ||
               !IsWordCharacter(text[end]);
    }

    static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) ||
        character == '_';
}
