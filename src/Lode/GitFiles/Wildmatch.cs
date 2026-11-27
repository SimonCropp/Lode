/// <summary>
/// Git's wildmatch, as .gitignore, .gitattributes and config conditions match paths: * and ?, a
/// [class], ** across folders where <see cref="PathName"/> keeps the others within one, and a
/// backslash for a character as it is. Case folds only for ASCII, as git's does.
/// </summary>
static class Wildmatch
{
    public const int CaseFold = 1;
    public const int PathName = 2;

    const int matched = 0;
    const int noMatch = 1;
    const int abortAll = -1;
    const int abortToStarStar = -2;

    public static bool Match(CharSpan pattern, CharSpan text, int flags) =>
        Wild(pattern, 0, text, 0, flags) == matched;

    // The character at an index, or the end as git's NUL.
    static char At(CharSpan span, int index)
    {
        if (index < span.Length)
        {
            return span[index];
        }

        return '\0';
    }

    static char Lower(char c, int flags)
    {
        if ((flags & CaseFold) != 0 &&
            char.IsAsciiLetterUpper(c))
        {
            return (char)(c + 32);
        }

        return c;
    }

    static bool IsGlobSpecial(char c) =>
        c is '*' or '?' or '[' or '\\';

    static int Wild(CharSpan pattern, int p, CharSpan text, int t, int flags)
    {
        for (; p < pattern.Length; t++, p++)
        {
            var patternChar = pattern[p];
            var textChar = At(text, t);
            if (t >= text.Length &&
                patternChar != '*')
            {
                return abortAll;
            }

            textChar = Lower(textChar, flags);
            patternChar = Lower(patternChar, flags);
            switch (patternChar)
            {
                case '\\':
                    // The character after, as it is, not folded even with CaseFold, as git's
                    // isn't; a pattern ending in one matches nothing.
                    p++;
                    patternChar = At(pattern, p);
                    if (textChar != patternChar)
                    {
                        return noMatch;
                    }

                    continue;
                case '?':
                    if ((flags & PathName) != 0 &&
                        textChar == '/')
                    {
                        return noMatch;
                    }

                    continue;
                case '*':
                {
                    bool matchSlash;
                    p++;
                    if (At(pattern, p) == '*')
                    {
                        var before = p - 2;
                        while (At(pattern, ++p) == '*')
                        {
                        }

                        if ((before < 0 || pattern[before] == '/') &&
                            (p >= pattern.Length ||
                             pattern[p] == '/' ||
                             (pattern[p] == '\\' && At(pattern, p + 1) == '/')))
                        {
                            // **/ may match no folder at all: foo/**/bar matches foo/bar.
                            if (At(pattern, p) == '/' &&
                                Wild(pattern, p + 1, text, t, flags) == matched)
                            {
                                return matched;
                            }

                            matchSlash = true;
                        }
                        else
                        {
                            matchSlash = false;
                        }
                    }
                    else
                    {
                        matchSlash = (flags & PathName) == 0;
                    }

                    if (p >= pattern.Length)
                    {
                        // A trailing ** matches everything; a trailing * only within the folder.
                        if (!matchSlash &&
                            text[t..].Contains('/'))
                        {
                            return abortToStarStar;
                        }

                        return matched;
                    }

                    if (!matchSlash &&
                        pattern[p] == '/')
                    {
                        // One * then a slash matches the rest of this folder's name.
                        var slash = text[t..].IndexOf('/');
                        if (slash < 0)
                        {
                            return abortAll;
                        }

                        // The slash itself is matched as the loop goes on.
                        t += slash;
                        break;
                    }

                    while (true)
                    {
                        if (t >= text.Length)
                        {
                            break;
                        }

                        // A literal after the star: skip to where it is, as what comes before
                        // it must be the star's.
                        if (!IsGlobSpecial(pattern[p]))
                        {
                            var literal = Lower(pattern[p], flags);
                            char found;
                            while (t < text.Length &&
                                   (matchSlash || text[t] != '/'))
                            {
                                found = Lower(text[t], flags);
                                if (found == literal)
                                {
                                    break;
                                }

                                t++;
                            }

                            found = Lower(At(text, t), flags);
                            if (t >= text.Length ||
                                found != literal)
                            {
                                if (matchSlash)
                                {
                                    return abortAll;
                                }

                                return abortToStarStar;
                            }
                        }

                        textChar = At(text, t);
                        var result = Wild(pattern, p, text, t, flags);
                        if (result != noMatch)
                        {
                            if (!matchSlash ||
                                result != abortToStarStar)
                            {
                                return result;
                            }
                        }
                        else if (!matchSlash &&
                                 textChar == '/')
                        {
                            return abortToStarStar;
                        }

                        t++;
                    }

                    return abortAll;
                }
                case '[':
                {
                    p++;
                    patternChar = At(pattern, p);
                    if (patternChar == '^')
                    {
                        patternChar = '!';
                    }

                    var negated = patternChar == '!';
                    if (negated)
                    {
                        p++;
                        patternChar = At(pattern, p);
                    }

                    var previous = '\0';
                    var inClass = false;
                    do
                    {
                        if (p >= pattern.Length)
                        {
                            return abortAll;
                        }

                        if (patternChar == '\\')
                        {
                            p++;
                            if (p >= pattern.Length)
                            {
                                return abortAll;
                            }

                            patternChar = pattern[p];
                            if (textChar == patternChar)
                            {
                                inClass = true;
                            }
                        }
                        else if (patternChar == '-' &&
                                 previous != '\0' &&
                                 p + 1 < pattern.Length &&
                                 pattern[p + 1] != ']')
                        {
                            p++;
                            patternChar = pattern[p];
                            if (patternChar == '\\')
                            {
                                p++;
                                if (p >= pattern.Length)
                                {
                                    return abortAll;
                                }

                                patternChar = pattern[p];
                            }

                            if (textChar <= patternChar &&
                                textChar >= previous)
                            {
                                inClass = true;
                            }
                            else if ((flags & CaseFold) != 0 &&
                                     char.IsAsciiLetterLower(textChar))
                            {
                                var upper = (char)(textChar - 32);
                                if (upper <= patternChar &&
                                    upper >= previous)
                                {
                                    inClass = true;
                                }
                            }

                            patternChar = '\0';
                        }
                        else if (patternChar == '[' &&
                                 At(pattern, p + 1) == ':')
                        {
                            var start = p + 2;
                            p = start;
                            while (p < pattern.Length &&
                                   pattern[p] != ']')
                            {
                                p++;
                            }

                            if (p >= pattern.Length)
                            {
                                return abortAll;
                            }

                            var length = p - start - 1;
                            if (length < 0 ||
                                pattern[p - 1] != ':')
                            {
                                // No :] after all, so an ordinary [.
                                p = start - 2;
                                patternChar = '[';
                                if (textChar == patternChar)
                                {
                                    inClass = true;
                                }

                                previous = patternChar;
                                p++;
                                patternChar = At(pattern, p);
                                continue;
                            }

                            switch (InNamedClass(pattern.Slice(start, length), textChar, flags))
                            {
                                case null:
                                    return abortAll;
                                case true:
                                    inClass = true;
                                    break;
                            }

                            patternChar = '\0';
                        }
                        else if (textChar == patternChar)
                        {
                            inClass = true;
                        }

                        previous = patternChar;
                        p++;
                        patternChar = At(pattern, p);
                    } while (patternChar != ']');

                    if (inClass == negated ||
                        ((flags & PathName) != 0 && textChar == '/'))
                    {
                        return noMatch;
                    }

                    continue;
                }
                default:
                    if (textChar != patternChar)
                    {
                        return noMatch;
                    }

                    continue;
            }
        }

        if (t < text.Length)
        {
            return noMatch;
        }

        return matched;
    }

    // [:alpha:] and the like, as ctype has them for ASCII. Null for a class git doesn't know,
    // which matches nothing at all.
    static bool? InNamedClass(CharSpan name, char c, int flags) =>
        name switch
        {
            "alnum" => char.IsAsciiLetterOrDigit(c),
            "alpha" => char.IsAsciiLetter(c),
            "blank" => c is ' ' or '\t',
            "cntrl" => c < 32 || c == 127,
            "digit" => char.IsAsciiDigit(c),
            "graph" => c > 32 && c < 127,
            "lower" => char.IsAsciiLetterLower(c),
            "print" => c >= 32 && c < 127,
            "punct" => c > 32 && c < 127 && !char.IsAsciiLetterOrDigit(c),
            "space" => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v',
            "upper" => char.IsAsciiLetterUpper(c) || ((flags & CaseFold) != 0 && char.IsAsciiLetterLower(c)),
            "xdigit" => char.IsAsciiHexDigit(c),
            _ => null
        };
}
