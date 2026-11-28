/// <summary>
/// A link a code site gave to hand a repository to a desktop app, which reached Lode because
/// it took such links over (see <see cref="RepositoryLinks"/>): the repository's remote, and the
/// file in it the link names, if any.
/// </summary>
sealed record RepositoryLink(RepositoryLinks.Handler Handler, string Link, string Remote, string? File)
{
    /// <summary>
    /// The branch the link was for, a pull request's say, or null. Only a clone the link made is
    /// switched to it: one already in the code folder may have changes on its own branch.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// The repository a link names, or null when it names none Lode can tell. GitHub Desktop's
    /// are x-github-client://openRepo/{url}, with ?branch= and, for a file, &amp;filepath=.
    /// Visual Studio's are git-client://clone?repo={url, escaped}.
    /// </summary>
    public static RepositoryLink? Parse(string link)
    {
        if (RepositoryLinks.HandlerOf(link) is not { } handler)
        {
            return null;
        }

        var rest = link[(handler.Scheme.Length + 1)..].TrimStart('/');
        var question = rest.IndexOf('?');
        var path = question < 0 ? rest : rest[..question];
        var query = question < 0 ? "" : rest[(question + 1)..];
        var parameters = Query(query);

        string? remote;
        string? file = null;
        string? branch = null;
        if (handler == RepositoryLinks.GitHubDesktop)
        {
            const string openRepo = "openRepo/";
            if (!path.StartsWith(openRepo, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            remote = Uri.UnescapeDataString(path[openRepo.Length..]);
            file = parameters.GetValueOrDefault("filepath");
            branch = parameters.GetValueOrDefault("branch");
        }
        else
        {
            if (!path.TrimEnd('/').Equals("clone", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            remote = parameters.GetValueOrDefault("repo");
        }

        if (string.IsNullOrWhiteSpace(remote) ||
            RemoteSite.For(remote) == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            file = null;
        }

        // One git would take for an option, which no branch's name can be.
        if (string.IsNullOrWhiteSpace(branch) ||
            branch.StartsWith('-'))
        {
            branch = null;
        }

        return new(handler, link, remote, file)
        {
            Branch = branch
        };
    }

    static Dictionary<string, string> Query(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            parameters.TryAdd(
                Unescape(pair[..equals]),
                Unescape(pair[(equals + 1)..]));
        }

        return parameters;
    }

    static string Unescape(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>
    /// The repository's page, the same for every url of it: https or ssh, with a user name or
    /// without, Azure DevOps under its dev.azure.com name or its older visualstudio.com one, and
    /// either case, as the sites take any.
    /// </summary>
    internal static string? Canonical(string remote)
    {
        if (RemoteSite.For(remote) is not { } site)
        {
            return null;
        }

        var url = Uri.UnescapeDataString(site.Url);
        const string visualStudio = ".visualstudio.com/";
        var host = url.IndexOf(visualStudio, StringComparison.OrdinalIgnoreCase);
        if (host > 0)
        {
            const string https = "https://";
            var organization = url[https.Length..host];
            url = $"https://dev.azure.com/{organization}/{url[(host + visualStudio.Length)..]}";
        }

        // Older Azure DevOps urls name the collection, which dev.azure.com has one of.
        url = url.Replace("/DefaultCollection/", "/", StringComparison.OrdinalIgnoreCase);
        return url.ToLowerInvariant();
    }

    /// <summary>
    /// The clone of the repository among those up to two levels into
    /// <paramref name="codeFolder"/>, the file the link names in it when that is there, or null
    /// for none. A clone ahead of a worktree or submodule of it.
    /// </summary>
    public string? Find(string codeFolder)
    {
        if (Canonical(Remote) is not { } wanted)
        {
            return null;
        }

        var repo = CodeCommands.FindRepos(codeFolder, codeFolder)
            .Where(_ => RemoteSite.OfRepo(_) is { } site &&
                        Canonical(site.Url) == wanted)
            .OrderBy(_ => Directory.Exists(Path.Combine(_, ".git")) ? 0 : 1)
            .FirstOrDefault();
        if (repo == null)
        {
            return null;
        }

        return In(repo);
    }

    /// <summary>
    /// The file the link names in <paramref name="repo"/>, a clone of the repository, when that
    /// is there; otherwise the clone.
    /// </summary>
    public string In(string repo)
    {
        if (File == null)
        {
            return repo;
        }

        try
        {
            var path = Path.GetFullPath(Path.Combine(repo, File.Replace('/', Path.DirectorySeparatorChar)));

            // Never somewhere outside the repository, however the link names it.
            if (Locations.IsWithin(path, repo) &&
                Path.Exists(path))
            {
                return path;
            }
        }
        catch (Exception exception)
            when (
                exception is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
        {
        }

        return repo;
    }

    /// <summary>
    /// The repository's name as its site shows it: owner/name, or project/name on Azure DevOps.
    /// </summary>
    public string Name
    {
        get
        {
            var url = RemoteSite.For(Remote)?.Url ?? Remote;
            var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var name = Uri.UnescapeDataString(parts[^1]);
                var owner = Uri.UnescapeDataString(parts[^2]);
                if (owner == "_git" && parts.Length >= 3)
                {
                    owner = Uri.UnescapeDataString(parts[^3]);
                }

                return $"{owner}/{name}";
            }

            return url;
        }
    }

    /// <summary>
    /// The program and arguments that run <paramref name="command"/>, a registry command, for
    /// <paramref name="link"/>: "%1" in it is the link, and with none the link goes on the end,
    /// either way as one argument, whatever it holds (see <see cref="WithLink"/>). A Squirrel
    /// app's versioned copy (GitHub Desktop's app-3.6.11) that an update has since removed is
    /// swapped for the stub above it, which starts whichever copy is current.
    /// </summary>
    internal static (string Program, string Arguments)? Split(string command, string link)
    {
        command = command.Trim();
        string program;
        string arguments;
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = command[1..close];
            arguments = command[(close + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            program = space < 0 ? command : command[..space];
            arguments = space < 0 ? "" : command[(space + 1)..].Trim();
        }

        program = Environment.ExpandEnvironmentVariables(program);
        if (arguments.Contains("%1", StringComparison.Ordinal))
        {
            arguments = WithLink(arguments, link);
        }
        else
        {
            arguments = $"{arguments} {Quoted(link)}".Trim();
        }

        if (!System.IO.File.Exists(program) &&
            Path.GetDirectoryName(program) is { } folder &&
            Path.GetFileName(folder).StartsWith("app-", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(folder) is { } above)
        {
            var stub = Path.Combine(above, Path.GetFileName(program));
            if (System.IO.File.Exists(stub))
            {
                program = stub;
            }
        }

        return (program, arguments);
    }

    /// <summary>
    /// The command's arguments with <paramref name="link"/> for each %1, as the one argument it
    /// reached Lode as, exactly, as the app splits its command line (CommandLineToArgvW's rules,
    /// which C and .NET programs share). A link from a .url file or another program, rather than
    /// a browser, which escapes them, can hold quotes, which put in as they are would end the
    /// argument early and start arguments of the link's own, and backslashes, which before a
    /// quote escape it. Where %1 is in quotes already, as "%1" is, the link goes in them escaped;
    /// where it is bare, in quotes of its own.
    /// </summary>
    static string WithLink(string arguments, string link)
    {
        var builder = new StringBuilder();
        var quoted = false;
        var backslashes = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (ch == '%' &&
                i + 1 < arguments.Length &&
                arguments[i + 1] == '1')
            {
                if (quoted)
                {
                    var beforeQuote = i + 2 < arguments.Length && arguments[i + 2] == '"';
                    builder.Append(Escaped(link, beforeQuote));
                }
                else
                {
                    builder.Append(Quoted(link));
                }

                i++;
                backslashes = 0;
                continue;
            }

            // A quote opens or closes quotes unless an odd number of backslashes escape it.
            if (ch == '"' &&
                backslashes % 2 == 0)
            {
                quoted = !quoted;
            }

            backslashes = ch == '\\' ? backslashes + 1 : 0;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    // The text in quotes, as one argument, as JumpList.Argument puts a folder, which can hold no
    // quotes.
    internal static string Quoted(string text) =>
        $"\"{Escaped(text, beforeQuote: true)}\"";

    // The text as it goes inside quotes: each quote escaped by a backslash, and the backslashes
    // before it doubled, as otherwise they would escape that one; those at the end doubled too
    // where a quote follows, which they would otherwise escape. Backslashes before anything else
    // are as they are.
    static string Escaped(string text, bool beforeQuote)
    {
        var builder = new StringBuilder();
        var backslashes = 0;
        foreach (var ch in text)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            builder.Append(ch);
            backslashes = 0;
        }

        builder.Append('\\', beforeQuote ? backslashes * 2 : backslashes);
        return builder.ToString();
    }

    /// <summary>
    /// Hands the link to the app it was for, as Windows would have before Lode took the links
    /// over. False when there is none, or it would not start.
    /// </summary>
    public bool Forward(RepositoryLinks links)
    {
        if (links.ForwardCommand(Handler) is not { } command ||
            Split(command, Link) is not var (program, arguments))
        {
            return false;
        }

        // Never back to Lode, which would only ask again.
        if (Path.GetFileNameWithoutExtension(program).Equals("lode", StringComparison.OrdinalIgnoreCase) ||
            arguments.Contains("Lode.dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception exception)
            when (
                exception is
                    Win32Exception or
                    InvalidOperationException or
                    FileNotFoundException)
        {
            return false;
        }
    }
}