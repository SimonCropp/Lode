public class RemoteSiteTests
{
    // The repository's page, from origin's url however it is written: https, ssh, or git's
    // user@host:path, with or without .git. Azure DevOps under both its names.
    [Test]
    public async Task For()
    {
        static string? Site(string remote)
        {
            if (RemoteSite.For(remote) is not { } site)
            {
                return null;
            }

            return $"{site.Name} {site.Url}";
        }

        await Assert.That(Site("https://github.com/octo/repo.git")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("https://github.com/octo/repo")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("git@github.com:octo/repo.git")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("ssh://git@github.com/octo/repo.git")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("https://GitHub.com/octo/repo/")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("https://gitlab.com/group/sub/repo.git")).IsEqualTo("GitLab https://gitlab.com/group/sub/repo");
        await Assert.That(Site("git@bitbucket.org:team/repo.git")).IsEqualTo("Bitbucket https://bitbucket.org/team/repo");
        await Assert.That(Site("https://dev.azure.com/org/Project/_git/repo")).IsEqualTo("Azure DevOps https://dev.azure.com/org/Project/_git/repo");
        await Assert.That(Site("https://org@dev.azure.com/org/Project/_git/repo")).IsEqualTo("Azure DevOps https://dev.azure.com/org/Project/_git/repo");
        await Assert.That(Site("git@ssh.dev.azure.com:v3/org/Project/repo")).IsEqualTo("Azure DevOps https://dev.azure.com/org/Project/_git/repo");
        await Assert.That(Site("org@vs-ssh.visualstudio.com:v3/org/Project/repo")).IsEqualTo("Azure DevOps https://dev.azure.com/org/Project/_git/repo");
        await Assert.That(Site("https://org.visualstudio.com/DefaultCollection/Project/_git/repo")).IsEqualTo("Azure DevOps https://org.visualstudio.com/DefaultCollection/Project/_git/repo");
    }

    // A commit's page, where each site has them.
    [Test]
    public async Task CommitUrl()
    {
        await Assert.That(RemoteSite.For("https://github.com/octo/repo.git")!.CommitUrl("abc")).IsEqualTo("https://github.com/octo/repo/commit/abc");
        await Assert.That(RemoteSite.For("https://gitlab.com/group/repo.git")!.CommitUrl("abc")).IsEqualTo("https://gitlab.com/group/repo/-/commit/abc");
        await Assert.That(RemoteSite.For("git@bitbucket.org:team/repo.git")!.CommitUrl("abc")).IsEqualTo("https://bitbucket.org/team/repo/commits/abc");
        await Assert.That(RemoteSite.For("https://dev.azure.com/org/Project/_git/repo")!.CommitUrl("abc")).IsEqualTo("https://dev.azure.com/org/Project/_git/repo/commit/abc");
    }

    // A branch's page and a tag's, where each site has them, a name's slashes kept and anything
    // else a url can't hold escaped.
    [Test]
    public async Task BranchAndTagUrls()
    {
        var github = RemoteSite.For("https://github.com/octo/repo.git")!;
        var gitlab = RemoteSite.For("https://gitlab.com/group/repo.git")!;
        var bitbucket = RemoteSite.For("git@bitbucket.org:team/repo.git")!;
        var azure = RemoteSite.For("https://dev.azure.com/org/Project/_git/repo")!;

        await Assert.That(github.BranchUrl("feature/a#1")).IsEqualTo("https://github.com/octo/repo/tree/feature/a%231");
        await Assert.That(gitlab.BranchUrl("feature/a")).IsEqualTo("https://gitlab.com/group/repo/-/tree/feature/a");
        await Assert.That(bitbucket.BranchUrl("feature/a")).IsEqualTo("https://bitbucket.org/team/repo/src/feature/a");
        await Assert.That(azure.BranchUrl("feature/a")).IsEqualTo("https://dev.azure.com/org/Project/_git/repo?version=GBfeature%2Fa");

        await Assert.That(github.TagUrl("0.37.0")).IsEqualTo("https://github.com/octo/repo/releases/tag/0.37.0");
        await Assert.That(gitlab.TagUrl("0.37.0")).IsEqualTo("https://gitlab.com/group/repo/-/tags/0.37.0");
        await Assert.That(bitbucket.TagUrl("0.37.0")).IsEqualTo("https://bitbucket.org/team/repo/src/0.37.0");
        await Assert.That(azure.TagUrl("0.37.0")).IsEqualTo("https://dev.azure.com/org/Project/_git/repo?version=GT0.37.0");
    }

    // The page that creates a pull request from a branch, where each site has it, from origin
    // however it is written: GitHub's compare, the name's slashes kept as for its page, GitLab's
    // new merge request, Bitbucket's and Azure DevOps' new pull request, each with the branch as
    // a value, so its slashes escaped too, as is anything else a url can't hold.
    [Test]
    public async Task PullRequestUrl()
    {
        static string Url(string remote, string branch) =>
            RemoteSite.For(remote)!.PullRequestUrl(branch);

        await Assert.That(Url("https://github.com/octo/repo.git", "feature/a")).IsEqualTo("https://github.com/octo/repo/compare/feature/a?expand=1");
        await Assert.That(Url("git@github.com:octo/repo.git", "fix #1&2")).IsEqualTo("https://github.com/octo/repo/compare/fix%20%231%262?expand=1");
        await Assert.That(Url("https://gitlab.com/group/sub/repo.git", "feature/a")).IsEqualTo("https://gitlab.com/group/sub/repo/-/merge_requests/new?merge_request%5Bsource_branch%5D=feature%2Fa");
        await Assert.That(Url("ssh://git@gitlab.com/group/repo.git", "fix #1&2")).IsEqualTo("https://gitlab.com/group/repo/-/merge_requests/new?merge_request%5Bsource_branch%5D=fix%20%231%262");
        await Assert.That(Url("https://bitbucket.org/team/repo.git", "feature/a")).IsEqualTo("https://bitbucket.org/team/repo/pull-requests/new?source=feature%2Fa&t=1");
        await Assert.That(Url("git@bitbucket.org:team/repo.git", "fix #1&2")).IsEqualTo("https://bitbucket.org/team/repo/pull-requests/new?source=fix%20%231%262&t=1");
        await Assert.That(Url("https://dev.azure.com/org/Project/_git/repo", "feature/a")).IsEqualTo("https://dev.azure.com/org/Project/_git/repo/pullrequestcreate?sourceRef=feature%2Fa");
        await Assert.That(Url("git@ssh.dev.azure.com:v3/org/Project/repo", "fix #1&2")).IsEqualTo("https://dev.azure.com/org/Project/_git/repo/pullrequestcreate?sourceRef=fix%20%231%262");
        await Assert.That(Url("https://org.visualstudio.com/DefaultCollection/Project/_git/repo", "main")).IsEqualTo("https://org.visualstudio.com/DefaultCollection/Project/_git/repo/pullrequestcreate?sourceRef=main");
    }

    // GitLab's are merge requests, everywhere else's pull requests, as each site calls them.
    [Test]
    public async Task PullRequestName()
    {
        await Assert.That(RemoteSite.For("https://github.com/octo/repo.git")!.PullRequestName).IsEqualTo("pull request");
        await Assert.That(RemoteSite.For("https://gitlab.com/group/repo.git")!.PullRequestName).IsEqualTo("merge request");
        await Assert.That(RemoteSite.For("git@bitbucket.org:team/repo.git")!.PullRequestName).IsEqualTo("pull request");
        await Assert.That(RemoteSite.For("https://dev.azure.com/org/Project/_git/repo")!.PullRequestName).IsEqualTo("pull request");
    }

    // A GitHub Enterprise server chosen in Options is GitHub, from origin however it is written,
    // a page of the repository's pasted from the browser too, with each page where github.com
    // has it: a commit's, a branch's, a tag's, and the compare that creates a pull request. Not
    // chosen, the server is no site Lode knows.
    [Test]
    public async Task GitHubEnterprise()
    {
        IReadOnlySet<string> enterprise = new HashSet<string>(["github.contoso.com"]);

        string? Site(string remote)
        {
            if (RemoteSite.For(remote, enterprise) is not { } site)
            {
                return null;
            }

            return $"{site.Name} {site.Url}";
        }

        await Assert.That(Site("https://github.contoso.com/octo/repo.git")).IsEqualTo("GitHub https://github.contoso.com/octo/repo");
        await Assert.That(Site("https://GitHub.Contoso.com/octo/repo/")).IsEqualTo("GitHub https://github.contoso.com/octo/repo");
        await Assert.That(Site("git@github.contoso.com:octo/repo.git")).IsEqualTo("GitHub https://github.contoso.com/octo/repo");
        await Assert.That(Site("ssh://git@github.contoso.com:2222/octo/repo.git")).IsEqualTo("GitHub https://github.contoso.com/octo/repo");
        await Assert.That(Site("https://github.contoso.com/octo/repo/tree/main/src")).IsEqualTo("GitHub https://github.contoso.com/octo/repo");
        await Assert.That(Site("https://github.com/octo/repo.git")).IsEqualTo("GitHub https://github.com/octo/repo");
        await Assert.That(Site("https://github.contoso.com/octo")).IsNull();
        await Assert.That(RemoteSite.For("https://github.contoso.com/octo/repo.git", new HashSet<string>())).IsNull();

        var site = RemoteSite.For("git@github.contoso.com:octo/repo.git", enterprise)!;
        await Assert.That(site.CommitUrl("abc")).IsEqualTo("https://github.contoso.com/octo/repo/commit/abc");
        await Assert.That(site.BranchUrl("feature/a#1")).IsEqualTo("https://github.contoso.com/octo/repo/tree/feature/a%231");
        await Assert.That(site.TagUrl("0.37.0")).IsEqualTo("https://github.contoso.com/octo/repo/releases/tag/0.37.0");
        await Assert.That(site.PullRequestUrl("feature/a")).IsEqualTo("https://github.contoso.com/octo/repo/compare/feature/a?expand=1");
        await Assert.That(site.PullRequestName).IsEqualTo("pull request");
    }

    // A server as typed in Options: its host, or a url of it, a repository's page say, in any
    // case. github.com needs no adding, and what names no host is left out.
    [Test]
    public async Task EnterpriseHosts() =>
        await Assert.That(RemoteSite.EnterpriseHosts(["GitHub.Contoso.com", "https://github.contoso.com/", "https://git.fabrikam.com/octo/repo", "github.com", "https://", "not a host!"]))
            .IsEquivalentTo(["github.contoso.com", "git.fabrikam.com"], CollectionOrdering.Matching);

    // Origins on no site Lode knows, or none at all, a folder say, have no page.
    [Test]
    public async Task NoSite()
    {
        await Assert.That(RemoteSite.For(@"C:\Code\origins\Repo.git")).IsNull();
        await Assert.That(RemoteSite.For("/srv/git/repo.git")).IsNull();
        await Assert.That(RemoteSite.For("https://git.example.com/repo.git")).IsNull();
        await Assert.That(RemoteSite.For("https://github.com/octo")).IsNull();
        await Assert.That(RemoteSite.For("")).IsNull();
    }

    // Origin's url, from the config's own section for it, not another remote's.
    [Test]
    public async Task OriginUrl()
    {
        string[] config =
        [
            "[core]",
            "\tbare = false",
            "[remote \"upstream\"]",
            "\turl = https://github.com/other/repo.git",
            "[remote \"origin\"]",
            "\tfetch = +refs/heads/*:refs/remotes/origin/*",
            "\turl = git@github.com:octo/repo.git",
            "[branch \"main\"]",
            "\tremote = origin"
        ];

        await Assert.That(RemoteSite.OriginUrl(config)).IsEqualTo("git@github.com:octo/repo.git");
        await Assert.That(RemoteSite.OriginUrl(["[core]", "\tbare = false"])).IsNull();
        await Assert.That(RemoteSite.RemoteUrl(config, "upstream")).IsEqualTo("https://github.com/other/repo.git");
        await Assert.That(RemoteSite.RemoteUrl(config, "elsewhere")).IsNull();
    }
}
