using System.Collections.Frozen;

/// <summary>
/// What a run of a line's characters is, for the colour it is drawn in, as github.com colours
/// code with Primer's pl- classes: a keyword, a string, a comment, a constant (a number, true,
/// null, a JSON key, a CSS property and its value), a tag in markup or a key in YAML, an
/// attribute in markup or a class in CSS, or a variable. The rest is plain, the line's own colour.
/// </summary>
enum SyntaxKind : byte
{
    Plain,
    Keyword,
    String,
    Comment,
    Constant,
    Tag,
    Attribute,
    Variable
}

/// <summary>
/// A run of a line's characters of one <see cref="SyntaxKind"/>, from its start.
/// </summary>
readonly record struct SyntaxRun(int Start, int Length, SyntaxKind Kind)
{
    public int End => Start + Length;
}

/// <summary>
/// A language's colours, a line at a time, by a small tokenizer of its own rather than a parser:
/// what each run of a line is, from the state the line before ended in, and the state it ends in,
/// for what goes on over lines, a block comment, a verbatim or raw string, a tag's attributes.
/// Each state is the language's own; 0 is code, outside anything that goes on. Picked by the
/// file's extension: C#, JavaScript and TypeScript, JSON, XML and HTML, XAML and the project
/// files, CSS, SCSS and Less, Python, PowerShell and YAML.
/// </summary>
abstract class SyntaxLanguage
{
    public abstract string Name { get; }

    /// <summary>
    /// Adds the runs of <paramref name="line"/>, started in <paramref name="state"/>, to
    /// <paramref name="runs"/>, in order, only those not plain, unless it is null, when only
    /// the state it ends in is wanted. Gives that state, for the line after.
    /// </summary>
    public abstract int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs);

    /// <summary>
    /// How the language's block comments open and close, and the state inside one, for a guess
    /// at where lines start whose start isn't known: those of a hunk, the file around it unread.
    /// Null for a language without them.
    /// </summary>
    public virtual (string Open, string Close, int State)? BlockComment => null;

    static readonly FrozenDictionary<string, SyntaxLanguage> byExtension = Extensions();

    static FrozenDictionary<string, SyntaxLanguage> Extensions()
    {
        var languages = new Dictionary<string, SyntaxLanguage>(StringComparer.OrdinalIgnoreCase);
        void Add(SyntaxLanguage language, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                languages[extension] = language;
            }
        }

        Add(new CSharpSyntax(), ".cs", ".csx");
        Add(new ScriptSyntax("JavaScript", typeScript: false), ".js", ".mjs", ".cjs", ".jsx");
        Add(new ScriptSyntax("TypeScript", typeScript: true), ".ts", ".mts", ".cts", ".tsx");
        Add(new JsonSyntax(), ".json", ".jsonc", ".json5", ".jsonl", ".webmanifest");
        Add(
            new MarkupSyntax("XML", html: false),
            ".xml", ".xaml", ".axaml", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj", ".props", ".targets",
            ".nuspec", ".config", ".resx", ".xsd", ".xsl", ".xslt", ".svg", ".slnx", ".manifest", ".ruleset",
            ".runsettings", ".wxs", ".wxi", ".plist", ".pubxml", ".appxmanifest", ".natvis", ".xlf");
        Add(new MarkupSyntax("HTML", html: true), ".html", ".htm", ".xhtml", ".cshtml", ".razor", ".vue");
        Add(new StyleSyntax("CSS", lineComments: false), ".css");
        Add(new StyleSyntax("SCSS", lineComments: true), ".scss", ".sass", ".less");
        Add(new PythonSyntax(), ".py", ".pyw", ".pyi");
        Add(new PowerShellSyntax(), ".ps1", ".psm1", ".psd1");
        Add(new YamlSyntax(), ".yml", ".yaml");
        return languages.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The language of the file at <paramref name="path"/>, by its extension. Null for one Lode
    /// has no colours for.
    /// </summary>
    public static SyntaxLanguage? For(string path) =>
        byExtension.GetValueOrDefault(Path.GetExtension(path));

    // A run added, unless runs aren't wanted, or it is plain. Beside the last of the same kind,
    // with only whitespace between them, it makes one with it, drawn at once.
    protected static void Add(List<SyntaxRun>? runs, CharSpan line, int start, int end, SyntaxKind kind)
    {
        if (runs == null ||
            end <= start ||
            kind == SyntaxKind.Plain)
        {
            return;
        }

        if (runs.Count > 0)
        {
            var last = runs[^1];
            if (last.Kind == kind &&
                last.End <= start &&
                line[last.End..start].IsWhiteSpace())
            {
                runs[^1] = last with
                {
                    Length = end - last.Start
                };
                return;
            }
        }

        runs.Add(new(start, end - start, kind));
    }

    protected static FrozenSet<string> Words(string words, bool ignoreCase = false)
    {
        var comparer = StringComparer.Ordinal;
        if (ignoreCase)
        {
            comparer = StringComparer.OrdinalIgnoreCase;
        }

        return words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(comparer);
    }

    // Letters, digits and underscores, and anything beyond ASCII, so a word of another alphabet,
    // or a letter with a mark on it, is one word, never split.
    protected static bool IsWordStart(char character) =>
        char.IsAsciiLetter(character) ||
        character == '_' ||
        character >= 0x80;

    protected static bool IsWordPart(char character) =>
        char.IsAsciiLetterOrDigit(character) ||
        character == '_' ||
        character >= 0x80;

    protected static int WordEnd(CharSpan line, int index)
    {
        while (index < line.Length &&
               IsWordPart(line[index]))
        {
            index++;
        }

        return index;
    }

    // Whether a number starts at index: a digit, or a point before one that follows no word.
    protected static bool IsNumberStart(CharSpan line, int index)
    {
        var character = line[index];
        if (char.IsAsciiDigit(character))
        {
            return true;
        }

        return character == '.' &&
               index + 1 < line.Length &&
               char.IsAsciiDigit(line[index + 1]) &&
               (index == 0 || !IsWordPart(line[index - 1]));
    }

    // The end of a number from index, as most languages write one: 0x, 0b or 0o and its digits,
    // or digits with underscores between them, a fraction and an exponent; then the letters
    // that say its type, 1L, 2.5f, 10m, 10n, 3j.
    protected static int NumberEnd(CharSpan line, int index)
    {
        var at = index;
        if (at + 1 < line.Length &&
            line[at] == '0' &&
            (char) (line[at + 1] | 0x20) is 'x' or 'b' or 'o')
        {
            at += 2;
            while (at < line.Length &&
                   (char.IsAsciiHexDigit(line[at]) || line[at] == '_'))
            {
                at++;
            }
        }
        else
        {
            at = DigitsEnd(line, at);
            if (at + 1 < line.Length &&
                line[at] == '.' &&
                char.IsAsciiDigit(line[at + 1]))
            {
                at = DigitsEnd(line, at + 1);
            }

            if (at < line.Length &&
                (char) (line[at] | 0x20) == 'e')
            {
                var exponent = at + 1;
                if (exponent < line.Length &&
                    line[exponent] is '+' or '-')
                {
                    exponent++;
                }

                if (exponent < line.Length &&
                    char.IsAsciiDigit(line[exponent]))
                {
                    at = DigitsEnd(line, exponent);
                }
            }
        }

        while (at < line.Length &&
               char.IsAsciiLetter(line[at]))
        {
            at++;
        }

        return at;
    }

    static int DigitsEnd(CharSpan line, int index)
    {
        while (index < line.Length &&
               (char.IsAsciiDigit(line[index]) || line[index] == '_'))
        {
            index++;
        }

        return index;
    }

    // The end of a string from just after its opening quote: just after its closing quote, a
    // backslash, where it escapes, escaping what follows it; or the line's end, not closed.
    protected static (int End, bool Closed) QuotedEnd(CharSpan line, int index, char quote, bool backslashes = true)
    {
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '\\' &&
                backslashes)
            {
                index += 2;
                continue;
            }

            if (character == quote)
            {
                return (index + 1, true);
            }

            index++;
        }

        return (line.Length, false);
    }

    // A comment that closes with close, from index to just after its close, or to the line's end.
    protected static (int End, bool Closed) CommentEnd(CharSpan line, int index, string close)
    {
        var at = line[index..].IndexOf(close, StringComparison.Ordinal);
        if (at < 0)
        {
            return (line.Length, false);
        }

        return (index + at + close.Length, true);
    }

    // Whether only whitespace is before index on the line: it starts a statement there.
    protected static bool StartsLine(CharSpan line, int index) =>
        line[..index].IsWhiteSpace();
}

/// <summary>
/// C#: its keywords, those of its contextual keywords seldom a name, strings regular, verbatim,
/// interpolated and raw, characters, numbers, comments, and preprocessor directives.
/// </summary>
sealed class CSharpSyntax : SyntaxLanguage
{
    const int blockComment = 1;
    const int verbatim = 2;
    const int interpolatedVerbatim = 3;

    // A raw string, with how many quotes close it above the state's low four bits.
    const int raw = 4;

    static readonly FrozenSet<string> keywords = Words(
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do " +
        "double else enum event explicit extern finally fixed float for foreach goto if implicit in int interface " +
        "internal is lock long namespace new object operator out override params private protected public readonly " +
        "ref return sbyte sealed short sizeof stackalloc static string struct switch this throw try typeof uint " +
        "ulong unchecked unsafe ushort using virtual void volatile while " +
        "and or not async await var dynamic nameof partial record required init when where yield get set global " +
        "unmanaged notnull managed scoped with nint nuint");

    static readonly FrozenSet<string> constants = Words("true false null");

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> keywordLookup = keywords.GetAlternateLookup<CharSpan>();
    static readonly FrozenSet<string>.AlternateLookup<CharSpan> constantLookup = constants.GetAlternateLookup<CharSpan>();

    // Directives whose rest is text, not code.
    static readonly FrozenSet<string>.AlternateLookup<CharSpan> textDirectives =
        Words("region endregion pragma error warning line nullable").GetAlternateLookup<CharSpan>();

    public override string Name => "C#";

    public override (string Open, string Close, int State)? BlockComment => ("/*", "*/", blockComment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var index = 0;
        switch (state & 0xF)
        {
            case blockComment:
            {
                var (end, closed) = CommentEnd(line, 0, "*/");
                Add(runs, line, 0, end, SyntaxKind.Comment);
                if (!closed)
                {
                    return state;
                }

                index = end;
                break;
            }
            case verbatim:
            case interpolatedVerbatim:
            {
                var (end, closed) = StringEnd(line, 0, verbatim: true, interpolated: (state & 0xF) == interpolatedVerbatim);
                Add(runs, line, 0, end, SyntaxKind.String);
                if (!closed)
                {
                    return state;
                }

                index = end;
                break;
            }
            case raw:
            {
                var (end, closed) = RawEnd(line, 0, state >> 4);
                Add(runs, line, 0, end, SyntaxKind.String);
                if (!closed)
                {
                    return state;
                }

                index = end;
                break;
            }
        }

        while (index < line.Length)
        {
            var character = line[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '/' &&
                index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    Add(runs, line, index, line.Length, SyntaxKind.Comment);
                    return 0;
                }

                if (line[index + 1] == '*')
                {
                    var (end, closed) = CommentEnd(line, index + 2, "*/");
                    Add(runs, line, index, end, SyntaxKind.Comment);
                    if (!closed)
                    {
                        return blockComment;
                    }

                    index = end;
                    continue;
                }
            }

            if (character == '#' &&
                StartsLine(line, index))
            {
                var end = WordEnd(line, index + 1);
                Add(runs, line, index, end, SyntaxKind.Keyword);
                if (textDirectives.Contains(line[(index + 1)..end]))
                {
                    return 0;
                }

                index = end;
                continue;
            }

            if (character is '"' or '\'' or '$' or '@' &&
                String(line, index, runs) is { } literal)
            {
                if (literal.State != 0)
                {
                    return literal.State;
                }

                index = literal.End;
                continue;
            }

            if (IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index);
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = end;
                continue;
            }

            // @class is a name, not a keyword; nor is what follows a dot, a member.
            if (character == '@' &&
                index + 1 < line.Length &&
                IsWordStart(line[index + 1]))
            {
                index = WordEnd(line, index + 1);
                continue;
            }

            if (IsWordStart(character))
            {
                var end = WordEnd(line, index);
                if (runs != null &&
                    (index == 0 || line[index - 1] != '.'))
                {
                    var word = line[index..end];
                    if (keywordLookup.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Keyword);
                    }
                    else if (constantLookup.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Constant);
                    }
                }

                index = end;
                continue;
            }

            index++;
        }

        return 0;
    }

    // A string or a character starting at index, after any $ and @ before its quote: where it
    // ends, and, where it goes on past the line, the state the next line starts in. Null for
    // none there.
    static (int End, int State)? String(CharSpan line, int index, List<SyntaxRun>? runs)
    {
        if (line[index] == '\'')
        {
            var (characterEnd, _) = QuotedEnd(line, index + 1, '\'');
            Add(runs, line, index, characterEnd, SyntaxKind.String);
            return (characterEnd, 0);
        }

        var at = index;
        var interpolated = false;
        var isVerbatim = false;
        while (at < line.Length &&
               line[at] is '$' or '@')
        {
            if (line[at] == '$')
            {
                interpolated = true;
            }
            else
            {
                isVerbatim = true;
            }

            at++;
        }

        if (at >= line.Length ||
            line[at] != '"')
        {
            return null;
        }

        var quotes = 0;
        while (at + quotes < line.Length &&
               line[at + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            var (rawEnd, rawClosed) = RawEnd(line, at + quotes, quotes);
            Add(runs, line, index, rawEnd, SyntaxKind.String);
            if (!rawClosed)
            {
                return (rawEnd, raw | (quotes << 4));
            }

            return (rawEnd, 0);
        }

        var (end, closed) = StringEnd(line, at + 1, isVerbatim, interpolated);
        Add(runs, line, index, end, SyntaxKind.String);
        if (!closed &&
            isVerbatim)
        {
            if (interpolated)
            {
                return (end, interpolatedVerbatim);
            }

            return (end, verbatim);
        }

        return (end, 0);
    }

    // The end of a string from just after its opening quote, and whether it closed on the line. A
    // backslash escapes what follows, but in a verbatim string, where a quote is doubled; in an
    // interpolated one, what is in braces is code, whose own strings can hold quotes. A regular
    // string doesn't go on past its line, closed or not.
    static (int End, bool Closed) StringEnd(CharSpan line, int index, bool verbatim, bool interpolated)
    {
        var depth = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (depth > 0)
            {
                if (character is '"' or '\'')
                {
                    index = QuotedEnd(line, index + 1, character).End;
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                }

                index++;
                continue;
            }

            if (character == '\\' &&
                !verbatim)
            {
                index += 2;
                continue;
            }

            if (character == '"')
            {
                if (verbatim &&
                    index + 1 < line.Length &&
                    line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return (index + 1, true);
            }

            if (character == '{' &&
                interpolated)
            {
                if (index + 1 < line.Length &&
                    line[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                depth = 1;
            }

            index++;
        }

        return (line.Length, false);
    }

    // The end of a raw string from index, closed by as many quotes as opened it.
    static (int End, bool Closed) RawEnd(CharSpan line, int index, int quotes)
    {
        while (index < line.Length)
        {
            var at = line[index..].IndexOf('"');
            if (at < 0)
            {
                break;
            }

            var start = index + at;
            var end = start;
            while (end < line.Length &&
                   line[end] == '"')
            {
                end++;
            }

            if (end - start >= quotes)
            {
                return (end, true);
            }

            index = end;
        }

        return (line.Length, false);
    }
}

/// <summary>
/// JavaScript, and TypeScript with its own keywords: strings, template literals, which go on
/// over lines, regular expressions where one can start, numbers, comments.
/// </summary>
sealed class ScriptSyntax : SyntaxLanguage
{
    const int blockComment = 1;
    const int template = 2;

    static readonly FrozenSet<string> scriptKeywords = Words(
        "break case catch class const continue debugger default delete do else export extends finally for function " +
        "if import in instanceof let new return super switch this throw try typeof var void while with yield async " +
        "await of from as static");

    static readonly FrozenSet<string> typeScriptKeywords = Words(
        "abstract asserts declare enum implements infer interface is keyof namespace override private protected " +
        "public readonly satisfies unique accessor");

    // TypeScript's primitive types are constants, as GitHub's grammar has them.
    static readonly FrozenSet<string> typeScriptConstants = Words("any bigint boolean never number object string symbol unknown");

    static readonly FrozenSet<string> scriptConstants = Words("true false null undefined NaN Infinity");

    // After these a slash starts a regular expression, not a division.
    static readonly FrozenSet<string>.AlternateLookup<CharSpan> beforeExpression =
        Words("return typeof instanceof in of new delete void throw case do else yield await").GetAlternateLookup<CharSpan>();

    readonly FrozenSet<string>.AlternateLookup<CharSpan> keywords;
    readonly FrozenSet<string>.AlternateLookup<CharSpan> constants;

    public ScriptSyntax(string name, bool typeScript)
    {
        Name = name;
        var keywordSet = scriptKeywords;
        var constantSet = scriptConstants;
        if (typeScript)
        {
            keywordSet = scriptKeywords.Concat(typeScriptKeywords).ToFrozenSet(StringComparer.Ordinal);
            constantSet = scriptConstants.Concat(typeScriptConstants).ToFrozenSet(StringComparer.Ordinal);
        }

        keywords = keywordSet.GetAlternateLookup<CharSpan>();
        constants = constantSet.GetAlternateLookup<CharSpan>();
    }

    public override string Name { get; }

    public override (string Open, string Close, int State)? BlockComment => ("/*", "*/", blockComment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var index = 0;
        if (state == blockComment)
        {
            var (end, closed) = CommentEnd(line, 0, "*/");
            Add(runs, line, 0, end, SyntaxKind.Comment);
            if (!closed)
            {
                return state;
            }

            index = end;
        }
        else if (state == template)
        {
            var (end, closed) = TemplateEnd(line, 0);
            Add(runs, line, 0, end, SyntaxKind.String);
            if (!closed)
            {
                return state;
            }

            index = end;
        }

        // Whether a slash here would start a regular expression: at the start, and after an
        // operator, but not after a name, a number, a string or a closing bracket.
        var expression = true;
        while (index < line.Length)
        {
            var character = line[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '/')
            {
                if (index + 1 < line.Length &&
                    line[index + 1] == '/')
                {
                    Add(runs, line, index, line.Length, SyntaxKind.Comment);
                    return 0;
                }

                if (index + 1 < line.Length &&
                    line[index + 1] == '*')
                {
                    var (end, closed) = CommentEnd(line, index + 2, "*/");
                    Add(runs, line, index, end, SyntaxKind.Comment);
                    if (!closed)
                    {
                        return blockComment;
                    }

                    index = end;
                    continue;
                }

                if (expression &&
                    RegexEnd(line, index + 1) is > 0 and var regexEnd)
                {
                    Add(runs, line, index, regexEnd, SyntaxKind.String);
                    index = regexEnd;
                    expression = false;
                    continue;
                }

                expression = true;
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                var (end, _) = QuotedEnd(line, index + 1, character);
                Add(runs, line, index, end, SyntaxKind.String);
                index = end;
                expression = false;
                continue;
            }

            if (character == '`')
            {
                var (end, closed) = TemplateEnd(line, index + 1);
                Add(runs, line, index, end, SyntaxKind.String);
                if (!closed)
                {
                    return template;
                }

                index = end;
                expression = false;
                continue;
            }

            if (IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index);
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = end;
                expression = false;
                continue;
            }

            if (IsWordStart(character) ||
                character == '$')
            {
                var end = index + 1;
                while (end < line.Length &&
                       (IsWordPart(line[end]) || line[end] == '$'))
                {
                    end++;
                }

                var word = line[index..end];
                var member = index > 0 &&
                             line[index - 1] == '.';
                expression = !member && beforeExpression.Contains(word);
                if (runs != null &&
                    !member)
                {
                    if (keywords.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Keyword);
                    }
                    else if (constants.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Constant);
                    }
                }

                index = end;
                continue;
            }

            expression = character is not (')' or ']' or '}');
            index++;
        }

        return 0;
    }

    // The end of a template literal from just after its backtick, and whether it closed on the
    // line. In ${ }, code, whose strings can hold a backtick.
    static (int End, bool Closed) TemplateEnd(CharSpan line, int index)
    {
        var depth = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }

            if (depth > 0)
            {
                if (character is '"' or '\'' or '`')
                {
                    index = QuotedEnd(line, index + 1, character).End;
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                }

                index++;
                continue;
            }

            if (character == '`')
            {
                return (index + 1, true);
            }

            if (character == '$' &&
                index + 1 < line.Length &&
                line[index + 1] == '{')
            {
                depth = 1;
                index += 2;
                continue;
            }

            index++;
        }

        return (line.Length, false);
    }

    // The end of a regular expression from just after its slash, its flags with it: the slash
    // that closes it is outside a class in square brackets. -1 for one that doesn't close on the
    // line, which is a division after all.
    static int RegexEnd(CharSpan line, int index)
    {
        var inClass = false;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }

            if (character == '[')
            {
                inClass = true;
            }
            else if (character == ']')
            {
                inClass = false;
            }
            else if (character == '/' &&
                     !inClass)
            {
                index++;
                while (index < line.Length &&
                       char.IsAsciiLetter(line[index]))
                {
                    index++;
                }

                return index;
            }

            index++;
        }

        return -1;
    }
}

/// <summary>
/// JSON, and JSON with comments: a key is a constant, as GitHub's grammar has it, beside the
/// strings that are values; numbers, true, false and null.
/// </summary>
sealed class JsonSyntax : SyntaxLanguage
{
    const int blockComment = 1;

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> constants = Words("true false null NaN Infinity").GetAlternateLookup<CharSpan>();

    public override string Name => "JSON";

    public override (string Open, string Close, int State)? BlockComment => ("/*", "*/", blockComment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var index = 0;
        if (state == blockComment)
        {
            var (end, closed) = CommentEnd(line, 0, "*/");
            Add(runs, line, 0, end, SyntaxKind.Comment);
            if (!closed)
            {
                return state;
            }

            index = end;
        }

        while (index < line.Length)
        {
            var character = line[index];
            if (character == '/' &&
                index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    Add(runs, line, index, line.Length, SyntaxKind.Comment);
                    return 0;
                }

                if (line[index + 1] == '*')
                {
                    var (end, closed) = CommentEnd(line, index + 2, "*/");
                    Add(runs, line, index, end, SyntaxKind.Comment);
                    if (!closed)
                    {
                        return blockComment;
                    }

                    index = end;
                    continue;
                }
            }

            if (character == '"')
            {
                var (end, _) = QuotedEnd(line, index + 1, '"');
                var after = end;
                while (after < line.Length &&
                       char.IsWhiteSpace(line[after]))
                {
                    after++;
                }

                var kind = SyntaxKind.String;
                if (after < line.Length &&
                    line[after] == ':')
                {
                    kind = SyntaxKind.Constant;
                }

                Add(runs, line, index, end, kind);
                index = end;
                continue;
            }

            if ((character == '-' &&
                 index + 1 < line.Length &&
                 char.IsAsciiDigit(line[index + 1])) ||
                IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index + (character == '-' ? 1 : 0));
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = end;
                continue;
            }

            if (IsWordStart(character))
            {
                var end = WordEnd(line, index);
                if (constants.Contains(line[index..end]))
                {
                    Add(runs, line, index, end, SyntaxKind.Constant);
                }

                index = end;
                continue;
            }

            index++;
        }

        return 0;
    }
}

/// <summary>
/// XML and HTML, and what is written in them, XAML and the project files: tags' names, their
/// attributes and values, comments, CDATA, and entities. A tag's attributes go on over lines, as
/// XAML's often do. In HTML, what a script or style holds is left plain, not taken for tags.
/// </summary>
sealed class MarkupSyntax(string name, bool html) : SyntaxLanguage
{
    // What a line is in, in the state's low four bits.
    const int text = 0;
    const int comment = 1;
    const int tag = 2;
    const int cdata = 3;
    const int doubleValue = 4;
    const int singleValue = 5;
    const int script = 6;
    const int style = 7;

    // In HTML, the tag being read opens a script or a style, above the low four bits.
    const int opensScript = 1 << 4;
    const int opensStyle = 2 << 4;

    public override string Name => name;

    public override (string Open, string Close, int State)? BlockComment => ("<!--", "-->", comment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var mode = state & 0xF;
        var opens = state & ~0xF;
        var index = 0;

        // After an attribute's =, a value without quotes.
        var value = false;
        while (index < line.Length)
        {
            switch (mode)
            {
                case comment:
                {
                    var (end, closed) = CommentEnd(line, index, "-->");
                    Add(runs, line, index, end, SyntaxKind.Comment);
                    index = end;
                    if (closed)
                    {
                        mode = text;
                    }

                    continue;
                }
                case cdata:
                {
                    var (end, closed) = CommentEnd(line, index, "]]>");
                    Add(runs, line, index, end, SyntaxKind.String);
                    index = end;
                    if (closed)
                    {
                        mode = text;
                    }

                    continue;
                }
                case doubleValue:
                case singleValue:
                {
                    var quote = mode == doubleValue ? '"' : '\'';
                    var (end, closed) = QuotedEnd(line, index, quote, backslashes: false);
                    Add(runs, line, index, end, SyntaxKind.String);
                    index = end;
                    if (closed)
                    {
                        mode = tag;
                    }

                    continue;
                }
                case script:
                case style:
                {
                    var close = mode == script ? "</script" : "</style";
                    var at = line[index..].IndexOf(close, StringComparison.OrdinalIgnoreCase);
                    if (at < 0)
                    {
                        return mode;
                    }

                    index += at;
                    mode = text;
                    continue;
                }
                case tag:
                {
                    var character = line[index];
                    if (char.IsWhiteSpace(character))
                    {
                        index++;
                        continue;
                    }

                    if (character == '>')
                    {
                        mode = opens switch
                        {
                            opensScript => script,
                            opensStyle => style,
                            _ => text
                        };
                        opens = 0;
                        index++;
                        continue;
                    }

                    if (character is '/' or '?' &&
                        index + 1 < line.Length &&
                        line[index + 1] == '>')
                    {
                        mode = text;
                        opens = 0;
                        index += 2;
                        continue;
                    }

                    if (character is '"' or '\'')
                    {
                        var (end, closed) = QuotedEnd(line, index + 1, character, backslashes: false);
                        Add(runs, line, index, end, SyntaxKind.String);
                        index = end;
                        value = false;
                        if (!closed)
                        {
                            mode = character == '"' ? doubleValue : singleValue;
                        }

                        continue;
                    }

                    if (character == '=')
                    {
                        value = true;
                        index++;
                        continue;
                    }

                    if (value)
                    {
                        var end = index;
                        while (end < line.Length &&
                               !char.IsWhiteSpace(line[end]) &&
                               line[end] != '>')
                        {
                            end++;
                        }

                        Add(runs, line, index, end, SyntaxKind.String);
                        index = end;
                        value = false;
                        continue;
                    }

                    if (IsNameStart(character))
                    {
                        var end = NameEnd(line, index);
                        Add(runs, line, index, end, SyntaxKind.Attribute);
                        index = end;
                        continue;
                    }

                    index++;
                    continue;
                }
            }

            // Text, to the next tag or entity.
            var next = line[index..].IndexOfAny('<', '&');
            if (next < 0)
            {
                break;
            }

            index += next;
            if (line[index] == '&')
            {
                var end = EntityEnd(line, index);
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = Math.Max(end, index + 1);
                continue;
            }

            var rest = line[index..];
            if (rest.StartsWith("<!--"))
            {
                Add(runs, line, index, index + 4, SyntaxKind.Comment);
                index += 4;
                mode = comment;
                continue;
            }

            if (rest.StartsWith("<![CDATA["))
            {
                Add(runs, line, index, index + 9, SyntaxKind.String);
                index += 9;
                mode = cdata;
                continue;
            }

            var nameStart = index + 1;
            var closing = false;
            if (nameStart < line.Length &&
                line[nameStart] is '/' or '!' or '?')
            {
                closing = line[nameStart] == '/';
                nameStart++;
            }

            if (nameStart >= line.Length ||
                !IsNameStart(line[nameStart]))
            {
                index++;
                continue;
            }

            var nameEnd = NameEnd(line, nameStart);
            var tagStart = nameStart;
            if (line[nameStart - 1] == '!')
            {
                tagStart--;
            }

            Add(runs, line, tagStart, nameEnd, SyntaxKind.Tag);
            opens = 0;
            if (html &&
                !closing)
            {
                var tagName = line[nameStart..nameEnd];
                if (tagName.Equals("script", StringComparison.OrdinalIgnoreCase))
                {
                    opens = opensScript;
                }
                else if (tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    opens = opensStyle;
                }
            }

            index = nameEnd;
            mode = tag;
            value = false;
        }

        if (mode is script or style)
        {
            return mode;
        }

        return mode | opens;
    }

    static bool IsNameStart(char character) =>
        char.IsAsciiLetter(character) ||
        character is '_' or ':' ||
        character >= 0x80;

    static int NameEnd(CharSpan line, int index)
    {
        while (index < line.Length &&
               (IsWordPart(line[index]) || line[index] is ':' or '.' or '-'))
        {
            index++;
        }

        return index;
    }

    // An entity's end, &amp; or &#160;, after its semicolon; where it isn't one, the ampersand's.
    static int EntityEnd(CharSpan line, int index)
    {
        var at = index + 1;
        if (at < line.Length &&
            line[at] == '#')
        {
            at++;
        }

        var start = at;
        while (at < line.Length &&
               at - start < 32 &&
               char.IsAsciiLetterOrDigit(line[at]))
        {
            at++;
        }

        if (at > start &&
            at < line.Length &&
            line[at] == ';')
        {
            return at + 1;
        }

        return index;
    }
}

/// <summary>
/// CSS, and SCSS and Less with their // comments: selectors' elements as tags, classes, ids and
/// pseudo-classes as attributes; in braces, properties and their values as constants, units and
/// !important as keywords, as GitHub's grammar has them; at-rules, strings, comments.
/// </summary>
sealed class StyleSyntax(string name, bool lineComments) : SyntaxLanguage
{
    // A block comment in the state's lowest bit; how deep in braces the line starts, above it.
    const int blockComment = 1;

    public override string Name => name;

    public override (string Open, string Close, int State)? BlockComment => ("/*", "*/", blockComment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var depth = state >> 1;
        var index = 0;
        if ((state & blockComment) != 0)
        {
            var (end, closed) = CommentEnd(line, 0, "*/");
            Add(runs, line, 0, end, SyntaxKind.Comment);
            if (!closed)
            {
                return state;
            }

            index = end;
        }

        // After a property's colon, its value, to the semicolon; and in parentheses, a media
        // query's features, which are properties too.
        var inValue = false;
        var parentheses = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '/' &&
                index + 1 < line.Length)
            {
                if (line[index + 1] == '*')
                {
                    var (end, closed) = CommentEnd(line, index + 2, "*/");
                    Add(runs, line, index, end, SyntaxKind.Comment);
                    if (!closed)
                    {
                        return (depth << 1) | blockComment;
                    }

                    index = end;
                    continue;
                }

                if (line[index + 1] == '/' &&
                    lineComments)
                {
                    Add(runs, line, index, line.Length, SyntaxKind.Comment);
                    break;
                }
            }

            switch (character)
            {
                case '"':
                case '\'':
                {
                    var (end, _) = QuotedEnd(line, index + 1, character);
                    Add(runs, line, index, end, SyntaxKind.String);
                    index = end;
                    continue;
                }
                case '{':
                    depth++;
                    inValue = false;
                    index++;
                    continue;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    inValue = false;
                    index++;
                    continue;
                case ';':
                    inValue = false;
                    index++;
                    continue;
                case '(':
                    parentheses++;
                    index++;
                    continue;
                case ')':
                    parentheses = Math.Max(0, parentheses - 1);
                    index++;
                    continue;
                case '@':
                case '!':
                {
                    var end = NameEnd(line, index + 1);
                    Add(runs, line, index, end, SyntaxKind.Keyword);
                    index = Math.Max(end, index + 1);
                    continue;
                }
                case '$':
                {
                    var end = NameEnd(line, index + 1);
                    Add(runs, line, index, end, SyntaxKind.Variable);
                    index = Math.Max(end, index + 1);
                    continue;
                }
                case '#':
                {
                    // A colour in a value; an id in a selector.
                    var end = NameEnd(line, index + 1);
                    Add(runs, line, index, end, inValue ? SyntaxKind.Constant : SyntaxKind.Attribute);
                    index = Math.Max(end, index + 1);
                    continue;
                }
            }

            if (IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index);
                var digits = end;
                while (digits > index &&
                       char.IsAsciiLetter(line[digits - 1]))
                {
                    digits--;
                }

                Add(runs, line, index, digits, SyntaxKind.Constant);
                if (end < line.Length &&
                    line[end] == '%')
                {
                    end++;
                }

                Add(runs, line, digits, end, SyntaxKind.Keyword);
                index = end;
                continue;
            }

            if (!inValue &&
                character is '.' or ':' &&
                index + 1 < line.Length &&
                (IsWordStart(line[index + 1]) || line[index + 1] is '-' or ':'))
            {
                // A class, or a pseudo-class or element.
                var start = index;
                index++;
                if (character == ':' &&
                    line[index] == ':')
                {
                    index++;
                }

                var end = NameEnd(line, index);
                Add(runs, line, start, end, SyntaxKind.Attribute);
                index = Math.Max(end, index);
                continue;
            }

            if (IsWordStart(character) ||
                character == '-')
            {
                var end = NameEnd(line, index);
                if (end == index)
                {
                    index++;
                    continue;
                }

                if (inValue ||
                    parentheses > 0)
                {
                    Add(runs, line, index, end, SyntaxKind.Constant);

                    // What url( holds is a string.
                    if (end < line.Length &&
                        line[end] == '(' &&
                        line[index..end].Equals("url", StringComparison.OrdinalIgnoreCase))
                    {
                        var close = line[end..].IndexOf(')');
                        var stringEnd = close < 0 ? line.Length : end + close;
                        Add(runs, line, end + 1, stringEnd, SyntaxKind.String);
                        index = stringEnd;
                        continue;
                    }

                    index = end;
                    continue;
                }

                if (depth > 0 &&
                    PropertyColon(line, end) is { } colon)
                {
                    Add(runs, line, index, end, SyntaxKind.Constant);
                    index = colon + 1;
                    inValue = true;
                    continue;
                }

                Add(runs, line, index, end, SyntaxKind.Tag);
                index = end;
                continue;
            }

            index++;
        }

        return depth << 1;
    }

    static int NameEnd(CharSpan line, int index)
    {
        while (index < line.Length &&
               (IsWordPart(line[index]) || line[index] == '-'))
        {
            index++;
        }

        return index;
    }

    // Where the colon is after a property's name, ending at index, the rest of the line no block
    // of its own, as a selector with a pseudo-class, a:hover {, would have. Null for none.
    static int? PropertyColon(CharSpan line, int index)
    {
        while (index < line.Length &&
               char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        if (index >= line.Length ||
            line[index] != ':' ||
            line[index..].Contains('{'))
        {
            return null;
        }

        return index;
    }
}

/// <summary>
/// Python: keywords, True, False and None, strings with their prefixes, triple-quoted ones going
/// on over lines, numbers, comments, and decorators as attributes.
/// </summary>
sealed class PythonSyntax : SyntaxLanguage
{
    const int singleTriple = 1;
    const int doubleTriple = 2;

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> keywords = Words(
            "and as assert async await break class continue def del elif else except finally for from global if " +
            "import in is lambda nonlocal not or pass raise return try while with yield")
        .GetAlternateLookup<CharSpan>();

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> constants = Words("True False None").GetAlternateLookup<CharSpan>();

    public override string Name => "Python";

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var index = 0;
        if (state is singleTriple or doubleTriple)
        {
            var (end, closed) = TripleEnd(line, 0, state == singleTriple ? '\'' : '"');
            Add(runs, line, 0, end, SyntaxKind.String);
            if (!closed)
            {
                return state;
            }

            index = end;
        }

        while (index < line.Length)
        {
            var character = line[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '#')
            {
                Add(runs, line, index, line.Length, SyntaxKind.Comment);
                return 0;
            }

            if (character == '@' &&
                StartsLine(line, index))
            {
                var end = index + 1;
                while (end < line.Length &&
                       (IsWordPart(line[end]) || line[end] == '.'))
                {
                    end++;
                }

                Add(runs, line, index, end, SyntaxKind.Attribute);
                index = end;
                continue;
            }

            if (character is '"' or '\'')
            {
                var (end, stringState) = String(line, index, index, runs);
                if (stringState != 0)
                {
                    return stringState;
                }

                index = end;
                continue;
            }

            if (IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index);
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = end;
                continue;
            }

            if (IsWordStart(character))
            {
                var end = WordEnd(line, index);

                // r"…", b'…', f"…" and the like: the prefix is the string's.
                if (end < line.Length &&
                    line[end] is '"' or '\'' &&
                    end - index <= 2 &&
                    IsPrefix(line[index..end]))
                {
                    var (stringEnd, stringState) = String(line, index, end, runs);
                    if (stringState != 0)
                    {
                        return stringState;
                    }

                    index = stringEnd;
                    continue;
                }

                if (runs != null &&
                    (index == 0 || line[index - 1] != '.'))
                {
                    var word = line[index..end];
                    if (keywords.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Keyword);
                    }
                    else if (constants.Contains(word))
                    {
                        Add(runs, line, index, end, SyntaxKind.Constant);
                    }
                }

                index = end;
                continue;
            }

            index++;
        }

        return 0;
    }

    static bool IsPrefix(CharSpan word)
    {
        foreach (var character in word)
        {
            if ((char) (character | 0x20) is not ('r' or 'b' or 'u' or 'f'))
            {
                return false;
            }
        }

        return true;
    }

    // A string from start, its prefix with it, its quote at quoteAt: where it ends, and the state
    // the next line starts in, for a triple-quoted one still open.
    static (int End, int State) String(CharSpan line, int start, int quoteAt, List<SyntaxRun>? runs)
    {
        var quote = line[quoteAt];
        if (quoteAt + 2 < line.Length &&
            line[quoteAt + 1] == quote &&
            line[quoteAt + 2] == quote)
        {
            var (tripleEnd, closed) = TripleEnd(line, quoteAt + 3, quote);
            Add(runs, line, start, tripleEnd, SyntaxKind.String);
            if (!closed)
            {
                return (tripleEnd, quote == '\'' ? singleTriple : doubleTriple);
            }

            return (tripleEnd, 0);
        }

        var (end, _) = QuotedEnd(line, quoteAt + 1, quote);
        Add(runs, line, start, end, SyntaxKind.String);
        return (end, 0);
    }

    static (int End, bool Closed) TripleEnd(CharSpan line, int index, char quote)
    {
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }

            if (character == quote &&
                index + 2 < line.Length &&
                line[index + 1] == quote &&
                line[index + 2] == quote)
            {
                return (index + 3, true);
            }

            index++;
        }

        return (line.Length, false);
    }
}

/// <summary>
/// PowerShell, its keywords and operators whatever their case: strings, which go on over lines,
/// here-strings, variables, numbers, and comments, block ones too.
/// </summary>
sealed class PowerShellSyntax : SyntaxLanguage
{
    const int blockComment = 1;
    const int hereDouble = 2;
    const int hereSingle = 3;
    const int doubleString = 4;
    const int singleString = 5;

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> keywords = Words(
            "begin break catch class continue data define do dynamicparam else elseif end enum exit filter finally " +
            "for foreach from function hidden if in param process return static switch throw trap try until using " +
            "var while workflow parallel sequence inlinescript",
            ignoreCase: true)
        .GetAlternateLookup<CharSpan>();

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> operators = Words(
            "eq ne gt ge lt le like notlike match notmatch contains notcontains in notin replace and or xor not band " +
            "bor bxor bnot is isnot as split join f shl shr ceq cne cgt cge clt cle ieq ine igt ige ilt ile clike " +
            "ilike cmatch imatch creplace ireplace csplit isplit cnotlike inotlike cnotmatch inotmatch ccontains " +
            "icontains",
            ignoreCase: true)
        .GetAlternateLookup<CharSpan>();

    static readonly FrozenSet<string>.AlternateLookup<CharSpan> constants = Words("true false null", ignoreCase: true).GetAlternateLookup<CharSpan>();

    public override string Name => "PowerShell";

    public override (string Open, string Close, int State)? BlockComment => ("<#", "#>", blockComment);

    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var index = 0;
        switch (state)
        {
            case blockComment:
            {
                var (end, closed) = CommentEnd(line, 0, "#>");
                Add(runs, line, 0, end, SyntaxKind.Comment);
                if (!closed)
                {
                    return state;
                }

                index = end;
                break;
            }
            case hereDouble:
            case hereSingle:
            {
                // Closed only by its quote and @ at the very start of a line.
                var close = state == hereDouble ? "\"@" : "'@";
                if (!line.StartsWith(close))
                {
                    Add(runs, line, 0, line.Length, SyntaxKind.String);
                    return state;
                }

                Add(runs, line, 0, 2, SyntaxKind.String);
                index = 2;
                break;
            }
            case doubleString:
            case singleString:
            {
                var (end, closed) = StringEnd(line, 0, state == doubleString ? '"' : '\'');
                Add(runs, line, 0, end, SyntaxKind.String);
                if (!closed)
                {
                    return state;
                }

                index = end;
                break;
            }
        }

        while (index < line.Length)
        {
            var character = line[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '<' &&
                index + 1 < line.Length &&
                line[index + 1] == '#')
            {
                var (end, closed) = CommentEnd(line, index + 2, "#>");
                Add(runs, line, index, end, SyntaxKind.Comment);
                if (!closed)
                {
                    return blockComment;
                }

                index = end;
                continue;
            }

            if (character == '#')
            {
                Add(runs, line, index, line.Length, SyntaxKind.Comment);
                return 0;
            }

            if (character == '@' &&
                index + 1 < line.Length &&
                line[index + 1] is '"' or '\'' &&
                line[(index + 2)..].IsWhiteSpace())
            {
                Add(runs, line, index, line.Length, SyntaxKind.String);
                return line[index + 1] == '"' ? hereDouble : hereSingle;
            }

            if (character is '"' or '\'')
            {
                var (end, closed) = StringEnd(line, index + 1, character);
                Add(runs, line, index, end, SyntaxKind.String);
                if (!closed)
                {
                    return character == '"' ? doubleString : singleString;
                }

                index = end;
                continue;
            }

            if (character == '$' &&
                index + 1 < line.Length)
            {
                var end = VariableEnd(line, index + 1);
                var kind = SyntaxKind.Variable;
                if (constants.Contains(line[(index + 1)..end]))
                {
                    kind = SyntaxKind.Constant;
                }

                Add(runs, line, index, end, kind);
                index = Math.Max(end, index + 1);
                continue;
            }

            // -eq and the like, where a parameter's name, -Path, would be.
            if (character == '-' &&
                index + 1 < line.Length &&
                char.IsAsciiLetter(line[index + 1]) &&
                (index == 0 || char.IsWhiteSpace(line[index - 1]) || line[index - 1] == '('))
            {
                var end = WordEnd(line, index + 1);
                if (operators.Contains(line[(index + 1)..end]))
                {
                    Add(runs, line, index, end, SyntaxKind.Keyword);
                }

                index = end;
                continue;
            }

            if (IsNumberStart(line, index))
            {
                var end = NumberEnd(line, index);
                Add(runs, line, index, end, SyntaxKind.Constant);
                index = end;
                continue;
            }

            if (IsWordStart(character))
            {
                // A command's name, Get-ChildItem, is one word.
                var end = WordEnd(line, index);
                while (end + 1 < line.Length &&
                       line[end] == '-' &&
                       IsWordStart(line[end + 1]))
                {
                    end = WordEnd(line, end + 1);
                }

                if (runs != null &&
                    (index == 0 || line[index - 1] != '.') &&
                    keywords.Contains(line[index..end]))
                {
                    Add(runs, line, index, end, SyntaxKind.Keyword);
                }

                index = end;
                continue;
            }

            index++;
        }

        return 0;
    }

    // The end of a string from just after its quote: in double quotes, a backtick escapes what
    // follows; in either, the quote doubled is one.
    static (int End, bool Closed) StringEnd(CharSpan line, int index, char quote)
    {
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '`' &&
                quote == '"')
            {
                index += 2;
                continue;
            }

            if (character == quote)
            {
                if (index + 1 < line.Length &&
                    line[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }

                return (index + 1, true);
            }

            index++;
        }

        return (line.Length, false);
    }

    // A variable's name from just after its $: ${anything}, $env:Path, $_, or one of $$, $? and $^.
    static int VariableEnd(CharSpan line, int index)
    {
        if (line[index] == '{')
        {
            var close = line[index..].IndexOf('}');
            if (close < 0)
            {
                return line.Length;
            }

            return index + close + 1;
        }

        if (line[index] is '$' or '?' or '^')
        {
            return index + 1;
        }

        var end = WordEnd(line, index);
        if (end + 1 < line.Length &&
            line[end] == ':' &&
            IsWordStart(line[end + 1]))
        {
            end = WordEnd(line, end + 1);
        }

        return end;
    }
}

/// <summary>
/// YAML: keys as tags, as GitHub's grammar has them, values as strings, numbers, true, false and
/// null as constants, anchors and aliases as variables, comments, and the lines of a | or >
/// block as a string, for as long as they are further in than the line that started it.
/// </summary>
sealed class YamlSyntax : SyntaxLanguage
{
    static readonly FrozenSet<string>.AlternateLookup<CharSpan> constants =
        Words("true false yes no on off null ~", ignoreCase: true).GetAlternateLookup<CharSpan>();

    public override string Name => "YAML";

    // In a block, the state is one more than how far in the line that started it is.
    public override int Tokenize(CharSpan line, int state, List<SyntaxRun>? runs)
    {
        var indent = 0;
        while (indent < line.Length &&
               line[indent] == ' ')
        {
            indent++;
        }

        if (state > 0)
        {
            if (line[indent..].IsWhiteSpace())
            {
                return state;
            }

            if (indent > state - 1)
            {
                Add(runs, line, indent, line.Length, SyntaxKind.String);
                return state;
            }
        }

        var index = indent;
        if (index >= line.Length)
        {
            return 0;
        }

        if (index == 0 &&
            (line.StartsWith("---") || line.StartsWith("...")) &&
            (line.Length == 3 || char.IsWhiteSpace(line[3])))
        {
            Add(runs, line, 0, 3, SyntaxKind.Keyword);
            index = 3;
        }

        // A sequence's dashes, before what is in it.
        while (index < line.Length &&
               line[index] == '-' &&
               (index + 1 == line.Length || line[index + 1] == ' '))
        {
            index++;
            while (index < line.Length &&
                   line[index] == ' ')
            {
                index++;
            }
        }

        var parent = indent;
        if (KeyEnd(line, index) is { } keyEnd)
        {
            Add(runs, line, index, keyEnd, SyntaxKind.Tag);
            parent = index;
            index = keyEnd + 1;
        }

        while (index < line.Length &&
               char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        if (index >= line.Length)
        {
            return 0;
        }

        var character = line[index];
        if (character == '#')
        {
            Add(runs, line, index, line.Length, SyntaxKind.Comment);
            return 0;
        }

        // A block's | or >, its lines those after.
        if (character is '|' or '>')
        {
            var end = index + 1;
            while (end < line.Length &&
                   (line[end] is '+' or '-' || char.IsAsciiDigit(line[end])))
            {
                end++;
            }

            var rest = line[end..].TrimStart();
            if (rest.IsEmpty ||
                rest[0] == '#')
            {
                Add(runs, line, index, end, SyntaxKind.Keyword);
                Add(runs, line, line.Length - rest.Length, line.Length, SyntaxKind.Comment);
                return parent + 1;
            }
        }

        while (index < line.Length &&
               line[index] is '&' or '*' or '!')
        {
            var end = index + 1;
            while (end < line.Length &&
                   !char.IsWhiteSpace(line[end]))
            {
                end++;
            }

            Add(runs, line, index, end, line[index] == '!' ? SyntaxKind.Keyword : SyntaxKind.Variable);
            index = end;
            while (index < line.Length &&
                   char.IsWhiteSpace(line[index]))
            {
                index++;
            }
        }

        Value(line, index, runs);
        return 0;
    }

    // Where a key ends, at its colon, a colon followed by a space or the line's end, quoted or
    // not, before any comment. Null for a line with no key.
    static int? KeyEnd(CharSpan line, int index)
    {
        if (index >= line.Length)
        {
            return null;
        }

        var end = index;
        if (line[index] is '"' or '\'')
        {
            end = QuotedEnd(line, index + 1, line[index], backslashes: line[index] == '"').End;
        }
        else
        {
            while (end < line.Length)
            {
                var character = line[end];
                if (character == ':' &&
                    (end + 1 == line.Length || line[end + 1] == ' '))
                {
                    break;
                }

                if (character == '#' &&
                    end > index &&
                    line[end - 1] == ' ')
                {
                    return null;
                }

                end++;
            }
        }

        if (end < line.Length &&
            end > index &&
            line[end] == ':' &&
            (end + 1 == line.Length || line[end + 1] == ' '))
        {
            return end;
        }

        return null;
    }

    // A value from index: quoted, a string; otherwise to any comment after it, a constant or a
    // number, or else a string.
    static void Value(CharSpan line, int index, List<SyntaxRun>? runs)
    {
        if (index >= line.Length)
        {
            return;
        }

        var character = line[index];
        var end = index;
        if (character is '"' or '\'')
        {
            end = QuotedEnd(line, index + 1, character, backslashes: character == '"').End;
            Add(runs, line, index, end, SyntaxKind.String);
        }
        else if (character is '[' or '{')
        {
            // A flow collection's text is left plain, but for its comment.
            end = index;
            while (end < line.Length &&
                   !(line[end] == '#' && line[end - 1] == ' '))
            {
                end++;
            }
        }
        else
        {
            while (end < line.Length &&
                   !(line[end] == '#' && end > index && line[end - 1] == ' '))
            {
                end++;
            }

            var scalar = line[index..end].TrimEnd();
            var scalarEnd = index + scalar.Length;
            var kind = SyntaxKind.String;
            if (constants.Contains(scalar) ||
                IsNumber(scalar))
            {
                kind = SyntaxKind.Constant;
            }

            Add(runs, line, index, scalarEnd, kind);
        }

        var comment = line[end..].IndexOf('#');
        if (comment >= 0)
        {
            Add(runs, line, end + comment, line.Length, SyntaxKind.Comment);
        }
    }

    static bool IsNumber(CharSpan scalar)
    {
        if (scalar.Length > 0 &&
            scalar[0] is '-' or '+')
        {
            scalar = scalar[1..];
        }

        return scalar.Length > 0 &&
               IsNumberStart(scalar, 0) &&
               NumberEnd(scalar, 0) == scalar.Length;
    }
}
