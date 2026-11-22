/// <summary>
/// The site a repository's origin is on, GitHub or Azure DevOps say, with the repository's page
/// there, for the status bar to link to.
/// </summary>
sealed record RemoteSite(string Name, string Url)
{
    /// <summary>
    /// The site origin is on, read from the repository's config, as git would take too long for
    /// every folder gone into. Null without an origin, or one on a site Lode doesn't know.
    /// </summary>
    public static RemoteSite? OfRepo(string repo)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git ||
                OriginUrl(File.ReadAllLines(Path.Combine(CodeCommands.CommonDirectory(git), "config"))) is not { } url)
            {
                return null;
            }

            return For(url);
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
    internal static string? OriginUrl(IEnumerable<string> config)
    {
        var inOrigin = false;
        foreach (var raw in config)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin)
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
    /// The site and page for a remote's url, as https, ssh, or git's user@host:path. GitHub,
    /// GitLab, Bitbucket and Azure DevOps, under its dev.azure.com name and its older
    /// visualstudio.com one.
    /// </summary>
    internal static RemoteSite? For(string remote)
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
