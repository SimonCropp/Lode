/// <summary>
/// A user name and password, or token, as git signs in to a site with them.
/// </summary>
sealed record GitCredential(string Username, string Password)
{
    /// <summary>
    /// What an Authorization header holds to sign in as git does: Basic, the name and password
    /// together.
    /// </summary>
    public string Basic =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    // Never the password, should one be written out by mistake.
    public override string ToString() =>
        $"{Username}:***";
}

/// <summary>
/// The credentials git signs in to a site with, from whatever git uses to keep them, Git
/// Credential Manager or gh say, so listing the repositories to clone signs in as cloning would.
/// Lode keeps none: each is asked for when needed, and held only while it is used. Nothing is
/// told back to git, so its store is left as it was.
/// </summary>
static class GitCredentials
{
    /// <summary>
    /// Asks git for its credential for a host, and, for a site with an account per path, as
    /// Azure DevOps has its organizations, that path. Null when git has none, or can't be asked.
    /// Git Credential Manager may show its own window to sign in. Tests answer for git.
    /// </summary>
    internal static Func<string, string?, Cancel, GitCredential?> Fill { get; set; } = Ask;

    static GitCredential? Ask(string host, string? path, Cancel cancel)
    {
        var input = new StringBuilder();
        input.Append("protocol=https\n");
        input.Append($"host={host}\n");
        if (path != null)
        {
            input.Append($"path={path}\n");
        }

        input.Append('\n');

        // Anywhere outside a repository, whose own config could send it elsewhere.
        var result = CodeCommands.GitOutput(Path.GetTempPath(), cancel, ["credential", "fill"], input.ToString());
        if (!result.Succeeded)
        {
            return null;
        }

        return Parse(result.Output);
    }

    /// <summary>
    /// What git credential fill said: a key=value line for each part, of which the username and
    /// password are kept.
    /// </summary>
    internal static GitCredential? Parse(string output)
    {
        string? username = null;
        string? password = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = trimmed[..equals];
            var value = trimmed[(equals + 1)..];
            if (key == "username")
            {
                username = value;
                continue;
            }

            if (key == "password")
            {
                password = value;
            }
        }

        if (password is not { Length: > 0 })
        {
            return null;
        }

        return new(username ?? "", password);
    }
}
