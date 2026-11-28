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
/// Lode keeps none: each is asked for when needed, and held only while it is used. How one went
/// is told back to git, as git does after using one itself (see <see cref="Approve"/>), so what
/// keeps them keeps one the site took and drops one it refused.
/// </summary>
static class GitCredentials
{
    /// <summary>
    /// Asks git for its credential for a host, and, for a site with an account per path, as
    /// Azure DevOps has its organizations, that path. Null when git has none, or can't be asked.
    /// Git Credential Manager may show its own window to sign in. Tests answer for git.
    /// </summary>
    internal static Func<string, string?, Cancel, GitCredential?> Fill { get; set; } = (host, path, cancel) => Ask(host, path, cancel, interactive: true);

    /// <summary>
    /// What git is run with, beside what every git Lode runs has. Tests point it at a config of
    /// their own, whose credential helper stands in for the user's, so nothing is asked of, or
    /// told to, what keeps the user's credentials.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? GitEnvironment { get; set; }

    /// <summary>
    /// Tells git the site took the credential it gave for the host and path, as git does once a
    /// fetch has signed in with one: Git Credential Manager keeps one it signed in for only once
    /// told, so without this the clone that follows, and every Lode after, would sign in again.
    /// </summary>
    internal static void Approve(string host, string? path, GitCredential credential, Cancel cancel) =>
        Tell("approve", host, path, credential, cancel);

    /// <summary>
    /// Tells git the site refused the credential it gave for the host and path, as git does once
    /// a fetch is refused one, so what keeps it drops it, a token since revoked say, and the next
    /// ask signs in again rather than being given it for ever.
    /// </summary>
    internal static void Reject(string host, string? path, GitCredential credential, Cancel cancel) =>
        Tell("reject", host, path, credential, cancel);

    static void Tell(string verb, string host, string? path, GitCredential credential, Cancel cancel) =>
        CodeCommands.GitOutput(Path.GetTempPath(), cancel, ["credential", verb], Description(host, path, credential), environment: GitEnvironment);

    // What git credential reads: a key=value line for each part, ended by a blank one. Without a
    // user name, none, as git keeps no credential that has none.
    static string Description(string host, string? path, GitCredential? credential)
    {
        var description = new StringBuilder();
        description.Append("protocol=https\n");
        description.Append($"host={host}\n");
        if (path != null)
        {
            description.Append($"path={path}\n");
        }

        if (credential != null)
        {
            if (credential.Username.Length > 0)
            {
                description.Append($"username={credential.Username}\n");
            }

            description.Append($"password={credential.Password}\n");
        }

        description.Append('\n');
        return description.ToString();
    }

    /// <summary>
    /// As <see cref="Fill"/>, but never with a window to sign in: null where git has no
    /// credential kept for the host already. For what Lode asks of a site unasked, as whether a
    /// repository just cloned is a fork, where a sign in would come from nowhere.
    /// </summary>
    internal static GitCredential? FillQuietly(string host, Cancel cancel) =>
        Ask(host, null, cancel, interactive: false);

    static GitCredential? Ask(string host, string? path, Cancel cancel, bool interactive)
    {
        // Git Credential Manager takes credential.interactive from git's config, which -c is
        // part of: false, it fails rather than showing its window. Git itself never asks, as
        // every git Lode runs is told not to.
        string[] arguments = ["credential", "fill"];
        if (!interactive)
        {
            arguments = ["-c", "credential.interactive=false", "credential", "fill"];
        }

        // Anywhere outside a repository, whose own config could send it elsewhere.
        var result = CodeCommands.GitOutput(Path.GetTempPath(), cancel, arguments, Description(host, path, null), environment: GitEnvironment);
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
