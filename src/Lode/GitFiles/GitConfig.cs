/// <summary>
/// A repository's config as git reads it: the system's, the user's, the repository's own, a
/// worktree's, then any the environment gives, each later one winning, with what each includes.
/// Keys are kept as git names them, section and name in lower case, a subsection as written:
/// branch.Feature.remote.
/// </summary>
sealed class GitConfig
{
    readonly List<(string Key, string? Value, bool Local)> entries = [];

    /// <summary>
    /// Whether the config may not be what git would have: an include was conditional on what
    /// isn't read here, a hasconfig: on a key other than the remotes' urls say, or HOME is set to
    /// nothing, so where git reads the user's config isn't known.
    /// </summary>
    public bool Uncertain { get; private set; }

    /// <summary>
    /// The last value of <paramref name="key"/>, as git takes a single value. Null where it isn't
    /// set, and also for a key set without a value, which is true: <see cref="IsSet"/> tells
    /// those apart.
    /// </summary>
    public string? Get(string key)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].Key == key)
            {
                return entries[index].Value;
            }
        }

        return null;
    }

    public bool IsSet(string key) =>
        entries.Any(_ => _.Key == key);

    /// <summary>
    /// The value from the repository's own config only, as git reads its format from.
    /// </summary>
    public string? GetLocal(string key)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].Local &&
                entries[index].Key == key)
            {
                return entries[index].Value;
            }
        }

        return null;
    }

    public IEnumerable<string> GetAll(string key) =>
        entries
            .Where(_ => _.Key == key && _.Value != null)
            .Select(_ => _.Value!);

    /// <summary>
    /// Whether another read of the config found what this one did: every key and value, in the
    /// same order.
    /// </summary>
    public bool SameAs(GitConfig other) =>
        Uncertain == other.Uncertain &&
        entries.SequenceEqual(other.entries);

    /// <summary>
    /// Each value of either key, in the order git reads them, null for one set without a value:
    /// for a setting git takes from one key over the other however they are ordered.
    /// </summary>
    public IEnumerable<(string Key, string? Value)> InOrder(string first, string second) =>
        entries
            .Where(_ => _.Key == first || _.Key == second)
            .Select(_ => (_.Key, _.Value));

    /// <summary>
    /// The keys set under <paramref name="prefix"/>, extensions. say, from the repository's own
    /// config.
    /// </summary>
    public IEnumerable<string> LocalKeys(string prefix) =>
        entries
            .Where(_ => _.Local && _.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(_ => _.Key)
            .Distinct();

    public bool GetBool(string key, bool fallback)
    {
        var index = entries.FindLastIndex(_ => _.Key == key);
        if (index < 0)
        {
            return fallback;
        }

        return ParseBool(entries[index].Value) ?? fallback;
    }

    // As git_parse_maybe_bool: set without a value is true, and so is a number not zero.
    internal static bool? ParseBool(string? value)
    {
        if (value == null)
        {
            return true;
        }

        if (value.Length == 0)
        {
            return false;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryParseInt(value, out var number))
        {
            return number != 0;
        }

        return null;
    }

    public int GetInt(string key, int fallback)
    {
        if (Get(key) is not { } value ||
            !TryParseInt(value, out var number))
        {
            return fallback;
        }

        return number;
    }

    // A number, with k, m or g after it for so many 1024s, as git's are: whitespace before it,
    // but nothing after, and none too big for an int.
    static bool TryParseInt(string value, out int number)
    {
        var text = value.AsSpan().TrimStart();
        long factor = 1;
        if (text.Length > 0)
        {
            switch (char.ToLowerInvariant(text[^1]))
            {
                case 'k':
                    factor = 1024;
                    break;
                case 'm':
                    factor = 1024 * 1024;
                    break;
                case 'g':
                    factor = 1024 * 1024 * 1024;
                    break;
            }
        }

        if (factor != 1)
        {
            text = text[..^1];
        }

        number = 0;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ||
            parsed > int.MaxValue / factor ||
            parsed < int.MinValue / factor)
        {
            return false;
        }

        number = (int)(parsed * factor);
        return true;
    }

    // Asked of git once, as what it says doesn't change while Lode runs.
    static readonly Lazy<(string? Config, string? Attributes)> systemFiles = new(() => FindSystemFiles(AskGit, Environment.GetEnvironmentVariable("PATH") ?? ""));

    /// <summary>
    /// The gitconfig of the git installed, the one the git Lode runs, the first on the path,
    /// reads where the environment names no other: GIT_CONFIG_SYSTEM and GIT_CONFIG_NOSYSTEM are
    /// taken as git takes them where the config is read. Null when there is none.
    /// </summary>
    public static string? SystemConfig => systemFiles.Value.Config;

    /// <summary>
    /// The gitattributes of the git installed, as <see cref="SystemConfig"/> is found.
    /// </summary>
    public static string? SystemAttributes => systemFiles.Value.Attributes;

    /// <summary>
    /// The system's gitconfig and gitattributes where git var says git reads them, the first git
    /// on the path being a shim, as Scoop's is, that runs a git installed elsewhere, with an etc
    /// of its own. Where git can't say, the etc beside the git on <paramref name="path"/>.
    /// </summary>
    /// <param name="answer">What git var writes for a variable, GIT_CONFIG_SYSTEM say: null where
    /// git couldn't be run or doesn't know it, as before 2.42.</param>
    internal static (string? Config, string? Attributes) FindSystemFiles(Func<string, string?> answer, string path)
    {
        var config = Answered(answer("GIT_CONFIG_SYSTEM"));
        var attributes = Answered(answer("GIT_ATTR_SYSTEM"));
        if (config != null &&
            attributes != null)
        {
            return (config, attributes);
        }

        if (FindSystemDirectory(path) is not { } etc)
        {
            return (config, attributes);
        }

        return (config ?? Path.Combine(etc, "gitconfig"), attributes ?? Path.Combine(etc, "gitattributes"));
    }

    // The path git var wrote, with forward slashes as Git for Windows writes it. Not one that
    // names no drive, /etc/gitconfig from a git of MSYS2's or Cygwin's own, which is in a root
    // only they know.
    static string? Answered(string? output)
    {
        var text = output?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(text) ||
            text.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !Path.IsPathFullyQualified(text))
        {
            return null;
        }

        return Path.GetFullPath(text);
    }

    // What git var writes for a variable, from the git Lode runs, without the variables that
    // would have it name another system config, or none: those are taken as git takes them
    // where the files are read. Null where git can't be run, or doesn't know the variable. Two
    // runs of git, some 30 ms each, once while Lode runs.
    static string? AskGit(string variable)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                // Never in a repository, whose config git would read.
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("var");
            startInfo.ArgumentList.Add(variable);
            foreach (var name in (string[]) ["GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_ATTR_NOSYSTEM"])
            {
                startInfo.Environment.Remove(name);
            }

            using var process = Process.Start(startInfo)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.Result;
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output;
        }
        catch (Exception)
        {
            // Whatever stops git answering leaves the files to be found from the path, rather
            // than the lazy answer holding the failure for every read after.
            return null;
        }
    }

    /// <summary>
    /// The etc folder of the first git.exe on <paramref name="path"/>: in the folder its cmd or
    /// bin is in, in the installation or in its mingw64.
    /// </summary>
    internal static string? FindSystemDirectory(string path)
    {
        if (FindInstalled(path) is not { } folder)
        {
            return null;
        }

        return Path.Combine(folder, "etc");
    }

    static string? FindInstalled(string path)
    {
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = folder.Trim('"');
            if (directory.Length == 0 ||
                directory.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                !File.Exists(Path.Combine(directory, "git.exe")))
            {
                continue;
            }

            // In the installation's cmd or bin, or in the bin of the MSYS2 environment it was
            // built in, mingw64 or ucrt64 say, whose etc is the installation's.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))!;
            var name = Path.GetFileName(parent);
            if (name.StartsWith("mingw", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ucrt", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("clang", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("usr", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(parent);
            }

            return parent;
        }

        return null;
    }

    /// <summary>
    /// The user's home, as Git for Windows works it out: see <see cref="FindHome"/>. Worked out
    /// each time, as git works it out each time it starts, so a home share connected since is
    /// found.
    /// </summary>
    public static string Home =>
        FindHome(Environment.GetEnvironmentVariable, Directory.Exists, Environment.SystemDirectory);

    /// <summary>
    /// The user's home as Git for Windows works it out as it starts: HOME where it is set, even
    /// to nothing; otherwise HOMEDRIVE and HOMEPATH together, where that is a folder, as a home
    /// share that isn't connected isn't, and isn't System32, as a service's is; otherwise their
    /// profile. Taking the profile where HOME wasn't set, on a machine with a home share git read
    /// H:\.gitconfig and this the profile's, so a core.autocrlf or core.excludesFile there was
    /// another. Empty where HOME is set to nothing: see <see cref="Uncertain"/>.
    /// </summary>
    /// <param name="systemDirectory">System32, as Windows names it, which git compares the home
    /// share with as it is, whatever the case.</param>
    internal static string FindHome(Func<string, string?> environment, Func<string, bool> isFolder, string systemDirectory)
    {
        if (environment("HOME") is { } home)
        {
            return home;
        }

        if (environment("HOMEDRIVE") is { } drive &&
            environment("HOMEPATH") is { } path)
        {
            var share = drive + path;
            if (!share.Equals(systemDirectory, StringComparison.OrdinalIgnoreCase) &&
                isFolder(share))
            {
                return share;
            }
        }

        // Where Windows gives no profile either, git has no home, and reads no config of the
        // user's; Windows always gives one, so its own answer is as good.
        return environment("USERPROFILE") ??
               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Where git looks for a file of the user's own in the XDG way: git/ignore, git/attributes.
    /// </summary>
    public static string XdgPath(string name) =>
        XdgPath(name, Home, Environment.GetEnvironmentVariable);

    static string XdgPath(string name, string home, Func<string, string?> environment)
    {
        var xdg = environment("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(home, ".config");
        }

        return Path.Combine(xdg, "git", name);
    }

    /// <summary>
    /// A path as git expands one in its config: ~/ for the home folder.
    /// </summary>
    public static string ExpandPath(string path)
    {
        if (path == "~")
        {
            return Home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(Home, path[2..]);
        }

        return path;
    }

    // What the config is read for: the repository, for gitdir: and onbranch: includes, and the
    // environment git would be run with, which says which files are read, as a
    // hasconfig:remote.*.url: include has them all read again first.
    sealed record Context(string GitDir, string CommonDir, string? Branch, Func<string, string?> Environment);

    readonly Context context;

    // Read as git reads the config for the remotes' urls as a hasconfig:remote.*.url: include is
    // first met: every such include taken, as what decides them isn't known yet.
    readonly bool gathering;

    // The url of every remote the config has, once a hasconfig:remote.*.url: include is met.
    List<string>? remoteUrls;

    GitConfig(Context context, bool gathering)
    {
        this.context = context;
        this.gathering = gathering;
    }

    /// <summary>
    /// The config for the repository whose git folder is <paramref name="gitDir"/>, its branch,
    /// for onbranch: includes, <paramref name="branch"/>.
    /// </summary>
    public static GitConfig Read(string gitDir, string commonDir, string? branch) =>
        Read(gitDir, commonDir, branch, Environment.GetEnvironmentVariable);

    /// <param name="environment">The environment git would be run with, which can say which
    /// files to read, or give keys of its own.</param>
    internal static GitConfig Read(string gitDir, string commonDir, string? branch, Func<string, string?> environment)
    {
        var config = new GitConfig(new(gitDir, commonDir, branch, environment), false);
        config.ReadAll();
        return config;
    }

    // The system's, the user's, the repository's and its worktree's, then what the environment
    // gives, in the order git reads them.
    void ReadAll()
    {
        var environment = context.Environment;
        if (environment("GIT_CONFIG_NOSYSTEM") is not { Length: > 0 } noSystem ||
            ParseBool(noSystem) == false)
        {
            if (environment("GIT_CONFIG_SYSTEM") is { Length: > 0 } system)
            {
                ReadFile(system, false, 0, false);
            }
            else
            {
                if (environment("PROGRAMDATA") is { Length: > 0 } programData)
                {
                    ReadFile(Path.Combine(programData, "Git", "config"), false, 0, false);
                }

                if (SystemConfig is { } systemConfig)
                {
                    ReadFile(systemConfig, false, 0, false);
                }
            }
        }

        if (environment("GIT_CONFIG_GLOBAL") is { Length: > 0 } global)
        {
            ReadFile(global, false, 0, false);
        }
        else if (FindHome(environment, Directory.Exists, Environment.SystemDirectory) is { Length: > 0 } home)
        {
            ReadFile(XdgPath("config", home, environment), false, 0, false);
            ReadFile(Path.Combine(home, ".gitconfig"), false, 0, false);
        }
        else
        {
            // HOME set to nothing, git reads /.gitconfig, at the top of whichever drive it is run
            // in, which isn't known here.
            Uncertain = true;
        }

        ReadFile(Path.Combine(context.CommonDir, "config"), true, 0, false);
        if (ParseBool(GetLocal("extensions.worktreeconfig")) == true)
        {
            ReadFile(Path.Combine(context.GitDir, "config.worktree"), true, 0, false);
        }

        ReadEnvironment(environment);
    }

    // GIT_CONFIG_COUNT, with a GIT_CONFIG_KEY_ and GIT_CONFIG_VALUE_ for each, as git -c does it
    // for the git it runs. A count that isn't one, or a key missing or with no section in it,
    // git refuses.
    void ReadEnvironment(Func<string, string?> environment)
    {
        if (environment("GIT_CONFIG_COUNT") is not { Length: > 0 } text)
        {
            return;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            throw new GitFilesException("GIT_CONFIG_COUNT isn't a count git would take");
        }

        for (var index = 0; index < count; index++)
        {
            if (environment($"GIT_CONFIG_KEY_{index}") is not { } key ||
                key.IndexOf('.') <= 0)
            {
                throw new GitFilesException($"GIT_CONFIG_KEY_{index} isn't a key git would take");
            }

            entries.Add((Canonical(key), environment($"GIT_CONFIG_VALUE_{index}") ?? "", false));
        }
    }

    // Section and name in lower case, a subsection between them as it is.
    static string Canonical(string key)
    {
        var first = key.IndexOf('.');
        var last = key.LastIndexOf('.');
        if (first == last)
        {
            return key.ToLowerInvariant();
        }

        return $"{key[..first].ToLowerInvariant()}.{key[(first + 1)..last]}.{key[(last + 1)..].ToLowerInvariant()}";
    }

    /// <summary>
    /// Every key set, in the order read, as git config --list lists them.
    /// </summary>
    internal IEnumerable<(string Key, string? Value)> List() =>
        entries.Select(_ => (_.Key, _.Value));

    // conditional: the file is included by an includeIf, or by a file that is, however far down.
    void ReadFile(string path, bool local, int depth, bool conditional)
    {
        if (GitRepository.ReadSmall(path) is { } text)
        {
            Parse(text, path, local, depth, conditional);
        }
    }

    void Parse(string text, string path, bool local, int depth, bool conditional)
    {
        var position = 0;
        var section = "";
        while (position < text.Length)
        {
            var c = text[position];
            if (c is '\n' or '\r' or ' ' or '\t')
            {
                position++;
                continue;
            }

            if (c is '#' or ';')
            {
                SkipLine(text, ref position);
                continue;
            }

            if (c == '[')
            {
                position++;
                section = ParseSection(text, ref position);
                continue;
            }

            if (!char.IsAsciiLetter(c))
            {
                throw new GitFilesException($"Bad config line in {path}");
            }

            var start = position;
            while (position < text.Length &&
                   (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '-'))
            {
                position++;
            }

            var name = text[start..position].ToLowerInvariant();
            while (position < text.Length &&
                   text[position] is ' ' or '\t')
            {
                position++;
            }

            string? value = null;
            if (position < text.Length &&
                text[position] == '=')
            {
                position++;
                value = ParseValue(text, ref position);
            }
            else if (position < text.Length &&
                     text[position] == '\r' &&
                     position + 1 < text.Length &&
                     text[position + 1] == '\n')
            {
                position += 2;
            }
            else if (position < text.Length &&
                     text[position] != '\n')
            {
                throw new GitFilesException($"Bad config line in {path}");
            }

            // Before any section, git takes the key as it is.
            var key = name;
            if (section.Length > 0)
            {
                key = $"{section}.{name}";
            }

            // As git's forbid_remote_url: a url in what an includeIf includes could have a
            // hasconfig:remote.*.url: include taken or not, so git refuses the config.
            if (gathering &&
                conditional &&
                IsRemoteUrl(key))
            {
                throw new GitFilesException($"{path} sets a remote's url, which git refuses in a file an includeIf includes where the config has a hasconfig:remote.*.url: include");
            }

            entries.Add((key, value, local));
            if (name == "path" &&
                value != null)
            {
                Include(section, value, path, local, depth, conditional);
            }
        }
    }

    // remote.<name>.url, as git's add_remote_url takes it: with a name, if an empty one.
    static bool IsRemoteUrl(string key) =>
        key.Length > "remote.url".Length &&
        key.StartsWith("remote.", StringComparison.Ordinal) &&
        key.EndsWith(".url", StringComparison.Ordinal);

    static void SkipLine(string text, ref int position)
    {
        var end = text.IndexOf('\n', position);
        if (end < 0)
        {
            position = text.Length;
            return;
        }

        position = end + 1;
    }

    // [section], [section "subsection"], or the old [section.subsection], all of it lowered.
    static string ParseSection(string text, ref int position)
    {
        var name = new StringBuilder();
        while (true)
        {
            if (position >= text.Length)
            {
                throw new GitFilesException("Unfinished config section");
            }

            var c = text[position++];
            if (c == ']')
            {
                return name.ToString();
            }

            if (char.IsWhiteSpace(c))
            {
                break;
            }

            if (!char.IsAsciiLetterOrDigit(c) &&
                c != '-' &&
                c != '.')
            {
                throw new GitFilesException("Bad config section");
            }

            name.Append(char.ToLowerInvariant(c));
        }

        while (position < text.Length &&
               text[position] is ' ' or '\t')
        {
            position++;
        }

        if (position >= text.Length ||
            text[position] != '"')
        {
            throw new GitFilesException("Bad config subsection");
        }

        position++;
        name.Append('.');
        while (true)
        {
            if (position >= text.Length ||
                text[position] == '\n')
            {
                throw new GitFilesException("Unfinished config subsection");
            }

            var c = text[position++];
            if (c == '"')
            {
                break;
            }

            if (c == '\\')
            {
                if (position >= text.Length ||
                    text[position] == '\n')
                {
                    throw new GitFilesException("Unfinished config subsection");
                }

                c = text[position++];
            }

            name.Append(c);
        }

        if (position >= text.Length ||
            text[position] != ']')
        {
            throw new GitFilesException("Bad config subsection");
        }

        position++;
        return name.ToString();
    }

    // As git's parse_value: quotes, the escapes it knows, a backslash at the end of a line going
    // on to the next, a comment outside quotes, and whitespace kept as it is between words, but
    // not before the first or after the last.
    static string ParseValue(string text, ref int position)
    {
        var value = new StringBuilder();
        var quoted = false;
        var comment = false;
        var spaces = new StringBuilder();
        while (true)
        {
            if (position >= text.Length)
            {
                if (quoted)
                {
                    throw new GitFilesException("Unfinished config value");
                }

                return value.ToString();
            }

            var c = text[position++];
            if (c == '\r' &&
                position < text.Length &&
                text[position] == '\n')
            {
                c = text[position++];
            }

            if (c == '\n')
            {
                if (quoted)
                {
                    throw new GitFilesException("Unfinished config value");
                }

                return value.ToString();
            }

            if (comment)
            {
                continue;
            }

            if (char.IsWhiteSpace(c) &&
                !quoted)
            {
                if (value.Length > 0)
                {
                    spaces.Append(c);
                }

                continue;
            }

            if (!quoted &&
                c is ';' or '#')
            {
                comment = true;
                continue;
            }

            value.Append(spaces);
            spaces.Clear();
            if (c == '\\')
            {
                // At the end of the file, as at the end of a line, the value goes on, to end
                // there.
                if (position >= text.Length)
                {
                    continue;
                }

                c = text[position++];
                if (c == '\r' &&
                    position < text.Length &&
                    text[position] == '\n')
                {
                    c = text[position++];
                }

                switch (c)
                {
                    case '\n':
                        continue;
                    case 't':
                        c = '\t';
                        break;
                    case 'b':
                        c = '\b';
                        break;
                    case 'n':
                        c = '\n';
                        break;
                    case '\\' or '"':
                        break;
                    default:
                        throw new GitFilesException("Bad config escape");
                }

                value.Append(c);
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            value.Append(c);
        }
    }

    // include.path, and includeIf.<condition>.path where the condition holds: the file relative
    // to the one including it, as far down as git goes.
    void Include(string section, string value, string from, bool local, int depth, bool conditional)
    {
        if (section != "include")
        {
            if (!section.StartsWith("includeif.", StringComparison.Ordinal) ||
                !ConditionHolds(section["includeif.".Length..], from))
            {
                return;
            }

            conditional = true;
        }

        if (depth >= 10)
        {
            throw new GitFilesException("Config includes go too deep");
        }

        var path = ExpandPath(value);
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(Path.GetDirectoryName(from)!, path);
        }

        ReadFile(path, local, depth + 1, conditional);
    }

    const string remoteUrlCondition = "hasconfig:remote.*.url:";

    bool ConditionHolds(string condition, string from)
    {
        if (condition.StartsWith("gitdir:", StringComparison.Ordinal))
        {
            return GitDirMatches(condition["gitdir:".Length..], from, context.GitDir, false);
        }

        if (condition.StartsWith("gitdir/i:", StringComparison.Ordinal))
        {
            return GitDirMatches(condition["gitdir/i:".Length..], from, context.GitDir, true);
        }

        if (condition.StartsWith("onbranch:", StringComparison.Ordinal))
        {
            if (context.Branch == null)
            {
                return false;
            }

            var branchPattern = condition["onbranch:".Length..];
            if (branchPattern.EndsWith('/'))
            {
                branchPattern += "**";
            }

            return Wildmatch.Match(branchPattern, context.Branch, Wildmatch.PathName);
        }

        // As git's include_by_remote_url: the pattern globbed as a path is, against the url of
        // every remote the config has. Taken while those are gathered, as git takes it then.
        if (condition.StartsWith(remoteUrlCondition, StringComparison.Ordinal))
        {
            if (gathering)
            {
                return true;
            }

            remoteUrls ??= GatherRemoteUrls();
            var urlPattern = condition[remoteUrlCondition.Length..];
            return remoteUrls.Any(_ => Wildmatch.Match(urlPattern, _, Wildmatch.PathName));
        }

        // Another hasconfig:, and any newer, which git may know and this doesn't.
        Uncertain = true;
        return false;
    }

    // As git's populate_remote_urls: all of the config read again, for every remote's url in
    // it, not only those before the include, as the url is most often in the repository's
    // config and the include in the user's. A url set without a value git can't match, and
    // fails on.
    List<string> GatherRemoteUrls()
    {
        var all = new GitConfig(context, true);
        all.ReadAll();
        if (all.Uncertain)
        {
            Uncertain = true;
        }

        var urls = new List<string>();
        foreach (var (key, value, _) in all.entries)
        {
            if (!IsRemoteUrl(key))
            {
                continue;
            }

            urls.Add(value ?? throw new GitFilesException($"{key} is set without a value, which git fails on where the config has a hasconfig:remote.*.url: include"));
        }

        return urls;
    }

    // As git's include_by_gitdir: ~ expanded, ./ from the including file's folder, a relative
    // pattern found anywhere, a trailing slash anything under it. Git matches with forward slashes.
    static bool GitDirMatches(string pattern, string from, string gitDir, bool ignoreCase)
    {
        pattern = ExpandPath(pattern).Replace('\\', '/');
        var prefix = 0;
        if (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(from))!.Replace('\\', '/');
            pattern = folder + pattern[1..];
            prefix = folder.Length + 1;
        }
        else if (!Path.IsPathRooted(pattern))
        {
            pattern = "**/" + pattern;
        }

        if (pattern.EndsWith('/'))
        {
            pattern += "**";
        }

        var text = Path.GetFullPath(gitDir).Replace('\\', '/');
        var comparison = StringComparison.Ordinal;
        if (ignoreCase)
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }

        if (prefix > 0 &&
            (text.Length < prefix || !text.AsSpan(0, prefix).Equals(pattern.AsSpan(0, prefix), comparison)))
        {
            return false;
        }

        var flags = Wildmatch.PathName;
        if (ignoreCase)
        {
            flags |= Wildmatch.CaseFold;
        }

        return Wildmatch.Match(pattern.AsSpan(prefix), text.AsSpan(prefix), flags);
    }
}
