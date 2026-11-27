/// <summary>
/// The attributes git gives a path, from its .gitattributes files as git's attr.c reads them:
/// the repository's info/attributes first, then each folder's, the deepest first, then the
/// user's core.attributesFile, then the installation's; in each, the last line that matches,
/// and in a line, its last say on an attribute, decide. A macro set brings its attributes with
/// it: binary is -diff -merge -text.
/// </summary>
sealed class AttributeRules
{
    public const string Set = "\0set";
    public const string Unset = "\0unset";
    public const string Unspecified = "\0unspecified";

    sealed record Line(PathPattern? Pattern, string? Macro, (string Name, string State)[] States);

    readonly string workTree;
    readonly bool ignoreCase;
    readonly Func<string, byte[]?> fromIndex;
    readonly Line[] system;
    readonly Line[] user;
    readonly Line[] info;
    readonly Dictionary<string, Line[]> folders = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Name, string State)[]> macros = new(StringComparer.Ordinal);

    static readonly Line[] builtIn = [new(null, "binary", [("diff", Unset), ("merge", Unset), ("text", Unset)])];

    /// <param name="fromIndex">A .gitattributes staged but not in the working tree, as git falls
    /// back on, by its path.</param>
    public AttributeRules(GitRepository repository, string workTree, Func<string, byte[]?> fromIndex)
    {
        this.workTree = workTree;
        this.fromIndex = fromIndex;
        ignoreCase = repository.Config.GetBool("core.ignorecase", false);
        system = [];
        if (Environment.GetEnvironmentVariable("GIT_ATTR_NOSYSTEM") is not { Length: > 0 } &&
            GitConfig.SystemDirectory is { } etc)
        {
            system = ReadFile(Path.Combine(etc, "gitattributes"), "", true);
        }

        var attributesFile = repository.Config.Get("core.attributesfile");
        if (attributesFile == null)
        {
            user = ReadFile(GitConfig.XdgPath("attributes"), "", true);
        }
        else
        {
            user = ReadFile(GitConfig.ExpandPath(attributesFile), "", true);
        }

        info = ReadFile(Path.Combine(repository.CommonDir, "info", "attributes"), "", true);
    }

    static Line[] ReadFile(string path, string @base, bool macrosAllowed)
    {
        if (GitRepository.ReadSmall(path) is not { } text)
        {
            return [];
        }

        return Parse(text, @base, macrosAllowed);
    }

    // A pattern, or a quoted one, or [attr] and a macro's name, then attributes: a name to set
    // it, -name to unset it, !name to leave it unspecified, name=value.
    static Line[] Parse(string text, string @base, bool macrosAllowed)
    {
        var lines = new List<Line>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan().TrimStart(" \t\r\n");
            if (line.Length == 0 ||
                line[0] == '#')
            {
                continue;
            }

            string name;
            CharSpan states;
            if (line[0] == '"' &&
                Unquote(line, out var unquoted, out var after))
            {
                name = unquoted;
                states = line[after..];
            }
            else
            {
                var end = line.IndexOfAny(" \t\r\n");
                if (end < 0)
                {
                    end = line.Length;
                }

                name = line[..end].ToString();
                states = line[end..];
            }

            string? macro = null;
            PathPattern? pattern = null;
            if (name.Length > "[attr]".Length &&
                name.StartsWith("[attr]", StringComparison.Ordinal))
            {
                if (!macrosAllowed)
                {
                    continue;
                }

                macro = name["[attr]".Length..];
            }
            else
            {
                pattern = PathPattern.Parse(name, @base);
                if ((pattern.Flags & PathPattern.Negative) != 0)
                {
                    continue;
                }
            }

            var parsed = new List<(string, string)>();
            foreach (var range in states.SplitAny(" \t\r\n"))
            {
                var state = states[range];
                if (state.Length == 0)
                {
                    continue;
                }

                if (state[0] == '-')
                {
                    parsed.Add((state[1..].ToString(), Unset));
                    continue;
                }

                if (state[0] == '!')
                {
                    parsed.Add((state[1..].ToString(), Unspecified));
                    continue;
                }

                var equals = state.IndexOf('=');
                if (equals < 0)
                {
                    parsed.Add((state.ToString(), Set));
                    continue;
                }

                parsed.Add((state[..equals].ToString(), state[(equals + 1)..].ToString()));
            }

            lines.Add(new(pattern, macro, parsed.ToArray()));
        }

        return lines.ToArray();
    }

    // A C-quoted pattern, as git writes a path with a space or a quote in it.
    static bool Unquote(CharSpan text, out string value, out int after)
    {
        var builder = new StringBuilder();
        var position = 1;
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == '"')
            {
                value = builder.ToString();
                after = position;
                return true;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (position >= text.Length)
            {
                break;
            }

            c = text[position++];
            switch (c)
            {
                case 'a':
                    builder.Append('\a');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'v':
                    builder.Append('\v');
                    break;
                case >= '0' and <= '3' when position + 1 < text.Length:
                    builder.Append((char)((c - '0') * 64 + (text[position] - '0') * 8 + (text[position + 1] - '0')));
                    position += 2;
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        value = "";
        after = 0;
        return false;
    }

    // A folder's .gitattributes, from the working tree, or the index where it isn't there. Only
    // the top's may define macros.
    Line[] FolderLines(string folder)
    {
        if (folders.TryGetValue(folder, out var known))
        {
            return known;
        }

        var relative = ".gitattributes";
        if (folder.Length > 0)
        {
            relative = $"{folder}/.gitattributes";
        }

        var text = GitRepository.ReadSmall(Path.Combine(workTree, relative));
        if (text == null &&
            fromIndex(relative) is { } staged)
        {
            text = Encoding.UTF8.GetString(staged);
        }

        known = [];
        if (text != null)
        {
            known = Parse(text, folder, folder.Length == 0);
        }

        folders[folder] = known;
        return known;
    }

    /// <summary>
    /// The attributes of a file at <paramref name="path"/>, from the top with forward slashes,
    /// each <see cref="Set"/>, <see cref="Unset"/>, <see cref="Unspecified"/> or a value. One
    /// nothing says anything of isn't there.
    /// </summary>
    public Dictionary<string, string> For(string path)
    {
        // Highest first: info, the deepest folder's to the top's, the user's, the system's, git's.
        var stack = new List<Line[]>
        {
            info
        };
        var folderPaths = new List<string>
        {
            ""
        };
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            folderPaths.Add(path[..slash]);
        }

        for (var index = folderPaths.Count - 1; index >= 0; index--)
        {
            stack.Add(FolderLines(folderPaths[index]));
        }

        stack.Add(user);
        stack.Add(system);
        stack.Add(builtIn);

        macros.Clear();
        foreach (var lines in stack)
        {
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                if (lines[index].Macro is { } macro)
                {
                    macros.TryAdd(macro, lines[index].States);
                }
            }
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameStart = path.LastIndexOf('/') + 1;
        foreach (var lines in stack)
        {
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index];
                if (line.Pattern != null &&
                    line.Pattern.Matches(path, nameStart, false, ignoreCase))
                {
                    Fill(found, line.States);
                }
            }
        }

        return found;
    }

    void Fill(Dictionary<string, string> found, (string Name, string State)[] states)
    {
        for (var index = states.Length - 1; index >= 0; index--)
        {
            var (name, state) = states[index];
            if (!found.TryAdd(name, state))
            {
                continue;
            }

            if (state == Set &&
                macros.TryGetValue(name, out var expansion))
            {
                Fill(found, expansion);
            }
        }
    }
}

/// <summary>
/// What a file in the working tree would be once staged, as git's convert_to_git makes it for
/// hashing it: line endings as text and eol, or core.autocrlf, say. A filter, ident, or an
/// encoding other than UTF-8 needs git, so is thrown as a <see cref="GitFilesException"/>.
/// </summary>
static class CleanFilter
{
    enum Action
    {
        Undefined,
        Binary,
        Text,
        TextInput,
        Auto,
        AutoInput
    }

    /// <param name="stagedHasCrlf">Whether what is staged for the path has CRLFs and is text:
    /// then git leaves the file's as they are.</param>
    public static ByteSpan ToGit(byte[] content, Dictionary<string, string> attributes, GitConfig config, Func<bool> stagedHasCrlf)
    {
        if (attributes.GetValueOrDefault("filter") is { } filter &&
            filter is not (AttributeRules.Set or AttributeRules.Unset or AttributeRules.Unspecified) &&
            (config.IsSet($"filter.{filter}.clean") || config.IsSet($"filter.{filter}.process")))
        {
            throw new GitFilesException($"The {filter} filter needs git");
        }

        if (attributes.GetValueOrDefault("working-tree-encoding") is { } encoding &&
            encoding is not (AttributeRules.Unset or AttributeRules.Unspecified) &&
            !IsUtf8(encoding))
        {
            throw new GitFilesException($"The {encoding} working tree encoding needs git");
        }

        if (attributes.GetValueOrDefault("ident") == AttributeRules.Set &&
            content.AsSpan().IndexOf("$Id"u8) >= 0)
        {
            throw new GitFilesException("ident needs git");
        }

        var action = ActionFor(attributes, config);
        if (action == Action.Binary ||
            content.Length == 0)
        {
            return content;
        }

        var stats = Gather(content);
        var convert = stats.Crlf > 0;
        var auto = action is Action.Auto or Action.AutoInput;
        if (auto)
        {
            if (IsBinary(stats))
            {
                return content;
            }

            if (convert &&
                stagedHasCrlf())
            {
                convert = false;
            }
        }

        if (!convert)
        {
            return content;
        }

        var result = new byte[content.Length - stats.Crlf];
        var written = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var c = content[index];
            if (c == '\r' &&
                (auto || (index + 1 < content.Length && content[index + 1] == '\n')))
            {
                continue;
            }

            result[written++] = c;
        }

        return result.AsSpan(0, written);
    }

    static bool IsUtf8(string encoding) =>
        encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase);

    // As git's convert_attrs: text, else the old crlf, then eol, then core.autocrlf where
    // neither says. Text with CRLF or LF on checkout is the same going in, so is one here.
    static Action ActionFor(Dictionary<string, string> attributes, GitConfig config)
    {
        var action = FromText(attributes.GetValueOrDefault("text"));
        if (action == Action.Undefined)
        {
            action = FromText(attributes.GetValueOrDefault("crlf"));
        }

        if (action != Action.Binary)
        {
            var eol = attributes.GetValueOrDefault("eol");
            if (eol is "lf" or "crlf")
            {
                if (action == Action.Auto)
                {
                    action = Action.AutoInput;
                }
                else
                {
                    action = Action.TextInput;
                }
            }
        }

        if (action == Action.Text)
        {
            return Action.TextInput;
        }

        if (action != Action.Undefined)
        {
            return action;
        }

        var autocrlf = config.Get("core.autocrlf");
        if (autocrlf != null &&
            autocrlf.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            return Action.AutoInput;
        }

        if (config.IsSet("core.autocrlf") &&
            GitConfig.ParseBool(autocrlf) == true)
        {
            return Action.Auto;
        }

        return Action.Binary;
    }

    static Action FromText(string? value) =>
        value switch
        {
            AttributeRules.Set => Action.Text,
            AttributeRules.Unset => Action.Binary,
            "input" => Action.TextInput,
            "auto" => Action.Auto,
            _ => Action.Undefined
        };

    public readonly record struct Stats(int Nul, int LoneCr, int Crlf, int LoneLf, int Printable, int NonPrintable);

    // As git's gather_stats.
    public static Stats Gather(ByteSpan data)
    {
        int nul = 0, loneCr = 0, crlf = 0, loneLf = 0, printable = 0, nonPrintable = 0;
        for (var index = 0; index < data.Length; index++)
        {
            var c = data[index];
            if (c == '\r')
            {
                if (index + 1 < data.Length &&
                    data[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    loneCr++;
                }

                continue;
            }

            if (c == '\n')
            {
                loneLf++;
                continue;
            }

            if (c == 127)
            {
                nonPrintable++;
            }
            else if (c < 32)
            {
                switch (c)
                {
                    case (byte)'\b' or (byte)'\t' or 0x1b or 0x0c:
                        printable++;
                        break;
                    case 0:
                        nul++;
                        nonPrintable++;
                        break;
                    default:
                        nonPrintable++;
                        break;
                }
            }
            else
            {
                printable++;
            }
        }

        // An end of file character at the end isn't counted.
        if (data.Length > 0 &&
            data[^1] == 0x1a)
        {
            nonPrintable--;
        }

        return new(nul, loneCr, crlf, loneLf, printable, nonPrintable);
    }

    public static bool IsBinary(Stats stats) =>
        stats.LoneCr > 0 ||
        stats.Nul > 0 ||
        (stats.Printable >> 7) < stats.NonPrintable;

    /// <summary>
    /// Whether staged content is text with CRLFs in it, for git to leave a file's line endings as
    /// they are.
    /// </summary>
    public static bool IsTextWithCrlf(ByteSpan staged)
    {
        if (staged.IndexOf((byte)'\r') < 0)
        {
            return false;
        }

        var stats = Gather(staged);
        return !IsBinary(stats) && stats.Crlf > 0;
    }

    /// <summary>
    /// The id git would give the content as a blob: SHA-1 of "blob", its length, a NUL, and it.
    /// </summary>
    public static ObjectId BlobId(ByteSpan content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Span<byte> header = stackalloc byte[32];
        "blob "u8.CopyTo(header);
        content.Length.TryFormat(header[5..], out var digits, provider: CultureInfo.InvariantCulture);
        header[5 + digits] = 0;
        hash.AppendData(header[..(6 + digits)]);
        hash.AppendData(content);
        Span<byte> id = stackalloc byte[ObjectId.Length];
        hash.GetHashAndReset(id);
        return new(id);
    }
}
