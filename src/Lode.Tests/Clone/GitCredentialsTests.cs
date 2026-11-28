/// <summary>
/// What git is told of the credentials it gives Clone's listings: a credential helper of the
/// tests' own stands in for the user's, in a config of their own in place of the user's and the
/// system's, so nothing is asked of, or told to, what keeps the user's credentials; and a stand-in
/// answers for the sites, so nothing leaves the machine.
/// </summary>
/// <remarks>
/// In the windows lane, though they show no window: RepositoryCloneTests' clones there of a
/// GitHub url ask git for GitHub's credential, quietly, and GitHub for the repository a fork was
/// made from, which while git and the sites are stood in for here would go to the stand-ins, and
/// be written down with what these tests' listings told them.
/// </remarks>
[NotInParallel("windows")]
public class GitCredentialsTests
{
    // GitHub took the credential, so git is told to keep it, as Git Credential Manager keeps one
    // it signed in for only once told: the clone that follows, and the next Lode, sign in with it
    // rather than asking again.
    [Test]
    public async Task ApprovedOnceGitHubTakesIt()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var listing = await Listing(
            scratch,
            _ => _ switch
            {
                "/user" => (HttpStatusCode.OK, """{"login": "octo"}"""),
                "/user/repos" => (HttpStatusCode.OK, """[{"full_name": "octo/lode", "owner": {"login": "octo"}, "clone_url": "https://github.com/octo/lode.git"}]"""),
                _ => (HttpStatusCode.NotFound, "")
            },
            () => HostedRepos.ReadGitHub([], Cancel.None));

        await Assert.That(listing.Problems).IsEmpty();
        await Assert.That(listing.Repos.Select(_ => _.Name)).IsEquivalentTo(["octo/lode"]);
        await Assert.That(Told(scratch)).IsEquivalentTo(
            [
                "get host=github.com",
                "store host=github.com username=octo password=token"
            ],
            CollectionOrdering.Matching);
    }

    // GitHub refused the credential, a token since revoked say, so git is told to drop it, and
    // the next listing signs in again rather than being given it for ever.
    [Test]
    public async Task RejectedOnceGitHubRefusesIt()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var listing = await Listing(
            scratch,
            _ => (HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}"""),
            () => HostedRepos.ReadGitHub([], Cancel.None));

        await Assert.That(listing.Repos).IsEmpty();
        await Assert.That(listing.Problems.Single()).Contains("401");
        await Assert.That(Told(scratch)).IsEquivalentTo(
            [
                "get host=github.com",
                "erase host=github.com username=octo password=token"
            ],
            CollectionOrdering.Matching);
    }

    // Each organization's credential, asked for by its path, is told back by it: kept where the
    // organization listed its repositories, dropped where it refused it. Nothing is told of the
    // profile, which says which organizations the account belongs to, refusing it: a token made
    // for an organization alone may read nothing else, and only that organization's answer says
    // whether it works there.
    [Test]
    public async Task AzureDevOpsToldForEachOrganization()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var listing = await Listing(
            scratch,
            _ => _ switch
            {
                "/contoso/_apis/git/repositories" => (HttpStatusCode.OK, """{"count": 0, "value": []}"""),
                "/fabrikam/_apis/git/repositories" => (HttpStatusCode.Unauthorized, ""),
                "/_apis/profile/profiles/me" => (HttpStatusCode.Unauthorized, ""),
                _ => (HttpStatusCode.NotFound, "")
            },
            () => HostedRepos.ReadAzureDevOps(["contoso", "fabrikam"], Cancel.None));

        await Assert.That(listing.Problems.Single()).StartsWith("Couldn't list fabrikam's repositories: 401");
        await Assert.That(Told(scratch)).IsEquivalentTo(
            [
                "get host=dev.azure.com path=contoso",
                "get host=dev.azure.com path=fabrikam",
                "store host=dev.azure.com path=contoso username=octo password=token",
                "erase host=dev.azure.com path=fabrikam username=octo password=token"
            ],
            CollectionOrdering.Matching);
    }

    // github.com, and a GitHub Enterprise server chosen in Options, each asked by its own API, the
    // server's under /api/v3, and signed in with git's credential for its own host, which is told
    // back to git for that host: kept for each that took it, and dropped for one that refused it.
    // The server's repositories are under headings of its own, its host before each.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GitHubEnterpriseListedBesideGitHub(bool taken)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var listing = await ListingByUrl(
            scratch,
            _ => (_.Host, _.AbsolutePath) switch
            {
                ("api.github.com", "/user") => (HttpStatusCode.OK, """{"login": "octo"}"""),
                ("api.github.com", "/user/repos") => (HttpStatusCode.OK, """[{"full_name": "octo/site", "owner": {"login": "octo"}, "clone_url": "https://github.com/octo/site.git"}]"""),
                ("github.contoso.com", "/api/v3/user") when taken => (HttpStatusCode.OK, """{"login": "octo"}"""),
                ("github.contoso.com", "/api/v3/user") => (HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}"""),
                ("github.contoso.com", "/api/v3/user/repos") => (HttpStatusCode.OK, """[{"full_name": "octo/lode", "owner": {"login": "octo"}, "clone_url": "https://github.contoso.com/octo/lode.git"}, {"full_name": "contoso/tools", "owner": {"login": "contoso"}, "clone_url": "https://github.contoso.com/contoso/tools.git", "private": true}]"""),
                _ => (HttpStatusCode.NotFound, "")
            },
            () => HostedRepos.ReadGitHub(["github.contoso.com"], Cancel.None));

        var enterprise = "store";
        if (taken)
        {
            await Assert.That(listing.Problems).IsEmpty();
            await Assert.That(listing.Repos.Select(_ => $"{_.Group}: {_.Name} {_.CloneUrl}")).IsEquivalentTo(
                [
                    "Your repositories: octo/site https://github.com/octo/site.git",
                    "github.contoso.com / Your repositories: octo/lode https://github.contoso.com/octo/lode.git",
                    "github.contoso.com / contoso: contoso/tools https://github.contoso.com/contoso/tools.git"
                ],
                CollectionOrdering.Matching);
        }
        else
        {
            enterprise = "erase";
            await Assert.That(listing.Repos.Select(_ => _.Name)).IsEquivalentTo(["octo/site"]);
            await Assert.That(listing.Problems.Single()).IsEqualTo("Couldn't sign in to github.contoso.com: 401 Unauthorized.");
        }

        await Assert.That(Told(scratch)).IsEquivalentTo(
            [
                "get host=github.com",
                "store host=github.com username=octo password=token",
                "get host=github.contoso.com",
                $"{enterprise} host=github.contoso.com username=octo password=token"
            ],
            CollectionOrdering.Matching);
    }

    // The listing read with git given a config of the scratch folder's own, whose one credential
    // helper writes down what it is told and gives octo's token, and the sites answered by answer,
    // by the path of what is asked.
    static Task<HostedListing> Listing(
        CodeCommandsTests.Scratch scratch,
        Func<string, (HttpStatusCode Status, string Body)> answer,
        Func<Task<HostedListing>> read) =>
        ListingByUrl(scratch, _ => answer(_.AbsolutePath), read);

    // As above, the sites answered by the whole url asked, each by its host as well as its path.
    static async Task<HostedListing> ListingByUrl(
        CodeCommandsTests.Scratch scratch,
        Func<Uri, (HttpStatusCode Status, string Body)> answer,
        Func<Task<HostedListing>> read)
    {
        var config = scratch.At("gitconfig");
        var told = Slashed(scratch.At("told.log"));
        // In place of every helper before it, should anything else be read; then the stand-in.
        CodeCommandsTests.Scratch.Git(scratch.Root, "config", "--file", config, "--add", "credential.helper", "");
        CodeCommandsTests.Scratch.Git(
            scratch.Root,
            "config",
            "--file",
            config,
            "--add",
            "credential.helper",
            $"!f() {{ echo \"== $1\" >> '{told}'; cat >> '{told}'; if [ \"$1\" = get ]; then echo username=octo; echo password=token; fi; }}; f");
        // As Git Credential Manager sets git up, so Azure DevOps' organization reaches it.
        CodeCommandsTests.Scratch.Git(scratch.Root, "config", "--file", config, "credential.https://dev.azure.com.useHttpPath", "true");

        var environment = GitCredentials.GitEnvironment;
        var client = HostedRepos.Client;
        GitCredentials.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_COUNT"] = "0"
        };
        HostedRepos.Client = new(new StandInSite(answer));
        try
        {
            return await read();
        }
        finally
        {
            HostedRepos.Client.Dispose();
            HostedRepos.Client = client;
            GitCredentials.GitEnvironment = environment;
        }
    }

    // What the helper was told, a line for each time: what it was asked to do, then what of the
    // credential it was given, in the order git gives it, leaving out the protocol, always https,
    // and what git says of itself.
    static List<string> Told(CodeCommandsTests.Scratch scratch)
    {
        var told = new List<string>();
        var path = scratch.At("told.log");
        if (!File.Exists(path))
        {
            return told;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("== ", StringComparison.Ordinal))
            {
                told.Add(line[3..]);
                continue;
            }

            if (line.Length > 0 &&
                told.Count > 0 &&
                !line.StartsWith("protocol=", StringComparison.Ordinal) &&
                !line.StartsWith("capability", StringComparison.Ordinal) &&
                !line.StartsWith("wwwauth", StringComparison.Ordinal))
            {
                told[^1] += $" {line}";
            }
        }

        return told;
    }

    static string Slashed(string path) =>
        path.Replace('\\', '/');

    // Answers each request by its path, without a socket: nothing leaves the process.
    sealed class StandInSite(Func<Uri, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel)
        {
            var (status, body) = answer(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body),
                    RequestMessage = request
                });
        }
    }
}
