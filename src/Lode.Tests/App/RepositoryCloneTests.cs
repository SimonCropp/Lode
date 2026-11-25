/// <summary>
/// Cloning the repository a link names into the code folder, run for real from a bare
/// repository in the temp folder standing in for origin, so nothing leaves the machine.
/// </summary>
public class RepositoryCloneTests
{
    static RepositoryLink Link(string link) =>
        RepositoryLink.Parse(link)!;

    // A folder named for the repository at the top of the code folder. Taken, one in a folder
    // named for the owner; with that taken too, the name with a number.
    [Test]
    public async Task Destination()
    {
        using var code = new CodeCommandsTests.Scratch();
        var link = Link("x-github-client://openRepo/https://github.com/JamesNK/Newtonsoft.Json");

        var top = RepositoryClone.Destination(code.Root, link);
        Directory.CreateDirectory(code.At("Newtonsoft.Json"));
        var underOwner = RepositoryClone.Destination(code.Root, link);
        Directory.CreateDirectory(code.At("JamesNK", "Newtonsoft.Json"));
        var numbered = RepositoryClone.Destination(code.Root, link);
        Directory.CreateDirectory(code.At("Newtonsoft.Json-2"));
        var nextNumber = RepositoryClone.Destination(code.Root, link);

        await Assert.That(code.Relative(top)).IsEqualTo("Newtonsoft.Json");
        await Assert.That(code.Relative(underOwner)).IsEqualTo("JamesNK/Newtonsoft.Json");
        await Assert.That(code.Relative(numbered)).IsEqualTo("Newtonsoft.Json-2");
        await Assert.That(code.Relative(nextNumber)).IsEqualTo("Newtonsoft.Json-3");
    }

    // A clone inside another repository is one the link would not find.
    [Test]
    public async Task DestinationNotInARepositoryNamedForTheOwner()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Newtonsoft.Json"));
        Directory.CreateDirectory(code.At("JamesNK", ".git"));

        var destination = RepositoryClone.Destination(code.Root, Link("x-github-client://openRepo/https://github.com/JamesNK/Newtonsoft.Json"));

        await Assert.That(code.Relative(destination)).IsEqualTo("Newtonsoft.Json-2");
    }

    // Azure DevOps allows names Windows doesn't: a space stays, a colon doesn't, and nor does a
    // dot at the end, which Windows would drop.
    [Test]
    public async Task DestinationIsAName()
    {
        using var code = new CodeCommandsTests.Scratch();

        var destination = RepositoryClone.Destination(code.Root, Link("git-client://clone?repo=https%3A%2F%2Fdev.azure.com%2Forganization%2FMy%20Project%2F_git%2FMy%3ARepo."));

        await Assert.That(code.Relative(destination)).IsEqualTo("My-Repo");
    }

    // A pull request's branch, when origin has it, is the one checked out. What git says as it
    // goes is reported.
    [Test]
    public async Task ClonesThenSwitchesToTheBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Lode");
        CodeCommandsTests.Scratch.Git(repo, "switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo, "feature.txt"), "");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Feature");
        CodeCommandsTests.Scratch.Git(repo, "push", "-u", "origin", "feature");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));
        var reported = new List<string>();

        var result = RepositoryClone.Clone(code.Origin("Lode"), destination, "feature", reported.Add, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Repo).IsEqualTo(destination);
        await Assert.That(File.Exists(Path.Combine(destination, "feature.txt"))).IsTrue();
        await Assert.That(CodeCommandsTests.Scratch.Git(destination, "branch", "--show-current")).IsEqualTo("feature");
        await Assert.That(reported).Contains(_ => _.StartsWith("Cloning into", StringComparison.Ordinal));
    }

    // A fork gets the repository it was made from as upstream, fetched, with its default branch
    // recorded, as GitHub Desktop sets one up. GitHub is answered for, and only for this clone's
    // origin, as other tests clone at the same time.
    [Test]
    [NotInParallel("forkParent")]
    public async Task AForkGetsUpstream()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateFork("Lode");
        var parent = code.Origin("LodeUpstream");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));

        var result = WithParent(code.Origin("Lode"), new(parent, "main"), () => RepositoryClone.Clone(code.Origin("Lode"), destination, null, null, Cancel.None));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).Contains($"Added {parent} as upstream");
        await Assert.That(CodeCommandsTests.Scratch.Git(destination, "remote", "get-url", "upstream")).IsEqualTo(parent);
        await Assert.That(CodeCommandsTests.Scratch.Git(destination, "symbolic-ref", "refs/remotes/upstream/HEAD")).IsEqualTo("refs/remotes/upstream/main");
    }

    // An upstream that can't be fetched still leaves a clone that worked, saying so.
    [Test]
    [NotInParallel("forkParent")]
    public async Task AnUpstreamThatCanNotBeFetchedLeavesTheClone()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var missing = code.Origin("Missing");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));

        var result = WithParent(code.Origin("Lode"), new(missing, "main"), () => RepositoryClone.Clone(code.Origin("Lode"), destination, null, null, Cancel.None));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).Contains("but couldn't fetch it");
        await Assert.That(File.Exists(Path.Combine(destination, "tracked.txt"))).IsTrue();
    }

    static CodeCommands.RepoResult WithParent(string remote, ForkParent parent, Func<CodeCommands.RepoResult> clone)
    {
        var parentOf = HostedRepos.ParentOf;
        HostedRepos.ParentOf = (asked, cancel) =>
        {
            if (asked == remote)
            {
                return parent;
            }

            return parentOf(asked, cancel);
        };
        try
        {
            return clone();
        }
        finally
        {
            HostedRepos.ParentOf = parentOf;
        }
    }

    // A pull request's from a fork: origin has no such branch, so the default branch.
    [Test]
    public async Task ABranchOriginLacksLeavesTheDefault()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));

        var result = RepositoryClone.Clone(code.Origin("Lode"), destination, "only-on-a-fork", null, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(CodeCommandsTests.Scratch.Git(destination, "branch", "--show-current")).IsEqualTo("main");
    }

    // Nothing is left behind, not even the owner's folder made for it, and git says why.
    [Test]
    public async Task AFailedCloneLeavesNothing()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Code"));

        var result = RepositoryClone.Clone(code.Origin("Missing"), code.At("Code", "Owner", "Missing"), null, null, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(RepositoryClone.Reason(result.Output)).StartsWith("fatal:");
        await Assert.That(Directory.Exists(code.At("Code", "Owner"))).IsFalse();
        await Assert.That(Directory.Exists(code.At("Code"))).IsTrue();
    }

    // Cloned by another dialog for the same link since this one was offered, say: the clone
    // fails, and what is there, the work in it too, is not what it takes back.
    [Test]
    public async Task ACloneOntoWhatIsThereAlreadyLeavesIt()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(destination);
        var work = Path.Combine(destination, "work.txt");
        await File.WriteAllTextAsync(work, "not committed");

        var result = RepositoryClone.Clone(code.Origin("Lode"), destination, null, null, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(RepositoryClone.Reason(result.Output)).EndsWith("is already there.");
        await Assert.That(File.Exists(work)).IsTrue();
    }

    // Stopped part way, git takes back nothing itself: what it cloned so far is deleted, the
    // read-only objects too.
    [Test]
    public async Task ACancelledCloneLeavesNothing()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));
        using var cancellation = new CancelSource();

        var result = RepositoryClone.Clone(code.Origin("Lode"), destination, null, _ => cancellation.Cancel(), cancellation.Token);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Directory.Exists(destination)).IsFalse();
    }

    // Through the dialog: asked, cloned into the code folder, and the file the link names is the
    // one opened.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task OfferClonesThenOpensTheFile()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var codeFolder = code.At("Code");
        Directory.CreateDirectory(codeFolder);
        var headings = new List<string>();

        string? opened;
        using (Rewrite("https://github.com/LodeTests/Lode", code.Origin("Lode")))
        {
            opened = OfferAnswering(
                "x-github-client://openRepo/https://github.com/LodeTests/Lode?filepath=tracked.txt",
                codeFolder,
                null,
                page =>
                {
                    headings.Add(page.Heading ?? "");
                    Click(page, "Clone it into the code folder");
                });
        }

        await Assert.That(opened).IsEqualTo(Path.Combine(codeFolder, "Lode", "tracked.txt"));
        await Assert.That(headings).IsEquivalentTo(
            [
                "LodeTests/Lode isn't cloned in the code folder",
                "Cloning LodeTests/Lode"
            ],
            CollectionOrdering.Matching);
    }

    // Through the dialog, a clone that fails says why, offers the app the link was for, and
    // opens nothing.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task OfferSaysWhyACloneFailed()
    {
        using var code = new CodeCommandsTests.Scratch();
        var codeFolder = code.At("Code");
        Directory.CreateDirectory(codeFolder);
        var headings = new List<string>();
        string? reason = null;
        List<string> buttons = [];

        string? opened;
        using (Rewrite("https://github.com/LodeTests/Missing", code.Origin("Missing")))
        {
            opened = OfferAnswering(
                "x-github-client://openRepo/https://github.com/LodeTests/Missing",
                codeFolder,
                """
                "C:\Apps\GitHubDesktop.exe" "%1"
                """,
                page =>
                {
                    headings.Add(page.Heading ?? "");
                    Click(page, "Clone it into the code folder");
                    if (page.Icon == TaskDialogIcon.Warning)
                    {
                        reason = page.Text;
                        buttons = page.Buttons.OfType<TaskDialogCommandLinkButton>().Select(_ => _.Text ?? "").ToList();
                        SynchronizationContext.Current!.Post(_ => page.BoundDialog?.Close(), null);
                    }
                });
        }

        await Assert.That(opened).IsNull();
        await Assert.That(headings).IsEquivalentTo(
            [
                "LodeTests/Missing isn't cloned in the code folder",
                "Cloning LodeTests/Missing",
                "Couldn't clone LodeTests/Missing"
            ],
            CollectionOrdering.Matching);
        await Assert.That(reason).StartsWith("fatal:");
        await Assert.That(buttons).IsEquivalentTo(["Open the link in GitHub Desktop"]);
        await Assert.That(Directory.EnumerateFileSystemEntries(codeFolder)).IsEmpty();
    }

    // The dialog answered by answer, with forward as the command of the app the link was for.
    static string? OfferAnswering(string link, string codeFolder, string? forward, Action<TaskDialogPage> answer)
    {
        var links = new RepositoryLinks(
            @"Software\Lode.Tests.NotTaken",
            "lode",
            @"Software\Lode.Tests.NotTaken.Associations",
            (_, _) => forward);
        RepositoryClone.PageShown = answer;
        try
        {
            return RepositoryClone.Offer(RepositoryLink.Parse(link)!, codeFolder, links);
        }
        finally
        {
            RepositoryClone.PageShown = null;
        }
    }

    // Once the page has finished showing, as a page can't be left while it is still being shown.
    static void Click(TaskDialogPage page, string text)
    {
        if (page.Buttons.FirstOrDefault(_ => _.Text == text) is { } button)
        {
            SynchronizationContext.Current!.Post(_ => button.PerformClick(), null);
        }
    }

    // Git, and so the clone, gets origin in the temp folder for the site's url.
    static Restore Rewrite(string url, string origin)
    {
        var restore = new Restore();
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", $"url.{origin.Replace('\\', '/')}.insteadOf");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", url);
        return restore;
    }

    sealed class Restore : IDisposable
    {
        static readonly string[] names = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];
        readonly string?[] values = names.Select(Environment.GetEnvironmentVariable).ToArray();

        public void Dispose()
        {
            for (var index = 0; index < names.Length; index++)
            {
                Environment.SetEnvironmentVariable(names[index], values[index]);
            }
        }
    }

    [Test]
    [Arguments("Receiving objects:  45% (1234/2742), 1.20 MiB | 2.34 MiB/s", 45)]
    [Arguments("remote: Counting objects: 100% (5/5), done.", 100)]
    [Arguments("Cloning into 'C:\\Code\\Lode'...", null)]
    [Arguments("No digits%", null)]
    public async Task Percent(string line, int? expected) =>
        await Assert.That(RepositoryClone.Percent(line)).IsEqualTo(expected);
}
