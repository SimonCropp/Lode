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
