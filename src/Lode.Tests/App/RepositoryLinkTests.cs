public class RepositoryLinkTests
{
    static readonly string[] links =
    [
        // GitHub's Code button: Open with GitHub Desktop.
        "x-github-client://openRepo/https://github.com/SimonCropp/Lode",
        // A pull request's: the branch it is on.
        "x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=feature&pr=12",
        // A file's: the file, on the branch shown.
        "x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=main&filepath=src%2FLode%2FProgram.cs",
        // A branch git would take for an option, which no branch can be named.
        "x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=--orphan",
        // GitHub's Open with Visual Studio.
        "git-client://clone?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FLode",
        "git-client://clone/?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FLode",
        // Azure DevOps' Clone in Visual Studio.
        "git-client://clone?repo=https%3A%2F%2Forganization%40dev.azure.com%2Forganization%2FMy%20Project%2F_git%2FMy%20Repo",
        // Not a repository's.
        "x-github-client://oauth?code=abc",
        "git-client://clone?repo=",
        "git-client://clone?repo=https%3A%2F%2Fexample.com%2Frepo"
    ];

    [Test]
    public Task Parse() =>
        Verify(
            links.ToDictionary(
                _ => _,
                _ => RepositoryLink.Parse(_) is { } link
                    ? (object) new
                    {
                        scheme = link.Handler.Scheme,
                        link.Remote,
                        link.File,
                        link.Branch,
                        link.Name
                    }
                    : "(none)"))
                    .Snapshot(
                        """
                        {
                          git-client://clone/?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FLode: {
                            scheme: git-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            Name: SimonCropp/Lode
                          },
                          git-client://clone?repo=: (none),
                          git-client://clone?repo=https%3A%2F%2Fexample.com%2Frepo: (none),
                          git-client://clone?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FLode: {
                            scheme: git-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            Name: SimonCropp/Lode
                          },
                          git-client://clone?repo=https%3A%2F%2Forganization%40dev.azure.com%2Forganization%2FMy%20Project%2F_git%2FMy%20Repo: {
                            scheme: git-client,
                            Remote: https://organization@dev.azure.com/organization/My Project/_git/My Repo,
                            Name: My Project/My Repo
                          },
                          x-github-client://oauth?code=abc: (none),
                          x-github-client://openRepo/https://github.com/SimonCropp/Lode: {
                            scheme: x-github-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            Name: SimonCropp/Lode
                          },
                          x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=--orphan: {
                            scheme: x-github-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            Name: SimonCropp/Lode
                          },
                          x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=feature&pr=12: {
                            scheme: x-github-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            Branch: feature,
                            Name: SimonCropp/Lode
                          },
                          x-github-client://openRepo/https://github.com/SimonCropp/Lode?branch=main&filepath=src%2FLode%2FProgram.cs: {
                            scheme: x-github-client,
                            Remote: https://github.com/SimonCropp/Lode,
                            File: src/Lode/Program.cs,
                            Branch: main,
                            Name: SimonCropp/Lode
                          }
                        }
                        """);

    // Every url of a repository is the one page, whichever way the clone and the link name it.
    [Test]
    [Arguments("https://github.com/SimonCropp/Lode", "git@github.com:simoncropp/lode.git")]
    [Arguments("https://github.com/SimonCropp/Lode", "https://github.com/SimonCropp/Lode.git")]
    [Arguments("https://organization@dev.azure.com/organization/My%20Project/_git/My%20Repo", "https://dev.azure.com/organization/My Project/_git/My Repo")]
    [Arguments("https://dev.azure.com/organization/Project/_git/Repo", "git@ssh.dev.azure.com:v3/organization/Project/Repo")]
    [Arguments("https://dev.azure.com/organization/Project/_git/Repo", "https://organization.visualstudio.com/Project/_git/Repo")]
    [Arguments("https://dev.azure.com/organization/Project/_git/Repo", "https://organization.visualstudio.com/DefaultCollection/Project/_git/Repo")]
    public async Task CanonicalMatches(string link, string remote) =>
        await Assert.That(RepositoryLink.Canonical(link)).IsEqualTo(RepositoryLink.Canonical(remote));

    [Test]
    public async Task CanonicalTellsRepositoriesApart() =>
        await Assert.That(RepositoryLink.Canonical("https://github.com/SimonCropp/Lode"))!
            .IsNotEqualTo(RepositoryLink.Canonical("https://github.com/SimonCropp/Verify"));

    [Test]
    public Task Split() =>
        Verify(
            new
            {
                quoted = RepositoryLink.Split(
                    """
                    "C:\Apps\GitHubDesktop.exe" --protocol-launcher "%1"
                    """,
                    "x-github-client://openRepo/https://github.com/a/b"),
                unquoted = RepositoryLink.Split(@"C:\Apps\VSWebLauncher.exe /openuri %1", "git-client://clone?repo=x"),
                noArgument = RepositoryLink.Split(@"C:\Apps\Thing.exe", "git-client://clone?repo=x")
            })
            .Snapshot(
                """
                {
                  quoted: {
                    Item1: C:\Apps\GitHubDesktop.exe,
                    Item2: --protocol-launcher "x-github-client://openRepo/https://github.com/a/b"
                  },
                  unquoted: {
                    Item1: C:\Apps\VSWebLauncher.exe,
                    Item2: /openuri "git-client://clone?repo=x"
                  },
                  noArgument: {
                    Item1: C:\Apps\Thing.exe,
                    Item2: "git-client://clone?repo=x"
                  }
                }
                """);

    // The link reaches the app as the one argument it reached Lode as, exactly, whatever it holds,
    // as the app splits its command line: a link from a .url file or another program, rather than
    // a browser, which escapes them, can hold quotes, which put back as they were would end the
    // argument and start arguments of the link's own, and backslashes, which before a quote, the
    // link's or the one closing it, escape it. Each way a command can take the link: in quotes of
    // its own, bare, inside a longer argument, or left to go on the end.
    [Test]
    public async Task SplitKeepsTheLinkOneArgument()
    {
        string[] held =
        [
            """x-github-client://openRepo/https://github.com/a/b?branch=x" --evil "y""",
            """x-github-client://openRepo/https://github.com/a/b?branch=x\" --evil \"y""",
            """git-client://clone?repo=a\\\"b"c""",
            """x-github-client://openRepo/https://github.com/a/b?filepath=src\""",
            """x-github-client://openRepo/https://github.com/a/b?filepath=a b\\""",
            "x-github-client://openRepo/https://github.com/a/b"
        ];

        foreach (var link in held)
        {
            await Assert.That(Arguments("\"C:\\Apps\\GitHubDesktop.exe\" --protocol-launcher \"%1\"", link))
                .IsEquivalentTo(["--protocol-launcher", link], CollectionOrdering.Matching);
            await Assert.That(Arguments(@"C:\Apps\VSWebLauncher.exe /openuri %1", link))
                .IsEquivalentTo(["/openuri", link], CollectionOrdering.Matching);
            await Assert.That(Arguments("\"C:\\Apps\\Thing.exe\" \"--uri=%1\" --new-window", link))
                .IsEquivalentTo([$"--uri={link}", "--new-window"], CollectionOrdering.Matching);
            await Assert.That(Arguments(@"C:\Apps\Thing.exe", link))
                .IsEquivalentTo([link], CollectionOrdering.Matching);
        }
    }

    // The arguments the app gets, as Windows splits its command line for it.
    static string[] Arguments(string command, string link) =>
        JumpListTests.CommandLineToArguments(RepositoryLink.Split(command, link)!.Value.Arguments);

    // The clone in the code folder is the one opened, a level down too, and the file a link names
    // is selected in it, unless it is outside the repository.
    [Test]
    public async Task FindsTheCloneInTheCodeFolder()
    {
        var temp = Directory.CreateTempSubdirectory("LodeTests.Links.").FullName;
        try
        {
            await FindIn(temp);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    static async Task FindIn(string temp)
    {
        var clone = Path.Combine(temp, "Lode");
        var other = Path.Combine(temp, "Group", "Other");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        Directory.CreateDirectory(Path.Combine(other, ".git"));
        Directory.CreateDirectory(Path.Combine(clone, "src"));
        await File.WriteAllTextAsync(Path.Combine(clone, "src", "Program.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(clone, ".git", "config"), "[remote \"origin\"]\n\turl = git@github.com:SimonCropp/Lode.git\n");
        await File.WriteAllTextAsync(Path.Combine(other, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SimonCropp/Other\n");

        var repo = RepositoryLink.Parse("x-github-client://openRepo/https://github.com/SimonCropp/Lode")!.Find(temp);
        var file = RepositoryLink.Parse("x-github-client://openRepo/https://github.com/SimonCropp/Lode?filepath=src%2FProgram.cs")!.Find(temp);
        var outside = RepositoryLink.Parse("x-github-client://openRepo/https://github.com/SimonCropp/Lode?filepath=..%2F..%2Fwindows")!.Find(temp);
        var nested = RepositoryLink.Parse("git-client://clone?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FOther")!.Find(temp);
        var missing = RepositoryLink.Parse("git-client://clone?repo=https%3A%2F%2Fgithub.com%2FSimonCropp%2FMissing")!.Find(temp);

        await Assert.That(repo).IsEqualTo(clone);
        await Assert.That(file).IsEqualTo(Path.Combine(clone, "src", "Program.cs"));
        await Assert.That(outside).IsEqualTo(clone);
        await Assert.That(nested).IsEqualTo(other);
        await Assert.That(missing).IsNull();
    }
}
