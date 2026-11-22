/// <summary>
/// A repository on a site, for Clone to list: which site, the heading it is listed under (Your
/// repositories, an organization, an Azure DevOps project), its name as the site shows it, and
/// the urls to clone it from and to open it at.
/// </summary>
sealed record HostedRepo(string Site, string Group, string Name, string CloneUrl, string WebUrl, bool Fork, bool Private);

/// <summary>
/// The repositories a site listed, and why any it couldn't list weren't: not signed in, say.
/// </summary>
sealed record HostedListing(IReadOnlyList<HostedRepo> Repos, IReadOnlyList<string> Problems)
{
    public static HostedListing Failed(string problem) =>
        new([], [problem]);
}

/// <summary>
/// Lists the repositories the user can clone from GitHub and Azure DevOps, signed in with git's
/// own credentials (see <see cref="GitCredentials"/>), and which of them are already in the code
/// folder, for Clone to leave out.
/// </summary>
static class HostedRepos
{
    public const string GitHub = "GitHub";
    public const string AzureDevOps = "Azure DevOps";
    public const string YourRepositories = "Your repositories";

    static HttpClient client = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All
        })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Lists the GitHub repositories. Tests answer for GitHub.
    /// </summary>
    internal static Func<Cancel, Task<HostedListing>> ListGitHub { get; set; } = ReadGitHub;

    /// <summary>
    /// Lists the Azure DevOps repositories of the organizations given, and of any others the
    /// account belongs to. Tests answer for Azure DevOps.
    /// </summary>
    internal static Func<IReadOnlyList<string>, Cancel, Task<HostedListing>> ListAzureDevOps { get; set; } = ReadAzureDevOps;

    static async Task<HostedListing> ReadGitHub(Cancel cancel)
    {
        var credential = await Task.Run(() => GitCredentials.Fill("github.com", null, cancel), cancel);
        if (credential == null)
        {
            return HostedListing.Failed("Couldn't sign in to GitHub: git has no credential for it. Sign in with gh auth login, or clone a repository from GitHub once with git.");
        }

        try
        {
            using var user = await Get("https://api.github.com/user", credential, cancel);
            if (!user.IsSuccessStatusCode)
            {
                return HostedListing.Failed($"Couldn't sign in to GitHub: {Describe(user)}.");
            }

            var login = ParseLogin(await user.Content.ReadAsStringAsync(cancel));
            var repos = new List<HostedRepo>();
            var url = "https://api.github.com/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member";
            while (url != null)
            {
                using var page = await Get(url, credential, cancel);
                if (!page.IsSuccessStatusCode)
                {
                    return new(repos, [$"Couldn't list all of GitHub's repositories: {Describe(page)}."]);
                }

                repos.AddRange(ParseGitHub(await page.Content.ReadAsStringAsync(cancel), login));
                url = NextLink(page.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null);
            }

            return new(repos, []);
        }
        catch (Exception exception)
            when (
                exception is
                    HttpRequestException or
                    TaskCanceledException or
                    JsonException)
        {
            return HostedListing.Failed($"Couldn't list GitHub's repositories: {exception.Message}");
        }
    }

    static Task<HttpResponseMessage> Get(string url, GitCredential credential, Cancel cancel, bool bearer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Lode");
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer)
        {
            request.Headers.Authorization = new("Bearer", credential.Password);
        }
        else
        {
            request.Headers.Authorization = new("Basic", credential.Basic);
        }

        return client.SendAsync(request, cancel);
    }

    static string Describe(HttpResponseMessage response) =>
        $"{(int) response.StatusCode} {response.ReasonPhrase}";

    internal static string ParseLogin(string json)
    {
        using var document = JsonDocument.Parse(json);
        return String(document.RootElement, "login");
    }

    /// <summary>
    /// A page of GitHub's /user/repos: those owned by <paramref name="login"/> under Your
    /// repositories, the rest under their owner.
    /// </summary>
    internal static List<HostedRepo> ParseGitHub(string json, string login)
    {
        using var document = JsonDocument.Parse(json);
        var repos = new List<HostedRepo>();
        foreach (var repo in document.RootElement.EnumerateArray())
        {
            var owner = "";
            if (repo.TryGetProperty("owner", out var ownerElement))
            {
                owner = String(ownerElement, "login");
            }

            var group = owner;
            if (owner.Equals(login, StringComparison.OrdinalIgnoreCase))
            {
                group = YourRepositories;
            }

            repos.Add(
                new(
                    GitHub,
                    group,
                    String(repo, "full_name"),
                    String(repo, "clone_url"),
                    String(repo, "html_url"),
                    Bool(repo, "fork"),
                    Bool(repo, "private")));
        }

        return repos;
    }

    /// <summary>
    /// The next page's url from a Link header: &lt;url&gt;; rel="next", among the others.
    /// </summary>
    internal static string? NextLink(string? header)
    {
        if (header == null)
        {
            return null;
        }

        foreach (var part in header.Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 ||
                !pieces.Skip(1).Any(_ => _.Trim() == "rel=\"next\""))
            {
                continue;
            }

            var url = pieces[0].Trim();
            if (url.StartsWith('<') &&
                url.EndsWith('>'))
            {
                return url[1..^1];
            }
        }

        return null;
    }

    static async Task<HostedListing> ReadAzureDevOps(IReadOnlyList<string> known, Cancel cancel)
    {
        if (known.Count == 0)
        {
            return HostedListing.Failed("No Azure DevOps organization known yet: add one, or clone one of its repositories into the code folder.");
        }

        var problems = new List<string>();
        var repos = new List<HostedRepo>();
        var organizations = new List<string>(known);
        var credentials = new Dictionary<string, GitCredential>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // The organizations known, each signed in to as git would, with one's credential
            // asking Azure DevOps for any others the account belongs to.
            foreach (var organization in known)
            {
                if (await Task.Run(() => GitCredentials.Fill("dev.azure.com", organization, cancel), cancel) is { } credential)
                {
                    credentials[organization] = credential;
                }
            }

            if (credentials.Values.FirstOrDefault() is { } any)
            {
                foreach (var other in await Organizations(any, cancel))
                {
                    if (!organizations.Contains(other, StringComparer.OrdinalIgnoreCase))
                    {
                        organizations.Add(other);
                        credentials[other] = any;
                    }
                }
            }

            foreach (var organization in organizations)
            {
                if (!credentials.TryGetValue(organization, out var credential))
                {
                    problems.Add($"Couldn't sign in to {organization}: git has no credential for it.");
                    continue;
                }

                var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/git/repositories?api-version=7.1";
                using var response = await GetAzure(url, credential, cancel);
                if (!Succeeded(response))
                {
                    problems.Add($"Couldn't list {organization}'s repositories: {Describe(response)}.");
                    continue;
                }

                repos.AddRange(ParseAzureDevOps(await response.Content.ReadAsStringAsync(cancel), organization));
            }
        }
        catch (Exception exception)
            when (
                exception is
                    HttpRequestException or
                    TaskCanceledException or
                    JsonException)
        {
            problems.Add($"Couldn't list Azure DevOps' repositories: {exception.Message}");
        }

        return new(repos, problems);
    }

    // Basic, as git signs in; a token from Microsoft's sign in, which Git Credential Manager may
    // keep in place of a personal access token, goes as a bearer token where Basic is refused.
    static async Task<HttpResponseMessage> GetAzure(string url, GitCredential credential, Cancel cancel)
    {
        var response = await Get(url, credential, cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation &&
            credential.Password.StartsWith("eyJ", StringComparison.Ordinal))
        {
            response.Dispose();
            return await Get(url, credential, cancel, bearer: true);
        }

        return response;
    }

    // Azure DevOps answers a request it won't sign in with 203 and its sign in page, which
    // counts as success otherwise.
    static bool Succeeded(HttpResponseMessage response) =>
        response.IsSuccessStatusCode &&
        response.StatusCode != HttpStatusCode.NonAuthoritativeInformation;

    // The organizations the signed in account belongs to: its profile's id, then the accounts
    // that member has. None when the credential can't say, one limited to an organization say.
    static async Task<List<string>> Organizations(GitCredential credential, Cancel cancel)
    {
        using var profile = await GetAzure("https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1", credential, cancel);
        if (!Succeeded(profile))
        {
            return [];
        }

        string id;
        using (var document = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(cancel)))
        {
            id = String(document.RootElement, "id");
        }

        if (id.Length == 0)
        {
            return [];
        }

        using var accounts = await GetAzure($"https://app.vssps.visualstudio.com/_apis/accounts?memberId={Uri.EscapeDataString(id)}&api-version=7.1", credential, cancel);
        if (!Succeeded(accounts))
        {
            return [];
        }

        return ParseAccounts(await accounts.Content.ReadAsStringAsync(cancel));
    }

    internal static List<string> ParseAccounts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return names;
        }

        foreach (var account in value.EnumerateArray())
        {
            var name = String(account, "accountName");
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// An organization's repositories, from Azure DevOps' git/repositories, each under its
    /// organization and project, leaving out those disabled.
    /// </summary>
    internal static List<HostedRepo> ParseAzureDevOps(string json, string organization)
    {
        using var document = JsonDocument.Parse(json);
        var repos = new List<HostedRepo>();
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return repos;
        }

        foreach (var repo in value.EnumerateArray())
        {
            if (Bool(repo, "isDisabled"))
            {
                continue;
            }

            var project = "";
            if (repo.TryGetProperty("project", out var projectElement))
            {
                project = String(projectElement, "name");
            }

            var name = String(repo, "name");
            repos.Add(
                new(
                    AzureDevOps,
                    $"{organization} / {project}",
                    $"{project}/{name}",
                    String(repo, "remoteUrl"),
                    String(repo, "webUrl"),
                    Bool(repo, "isFork"),
                    false));
        }

        return repos;
    }

    static string String(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        return "";
    }

    static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The repositories already in the code folder, up to two levels in, as the links find them:
    /// by their origin's page, however its url is written.
    /// </summary>
    public static HashSet<string> Cloned(string? codeFolder)
    {
        var cloned = new HashSet<string>(StringComparer.Ordinal);
        if (codeFolder == null ||
            !Directory.Exists(codeFolder))
        {
            return cloned;
        }

        foreach (var repo in CodeCommands.FindRepos(codeFolder, codeFolder))
        {
            if (RemoteSite.OfRepo(repo) is { } site &&
                RepositoryLink.Canonical(site.Url) is { } canonical)
            {
                cloned.Add(canonical);
            }
        }

        return cloned;
    }

    /// <summary>
    /// Whether <paramref name="repo"/> is one of <paramref name="cloned"/>.
    /// </summary>
    public static bool IsCloned(HostedRepo repo, HashSet<string> cloned) =>
        RepositoryLink.Canonical(repo.CloneUrl) is { } canonical &&
        cloned.Contains(canonical);

    /// <summary>
    /// The Azure DevOps organizations the code folder has clones from.
    /// </summary>
    public static List<string> OrganizationsCloned(HashSet<string> cloned)
    {
        const string prefix = "https://dev.azure.com/";
        return cloned
            .Where(_ => _.StartsWith(prefix, StringComparison.Ordinal))
            .Select(_ => _[prefix.Length..].Split('/')[0])
            .Where(_ => _.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
