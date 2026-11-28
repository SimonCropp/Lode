/// <summary>
/// The site a repository's origin is on, GitHub or Azure DevOps say, with the repository's page
/// there, for the status bar to link to.
/// </summary>
sealed record RemoteSite(string Name, string Url)
{
    /// <summary>
    /// A commit's page on the site, for History's View on: GitLab's under /-/, Bitbucket's
    /// under /commits/.
    /// </summary>
    public string CommitUrl(string sha) =>
        Name switch
        {
            "GitLab" => $"{Url}/-/commit/{sha}",
            "Bitbucket" => $"{Url}/commits/{sha}",
            _ => $"{Url}/commit/{sha}"
        };

    /// <summary>
    /// A branch's page on the site, its files as the branch has them, for the status bar's View
    /// on: GitLab's under /-/tree/, Bitbucket's under /src/, Azure DevOps' as a version.
    /// </summary>
    public string BranchUrl(string branch) =>
        Name switch
        {
            "GitLab" => $"{Url}/-/tree/{InPath(branch)}",
            "Bitbucket" => $"{Url}/src/{InPath(branch)}",
            "Azure DevOps" => $"{Url}?version=GB{Uri.EscapeDataString(branch)}",
            _ => $"{Url}/tree/{InPath(branch)}"
        };

    /// <summary>
    /// A tag's page on the site, for the status bar's View on: GitHub's has the tag's release,
    /// where it has one, GitLab's the tag, Bitbucket's and Azure DevOps' its files.
    /// </summary>
    public string TagUrl(string tag) =>
        Name switch
        {
            "GitLab" => $"{Url}/-/tags/{Uri.EscapeDataString(tag)}",
            "Bitbucket" => $"{Url}/src/{InPath(tag)}",
            "Azure DevOps" => $"{Url}?version=GT{Uri.EscapeDataString(tag)}",
            _ => $"{Url}/releases/tag/{InPath(tag)}"
        };

    /// <summary>
    /// The page that creates a pull request from a branch origin has, as GitHub Desktop's Create
    /// pull request opens it, into the default branch, which each site picks: GitHub's compare,
    /// which in a fork compares with the repository it was made from, GitLab's new merge
    /// request, Bitbucket's and Azure DevOps' new pull request, as each links to one from what
    /// it says to a push. The branch is a value in the url for all but GitHub, so escaped whole,
    /// its slashes too.
    /// </summary>
    public string PullRequestUrl(string branch) =>
        Name switch
        {
            "GitLab" => $"{Url}/-/merge_requests/new?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(branch)}",
            "Bitbucket" => $"{Url}/pull-requests/new?source={Uri.EscapeDataString(branch)}&t=1",
            "Azure DevOps" => $"{Url}/pullrequestcreate?sourceRef={Uri.EscapeDataString(branch)}",
            _ => $"{Url}/compare/{InPath(branch)}?expand=1"
        };

    /// <summary>
    /// What the site calls a pull request: GitLab a merge request.
    /// </summary>
    public string PullRequestName =>
        Name == "GitLab" ? "merge request" : "pull request";

    // A branch's or tag's name in a url's path: each part escaped, the slashes between kept, as
    // the sites take feature/x.
    static string InPath(string name) =>
        string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// The site origin is on, read from the repository's config, as git would take too long for
    /// every folder gone into. Null without an origin, or one on a site Lode doesn't know.
    /// </summary>
    public static RemoteSite? OfRepo(string repo)
    {
        if (UrlOf(repo, "origin") is not { } url)
        {
            return null;
        }

        return For(url);
    }

    /// <summary>
    /// The url of the repository's upstream remote, the repository a fork was made from, as
    /// GitHub Desktop names it, read from its config as <see cref="OfRepo"/> reads origin's. Null
    /// for a repository that isn't a fork, or has no upstream remote to say it is one.
    /// </summary>
    public static string? UpstreamUrl(string repo) =>
        UrlOf(repo, "upstream");

    static string? UrlOf(string repo, string remote)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git)
            {
                return null;
            }

            return RemoteUrl(File.ReadAllLines(Path.Combine(CodeCommands.CommonDirectory(git), "config")), remote);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The url of the [remote "origin"] section of a git config, or null.
    /// </summary>
    internal static string? OriginUrl(IEnumerable<string> config) =>
        RemoteUrl(config, "origin");

    /// <summary>
    /// The url of the [remote "<paramref name="remote"/>"] section of a git config, or null.
    /// </summary>
    internal static string? RemoteUrl(IEnumerable<string> config, string remote)
    {
        var section = $"[remote \"{remote}\"]";
        var inRemote = false;
        foreach (var raw in config)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inRemote = line.Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inRemote)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0 &&
                line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>
    /// The GitHub Enterprise servers, by host, in lower case, from
    /// <see cref="Settings.GitHubEnterpriseHosts"/>: put in as Lode starts, and again as Options
    /// changes them. Each is GitHub, its pages laid out as github.com's are.
    /// </summary>
    internal static IReadOnlySet<string> GitHubEnterprise { get; private set; } = new HashSet<string>();

    /// <summary>
    /// Makes the servers <paramref name="hosts"/> name GitHub Enterprise, each a host, or a url
    /// of one, as it might be pasted from the browser.
    /// </summary>
    internal static void UseGitHubEnterprise(IEnumerable<string> hosts) =>
        GitHubEnterprise = EnterpriseHosts(hosts).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The hosts of the GitHub Enterprise servers <paramref name="typed"/> names, each once, in
    /// the order typed, leaving out what names none.
    /// </summary>
    internal static List<string> EnterpriseHosts(IEnumerable<string> typed) =>
        typed
            .Select(EnterpriseHost)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The host of a GitHub Enterprise server, in lower case, from its host, or a url of one:
    /// github.example.com, https://github.example.com/ or a repository's page there. Null for
    /// nothing a host, or github.com, which needs no adding.
    /// </summary>
    internal static string? EnterpriseHost(string typed)
    {
        typed = typed.Trim();
        if (!typed.Contains("://", StringComparison.Ordinal))
        {
            typed = $"https://{typed}";
        }

        if (!Uri.TryCreate(typed, UriKind.Absolute, out var uri) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com")
        {
            return null;
        }

        return host;
    }

    /// <summary>
    /// The site and page for a remote's url, as https, ssh, or git's user@host:path. GitHub,
    /// GitHub Enterprise's servers in <see cref="GitHubEnterprise"/>, GitLab, Bitbucket and Azure
    /// DevOps, under its dev.azure.com name and its older visualstudio.com one.
    /// </summary>
    internal static RemoteSite? For(string remote) =>
        For(remote, GitHubEnterprise);

    /// <param name="gitHubEnterprise">The GitHub Enterprise servers, by host, in lower case.</param>
    internal static RemoteSite? For(string remote, IReadOnlySet<string> gitHubEnterprise)
    {
        if (HostAndPath(remote.Trim()) is not var (host, path))
        {
            return null;
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        path = path.Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        host = host.ToLowerInvariant();

        // As github.com: the owner and name, whatever page of the repository it is.
        if (parts.Length >= 2 &&
            gitHubEnterprise.Contains(host))
        {
            return new("GitHub", $"https://{host}/{parts[0]}/{parts[1]}");
        }

        switch (host)
        {
            case "github.com" when parts.Length >= 2:
                return new("GitHub", $"https://github.com/{parts[0]}/{parts[1]}");
            case "gitlab.com" when parts.Length >= 2:
                return new("GitLab", $"https://gitlab.com/{path}");
            case "bitbucket.org" when parts.Length >= 2:
                return new("Bitbucket", $"https://bitbucket.org/{parts[0]}/{parts[1]}");
            case "dev.azure.com" when parts.Length >= 4:
                return new("Azure DevOps", $"https://dev.azure.com/{path}");

            // Over ssh: v3/organization/project/repository.
            case "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" when parts is ["v3", var organization, var project, var repository]:
                return new("Azure DevOps", $"https://dev.azure.com/{organization}/{project}/_git/{repository}");
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal) &&
            parts.Length >= 3)
        {
            return new("Azure DevOps", $"https://{host}/{path}");
        }

        return null;
    }

    // A url's host and path, or those of git's scp-like user@host:path.
    static (string Host, string Path)? HostAndPath(string remote)
    {
        if (remote.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) ||
                uri.Host.Length == 0)
            {
                return null;
            }

            return (uri.Host, uri.AbsolutePath);
        }

        var colon = remote.IndexOf(':');
        var at = remote.IndexOf('@');

        // A drive letter, C:\..., is a folder, not a host.
        if (colon < 2 ||
            at > colon)
        {
            return null;
        }

        return (remote[(at + 1)..colon], remote[(colon + 1)..]);
    }
}
