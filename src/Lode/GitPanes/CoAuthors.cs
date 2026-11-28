/// <summary>
/// Who else wrote a commit, as GitHub Desktop adds them: a Co-authored-by trailer for each at the
/// end of the message, which GitHub shows the commit's authors from, and git reads as
/// interpret-trailers does. Typed into Changes as Name &lt;email&gt;, apart by commas, or picked
/// from the authors of the repository's history.
/// </summary>
static class CoAuthors
{
    internal const string Key = "Co-authored-by";

    /// <summary>
    /// How many commits back the authors suggested are read from: those who have committed
    /// lately are those likely to be writing with you, and reading every commit of a long
    /// history would keep the suggestions waiting.
    /// </summary>
    internal const int Commits = 10_000;

    /// <summary>
    /// How many authors are suggested at a time.
    /// </summary>
    internal const int Suggested = 8;

    internal sealed record Author(string Name, string Email)
    {
        /// <summary>
        /// As it is typed, and as the trailer has it after the key.
        /// </summary>
        public string Text => $"{Name} <{Email}>";
    }

    /// <summary>
    /// The co-authors typed, each Name &lt;email&gt;, whatever is between them, commas, semicolons
    /// or spaces, passed over, the same email twice taken once. Null where something typed isn't
    /// one: a name with no email yet, say, which the commit would otherwise lose.
    /// </summary>
    internal static List<Author>? Parse(string text)
    {
        var authors = new List<Author>();
        var rest = text.AsSpan();
        while (true)
        {
            rest = rest.TrimStart(" \t\r\n,;");
            if (rest.IsEmpty)
            {
                return authors;
            }

            var close = rest.IndexOf('>');
            if (close < 0)
            {
                return null;
            }

            var entry = rest[..close];
            var open = entry.IndexOf('<');
            if (open < 0)
            {
                return null;
            }

            var name = entry[..open].Trim();
            var email = entry[(open + 1)..].Trim();

            // Each goes on a line of its own, as git's trailers do, and git takes the email as
            // what is between the brackets.
            if (name.IsEmpty ||
                email.IsEmpty ||
                name.IndexOfAny('\r', '\n') >= 0 ||
                email.IndexOfAny(" \t\r\n<") >= 0)
            {
                return null;
            }

            var author = new Author(name.ToString(), email.ToString());
            if (!authors.Any(_ => string.Equals(_.Email, author.Email, StringComparison.OrdinalIgnoreCase)))
            {
                authors.Add(author);
            }

            rest = rest[(close + 1)..];
        }
    }

    /// <summary>
    /// <paramref name="message"/> with a Co-authored-by trailer for each co-author, in git's form:
    /// after a blank line, a paragraph of its own, or, where the message ends in trailers
    /// already, a Signed-off-by say, among them, as git interpret-trailers adds one, so git still
    /// reads them all. One the message has already isn't added again.
    /// </summary>
    internal static string WithTrailers(string message, IReadOnlyList<Author> coAuthors)
    {
        if (coAuthors.Count == 0)
        {
            return message;
        }

        // The description breaks its lines at CRLF, which git would make LF of.
        message = message.ReplaceLineEndings("\n").TrimEnd();
        var paragraphStart = message.LastIndexOf("\n\n", StringComparison.Ordinal);
        var trailersAlready = paragraphStart >= 0 &&
                              IsTrailerBlock(message[(paragraphStart + 2)..]);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (trailersAlready)
        {
            foreach (var line in message[(paragraphStart + 2)..].Split('\n'))
            {
                existing.Add(Normalise(line));
            }
        }

        var lines = coAuthors
            .Select(_ => $"{Key}: {_.Text}")
            .Where(_ => !existing.Contains(Normalise(_)))
            .ToList();
        if (lines.Count == 0)
        {
            return message;
        }

        var separator = "\n\n";
        if (trailersAlready)
        {
            separator = "\n";
        }

        return message + separator + string.Join('\n', lines);

        // Trailers git takes as the same, spaced differently.
        static string Normalise(string line) =>
            Regex.Replace(line.Trim(), @"\s*:\s*", ": ");
    }

    /// <summary>
    /// Whether <paramref name="paragraph"/> is trailers, as git takes a message's last paragraph
    /// to be: every line a token, of letters, digits and dashes, a colon and a value, or a line
    /// carrying the one before it on, indented. Git takes some paragraphs that are only partly
    /// trailers as trailers too; one that is wholly is one git takes so.
    /// </summary>
    internal static bool IsTrailerBlock(string paragraph)
    {
        var lines = paragraph.Split('\n');
        if (lines.Length == 0 ||
            lines[0].Length == 0 ||
            char.IsWhiteSpace(lines[0][0]))
        {
            return false;
        }

        return lines.All(_ => _.Length > 0 && (char.IsWhiteSpace(_[0]) || trailerLine.IsMatch(_)));
    }

    static readonly Regex trailerLine = new(@"^[A-Za-z0-9-]+\s*:", RegexOptions.CultureInvariant);

    /// <summary>
    /// A description, as a commit's message is put back to commit again, with the co-authors its
    /// trailers name taken out, for the co-authors field, apart by commas: the description without
    /// them, its lines ending in LF, and its trailers paragraph gone where they were all it had.
    /// </summary>
    internal static (string Description, string CoAuthors) Take(string description)
    {
        var text = description.ReplaceLineEndings("\n").Trim();
        var paragraphStart = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        var before = "";
        var paragraph = text;
        if (paragraphStart >= 0)
        {
            before = text[..paragraphStart];
            paragraph = text[(paragraphStart + 2)..];
        }

        if (!IsTrailerBlock(paragraph))
        {
            return (description, "");
        }

        var kept = new List<string>();
        var taken = new List<Author>();
        foreach (var line in paragraph.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 &&
                line[..colon].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase) &&
                Parse(line[(colon + 1)..]) is [var author])
            {
                taken.Add(author);
                continue;
            }

            kept.Add(line);
        }

        if (taken.Count == 0)
        {
            return (description, "");
        }

        var rest = before;
        if (kept.Count > 0)
        {
            rest = string.Join('\n', kept);
            if (before.Length > 0)
            {
                rest = $"{before}\n\n{rest}";
            }
        }

        return (rest, string.Join(", ", taken.Select(_ => _.Text)));
    }

    /// <summary>
    /// Who has committed to the repository lately, as its history names them, a .mailmap mapping
    /// names and emails as git log's %aN and %aE do: newest first, each email once, and not the
    /// one committing now, who needs no trailer. None where git can't say, before the first
    /// commit say.
    /// </summary>
    internal static IReadOnlyList<Author> Read(string repo, Cancel cancel)
    {
        var log = CodeCommands.GitOutput(
            repo,
            cancel,
            [
                "--no-optional-locks",
                "log",
                "-z",
                "--no-show-signature",
                "--encoding=UTF-8",
                "--format=%aN%x1f%aE",
                $"--max-count={Commits}",
                "HEAD",
                "--"
            ]);
        if (!log.Succeeded)
        {
            return [];
        }

        string? self = null;
        var ident = CodeCommands.GitOutput(repo, cancel, ["var", "GIT_AUTHOR_IDENT"]);
        if (ident.Succeeded)
        {
            self = EmailOf(ident.Output);
        }

        return ParseLog(log.Output, self);
    }

    /// <summary>
    /// What git log -z said, in the format <see cref="Read"/> asks for, less
    /// <paramref name="self"/>'s email.
    /// </summary>
    internal static List<Author> ParseLog(string output, string? self)
    {
        var authors = new List<Author>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (self != null)
        {
            seen.Add(self);
        }

        foreach (var record in output.Split('\0'))
        {
            var fields = record.Trim('\n').Split('\u001f');
            if (fields.Length != 2)
            {
                continue;
            }

            var name = fields[0].Trim();
            var email = fields[1].Trim();
            if (name.Length == 0 ||
                email.Length == 0 ||
                !seen.Add(email))
            {
                continue;
            }

            authors.Add(new(name, email));
        }

        return authors;
    }

    // The email of an identity as git var gives it: Name <email> 1700000000 +0000.
    static string? EmailOf(string ident)
    {
        var open = ident.IndexOf('<');
        var close = ident.IndexOf('>', open + 1);
        if (open < 0 ||
            close < 0)
        {
            return null;
        }

        return ident[(open + 1)..close];
    }

    /// <summary>
    /// What is being typed at <paramref name="caret"/>: where it starts and ends, after the
    /// co-author before it and up to the comma after it, and the authors it matches, as the
    /// command palette matches, its words in their name or email, but for those typed already.
    /// None where nothing is typed there, or it is a co-author typed whole.
    /// </summary>
    internal static (int Start, int End, IReadOnlyList<Author> Matches) Suggest(IReadOnlyList<Author> authors, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = 0;
        if (caret > 0)
        {
            start = text.LastIndexOfAny([',', ';', '>'], caret - 1) + 1;
        }

        while (start < caret &&
               char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = text.IndexOfAny([',', ';'], caret);
        if (end < 0)
        {
            end = text.Length;
        }

        var typing = text[start..end];
        if (typing.Trim().Length == 0 ||
            typing.Contains('>'))
        {
            return (start, end, []);
        }

        var entered = Entered(text);
        var candidates = authors
            .Where(_ => !entered.Contains(_.Email))
            .ToList();
        var matches = CommandPalette.Match(candidates, _ => _.Text, typing.Replace('<', ' '))
            .Take(Suggested)
            .ToList();
        return (start, end, matches);
    }

    // The emails of the co-authors typed whole, wherever they are.
    static HashSet<string> Entered(string text)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "<([^<>]+)>"))
        {
            emails.Add(match.Groups[1].Value.Trim());
        }

        return emails;
    }

    /// <summary>
    /// <paramref name="text"/> with what was being typed, from <paramref name="start"/> to
    /// <paramref name="end"/>, put in place of by <paramref name="author"/>, and a comma after, for
    /// the next: the text, and where the caret goes, after the comma.
    /// </summary>
    internal static (string Text, int Caret) Pick(string text, int start, int end, Author author)
    {
        var before = text[..start];
        if (before.TrimEnd().EndsWith('>'))
        {
            before = before.TrimEnd() + ", ";
        }

        var after = text[end..].TrimStart(' ', ',', ';');
        var picked = before + author.Text + ", ";
        return (picked + after, picked.Length);
    }
}
