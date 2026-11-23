public class CloneTests
{
    [Test]
    public async Task ParseCredential()
    {
        var credential = GitCredentials.Parse("protocol=https\nhost=github.com\nusername=octo\npassword=gho_secret\n");

        await Assert.That(credential).IsEqualTo(new("octo", "gho_secret"));
        await Assert.That(credential!.ToString()).DoesNotContain("gho_secret");
        await Assert.That(GitCredentials.Parse("protocol=https\nhost=github.com\n")).IsNull();
    }

    // The signed in user's own under Your repositories, the rest under their owner.
    [Test]
    public async Task ParseGitHub()
    {
        const string json = """
            [
              {"full_name": "octo/lode", "owner": {"login": "octo"}, "clone_url": "https://github.com/octo/lode.git", "html_url": "https://github.com/octo/lode", "fork": false, "private": true},
              {"full_name": "octo/AngleSharp", "owner": {"login": "Octo"}, "clone_url": "https://github.com/octo/AngleSharp.git", "html_url": "https://github.com/octo/AngleSharp", "fork": true, "private": false},
              {"full_name": "dotnet/runtime", "owner": {"login": "dotnet"}, "clone_url": "https://github.com/dotnet/runtime.git", "html_url": "https://github.com/dotnet/runtime", "fork": false, "private": false}
            ]
            """;

        var repos = HostedRepos.ParseGitHub(json, "octo");

        await Assert.That(repos.Select(_ => $"{_.Group}: {_.Name} fork={_.Fork} private={_.Private}"))
            .IsEquivalentTo(
            [
                "Your repositories: octo/lode fork=False private=True",
                "Your repositories: octo/AngleSharp fork=True private=False",
                "dotnet: dotnet/runtime fork=False private=False"
            ]);
        await Assert.That(HostedRepos.ParseLogin("""{"login": "octo", "id": 1}""")).IsEqualTo("octo");
    }

    // What GitHub's /repos/{owner}/{name} says of a fork: the repository it was made from, by
    // https or ssh, and that one's default branch. Nothing of one that isn't a fork.
    [Test]
    public async Task ParseParent()
    {
        const string fork = """
            {
              "full_name": "octo/AngleSharp",
              "fork": true,
              "default_branch": "octo-main",
              "parent": {
                "full_name": "AngleSharp/AngleSharp",
                "clone_url": "https://github.com/AngleSharp/AngleSharp.git",
                "ssh_url": "git@github.com:AngleSharp/AngleSharp.git",
                "default_branch": "devel"
              }
            }
            """;
        const string notAFork = """{"full_name": "octo/lode", "fork": false, "default_branch": "main"}""";

        await Assert.That(HostedRepos.ParseParent(fork, ssh: false)).IsEqualTo(new("https://github.com/AngleSharp/AngleSharp.git", "devel"));
        await Assert.That(HostedRepos.ParseParent(fork, ssh: true)).IsEqualTo(new("git@github.com:AngleSharp/AngleSharp.git", "devel"));
        await Assert.That(HostedRepos.ParseParent(notAFork, ssh: false)).IsNull();
    }

    [Test]
    public async Task NextLink()
    {
        const string header = """<https://api.github.com/user/repos?page=2>; rel="next", <https://api.github.com/user/repos?page=5>; rel="last" """;

        await Assert.That(HostedRepos.NextLink(header)).IsEqualTo("https://api.github.com/user/repos?page=2");
        await Assert.That(HostedRepos.NextLink("""<https://api.github.com/user/repos?page=1>; rel="prev" """)).IsNull();
        await Assert.That(HostedRepos.NextLink(null)).IsNull();
    }

    // Under organization and project, those disabled left out.
    [Test]
    public async Task ParseAzureDevOps()
    {
        const string json = """
            {
              "count": 3,
              "value": [
                {"name": "Api", "project": {"name": "Shop"}, "remoteUrl": "https://contoso@dev.azure.com/contoso/Shop/_git/Api", "webUrl": "https://dev.azure.com/contoso/Shop/_git/Api", "isDisabled": false},
                {"name": "Old", "project": {"name": "Shop"}, "remoteUrl": "https://contoso@dev.azure.com/contoso/Shop/_git/Old", "webUrl": "https://dev.azure.com/contoso/Shop/_git/Old", "isDisabled": true},
                {"name": "Site", "project": {"name": "Web"}, "remoteUrl": "https://contoso@dev.azure.com/contoso/Web/_git/Site", "webUrl": "https://dev.azure.com/contoso/Web/_git/Site", "isFork": true}
              ]
            }
            """;

        var repos = HostedRepos.ParseAzureDevOps(json, "contoso");

        await Assert.That(repos.Select(_ => $"{_.Group}: {_.Name} fork={_.Fork}"))
            .IsEquivalentTo(["contoso / Shop: Shop/Api fork=False", "contoso / Web: Web/Site fork=True"]);
        await Assert.That(HostedRepos.ParseAccounts("""{"count": 2, "value": [{"accountName": "contoso"}, {"accountName": "fabrikam"}]}"""))
            .IsEquivalentTo(["contoso", "fabrikam"]);
    }

    // Matched as the links match clones: by the repository's page, however its url is written.
    [Test]
    public async Task Cloned()
    {
        HashSet<string> cloned =
        [
            RepositoryLink.Canonical("git@github.com:octo/Lode.git")!,
            RepositoryLink.Canonical("https://contoso.visualstudio.com/DefaultCollection/Shop/_git/Api")!
        ];

        await Assert.That(HostedRepos.IsCloned(Repo("https://github.com/octo/lode.git"), cloned)).IsTrue();
        await Assert.That(HostedRepos.IsCloned(Repo("https://contoso@dev.azure.com/contoso/Shop/_git/Api"), cloned)).IsTrue();
        await Assert.That(HostedRepos.IsCloned(Repo("https://github.com/octo/other.git"), cloned)).IsFalse();
        await Assert.That(HostedRepos.OrganizationsCloned(cloned)).IsEquivalentTo(["contoso"]);
    }

    // The repositories under their headings, Your repositories first, then each by name.
    [Test]
    public async Task Rows()
    {
        var rows = CloneDialog.RowsFor(
        [
            new("GitHub", "dotnet", "dotnet/runtime", "", "", false, false),
            new("GitHub", HostedRepos.YourRepositories, "octo/zeta", "", "", false, false),
            new("GitHub", HostedRepos.YourRepositories, "octo/alpha", "", "", false, false),
            new("GitHub", "angular", "angular/angular", "", "", false, false)
        ]);

        static string Line(CloneDialog.Row row)
        {
            if (row.Repo == null)
            {
                return $"# {row.Text}";
            }

            return row.Text;
        }

        await Assert.That(rows.Select(Line))
            .IsEquivalentTo(["# Your repositories", "octo/alpha", "octo/zeta", "# angular", "angular/angular", "# dotnet", "dotnet/runtime"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // A url however written, or a GitHub repository's owner/name, with the repository's name.
    [Test]
    public async Task FromUrl()
    {
        await Assert.That(CloneDialog.FromUrl("octo/lode")).IsEqualTo(("https://github.com/octo/lode.git", "octo/lode"));
        await Assert.That(CloneDialog.FromUrl(" https://github.com/octo/lode.git ")).IsEqualTo(("https://github.com/octo/lode.git", "octo/lode"));
        await Assert.That(CloneDialog.FromUrl("git@github.com:octo/lode.git")).IsEqualTo(("git@github.com:octo/lode.git", "octo/lode"));
        await Assert.That(CloneDialog.FromUrl("https://dev.azure.com/contoso/Shop/_git/Api")).IsEqualTo(("https://dev.azure.com/contoso/Shop/_git/Api", "Shop/Api"));
        await Assert.That(CloneDialog.FromUrl("https://git.example.com/team/tool.git")).IsEqualTo(("https://git.example.com/team/tool.git", "tool"));
        await Assert.That(CloneDialog.FromUrl("  ")).IsNull();
    }

    [Test]
    public async Task OrganizationOf()
    {
        await Assert.That(CloneDialog.OrganizationOf("contoso")).IsEqualTo("contoso");
        await Assert.That(CloneDialog.OrganizationOf("https://dev.azure.com/contoso/Shop")).IsEqualTo("contoso");
        await Assert.That(CloneDialog.OrganizationOf("https://contoso.visualstudio.com/")).IsEqualTo("contoso");
    }

    // A clone named owner/name goes in the code folder as a link's does.
    [Test]
    public async Task DestinationByName()
    {
        using var fixture = Fixture.Create();

        await Assert.That(RepositoryClone.Destination(fixture.Root, "octo/lode")).IsEqualTo(fixture.At("lode"));
        await Assert.That(RepositoryClone.Destination(fixture.Root, "Shop/Api")).IsEqualTo(fixture.At("Api"));
    }

    static HostedRepo Repo(string cloneUrl) =>
        new("GitHub", "", "", cloneUrl, "", false, false);
}
