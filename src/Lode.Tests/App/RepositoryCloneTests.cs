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

    // Every folder made for it goes, not only the one it was to be in.
    [Test]
    public async Task AFailedCloneTakesBackEachFolderItMade()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Code"));

        var result = RepositoryClone.Clone(code.Origin("Missing"), code.At("Code", "Owner", "Group", "Missing"), null, null, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(code.At("Code"))).IsEmpty();
    }

    // The owner's folder made for it may hold more by the time git fails: another clone into
    // it, or a file saved there. That stays, and so does the folder. It is put there as git
    // says why it fails, which it does before it ends.
    [Test]
    public async Task AFailedCloneLeavesWhatWasPutBesideIt()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Code"));
        var destination = code.At("Code", "Owner", "Missing");
        var other = code.At("Code", "Owner", "Other");

        var result = RepositoryClone.Clone(code.Origin("Missing"), destination, null, _ => PutThere(other), Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Directory.Exists(destination)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(other, "work.txt"))).IsTrue();
    }

    // As for one that fails: cancelled, what it cloned so far goes, and what is beside it stays.
    [Test]
    public async Task ACancelledCloneLeavesWhatWasPutBesideIt()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        Directory.CreateDirectory(code.At("Code"));
        var destination = code.At("Code", "Owner", "Lode");
        var other = code.At("Code", "Owner", "Other");
        using var cancellation = new CancelSource();

        var result = RepositoryClone.Clone(
            code.Origin("Lode"),
            destination,
            null,
            _ =>
            {
                PutThere(other);
                cancellation.Cancel();
            },
            cancellation.Token);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Directory.Exists(destination)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(other, "work.txt"))).IsTrue();
    }

    // A folder that was there already isn't the clone's to take back, empty or not.
    [Test]
    public async Task AFailedCloneLeavesTheFolderThatWasThere()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Code", "Owner"));

        var result = RepositoryClone.Clone(code.Origin("Missing"), code.At("Code", "Owner", "Missing"), null, null, Cancel.None);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(Directory.Exists(code.At("Code", "Owner"))).IsTrue();
    }

    // Another clone, say, with work in it.
    static void PutThere(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "work.txt"), "not committed");
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

    // A repository whose origin has a submodule, Library, from a bare origin of its own, added as
    // git adds one once it is let take a file:// one, which it refuses by default. Or, given a
    // url, with the submodule said to be there instead.
    static string WithSubmodule(CodeCommandsTests.Scratch code, string name, string? url = null)
    {
        var repo = code.CreateRepo(name);
        code.CreateRepo("Library");
        CodeCommandsTests.Scratch.Git(repo, "-c", "protocol.file.allow=always", "submodule", "add", Slashed(code.Origin("Library")), "Library");
        if (url != null)
        {
            CodeCommandsTests.Scratch.Git(repo, "config", "--file", ".gitmodules", "submodule.Library.url", url);
            CodeCommandsTests.Scratch.Git(repo, "add", ".gitmodules");
        }

        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Library");
        CodeCommandsTests.Scratch.Git(repo, "push", "origin", "main");
        return repo;
    }

    static string Slashed(string path) =>
        path.Replace('\\', '/');

    // Git's own -c, given to the update, as the real one is given none: git takes file://
    // submodules only once told it may.
    static readonly string[] fileSubmodules = ["-c", "protocol.file.allow=always"];

    // Its submodules come too, checked out, and nested ones in them, as git submodule update
    // --init --recursive fetches them, so the clone builds, as GitHub Desktop's does. What git
    // says as it goes is reported, as the clone's is.
    [Test]
    public async Task ClonesTheSubmodulesToo()
    {
        using var code = new CodeCommandsTests.Scratch();
        WithSubmodule(code, "Lode");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));
        var reported = new List<string>();

        var clone = RepositoryClone.Clone(code.Origin("Lode"), destination, null, reported.Add, Cancel.None);
        var urls = RepositoryClone.SubmoduleUrls(destination);
        var result = RepositoryClone.WithSubmodules(clone, reported.Add, Cancel.None, fileSubmodules);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(urls).IsEquivalentTo([Slashed(code.Origin("Library"))]);
        await Assert.That(File.ReadAllText(Path.Combine(destination, "Library", "tracked.txt"))).IsEqualTo("original");
        await Assert.That(reported).Contains(_ => _.Contains("Library", StringComparison.Ordinal));
    }

    // A repository with none is left as it was cloned.
    [Test]
    public async Task NoSubmodulesIsTheCloneAsItWas()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Lode");
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));

        var clone = RepositoryClone.Clone(code.Origin("Lode"), destination, null, null, Cancel.None);
        var result = RepositoryClone.WithSubmodules(clone, null, Cancel.None, fileSubmodules);

        await Assert.That(RepositoryClone.SubmoduleUrls(destination)).IsEmpty();
        await Assert.That(result).IsEqualTo(clone);
    }

    // A submodule that can't be fetched, its origin gone say, fails the whole, saying so and what
    // git said, as its folder is empty and the clone won't build; but the clone stays, the rest of
    // it as cloned, for it to be fetched once what stopped it is put right.
    [Test]
    public async Task ASubmoduleThatCanNotBeFetchedLeavesTheClone()
    {
        using var code = new CodeCommandsTests.Scratch();
        WithSubmodule(code, "Lode", Slashed(code.Origin("Missing")));
        var destination = code.At("Code", "Lode");
        Directory.CreateDirectory(code.At("Code"));

        var clone = RepositoryClone.Clone(code.Origin("Lode"), destination, null, null, Cancel.None);
        var result = RepositoryClone.WithSubmodules(clone, null, Cancel.None, fileSubmodules);

        await Assert.That(clone.Succeeded).IsTrue();
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Repo).IsEqualTo(destination);
        await Assert.That(result.Output).Contains("Cloned, but couldn't fetch its submodules");
        await Assert.That(result.Output).Contains("Missing");
        await Assert.That(File.Exists(Path.Combine(destination, "tracked.txt"))).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(destination, "Library"))).IsEmpty();
    }

    // As git takes a submodule's url: whole as it is, or, starting ./ or ../, from origin's, each
    // ../ a folder up from it, an ssh one's host too.
    [Test]
    public async Task SubmoduleUrlFromOrigin()
    {
        await Assert.That(RepositoryClone.SubmoduleUrl("https://github.com/other/library.git", "https://github.com/octo/repo.git")).IsEqualTo("https://github.com/other/library.git");
        await Assert.That(RepositoryClone.SubmoduleUrl("../library.git", "https://github.com/octo/repo.git")).IsEqualTo("https://github.com/octo/library.git");
        await Assert.That(RepositoryClone.SubmoduleUrl("../../other/library", "https://github.com/octo/repo/")).IsEqualTo("https://github.com/other/library");
        await Assert.That(RepositoryClone.SubmoduleUrl("./library", "https://github.com/octo/repo")).IsEqualTo("https://github.com/octo/repo/library");
        await Assert.That(RepositoryClone.SubmoduleUrl("../library.git", "git@github.com:octo/repo.git")).IsEqualTo("git@github.com:octo/library.git");
        await Assert.That(RepositoryClone.SubmoduleUrl("../library.git", "git@host:repo.git")).IsEqualTo("git@host:library.git");
    }

    // Through the dialog: asked, cloned into the code folder, and the file the link names is the
    // one opened.
    [Test]
    [WindowThread]
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
    [WindowThread]
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

    // Through the dialog, from a link, which a web page could make point anywhere: once cloned,
    // it asks before fetching the submodules, saying where each comes from, and fetched, they are
    // checked out, and the file the link names is opened.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task OfferAsksBeforeFetchingTheSubmodules()
    {
        using var code = new CodeCommandsTests.Scratch();
        WithSubmodule(code, "Lode");
        var codeFolder = code.At("Code");
        Directory.CreateDirectory(codeFolder);
        var headings = new List<string>();
        string? asked = null;

        string? opened;
        using (Rewrite("https://github.com/LodeTests/Lode", code.Origin("Lode"), ("protocol.file.allow", "always")))
        {
            opened = OfferAnswering(
                "x-github-client://openRepo/https://github.com/LodeTests/Lode?filepath=tracked.txt",
                codeFolder,
                null,
                page =>
                {
                    headings.Add(page.Heading ?? "");
                    if (page.Heading == "Fetch the submodules of LodeTests/Lode?")
                    {
                        asked = page.Text;
                    }

                    Click(page, "Clone it into the code folder");
                    Click(page, "Fetch them");
                });
        }

        await Assert.That(opened).IsEqualTo(Path.Combine(codeFolder, "Lode", "tracked.txt"));
        await Assert.That(headings).IsEquivalentTo(
            [
                "LodeTests/Lode isn't cloned in the code folder",
                "Cloning LodeTests/Lode",
                "Fetch the submodules of LodeTests/Lode?",
                "Fetching the submodules of LodeTests/Lode"
            ],
            CollectionOrdering.Matching);
        await Assert.That(asked).Contains(Slashed(code.Origin("Library")));
        await Assert.That(File.Exists(Path.Combine(codeFolder, "Lode", "Library", "tracked.txt"))).IsTrue();
    }

    // Declined, the clone opens without them, their folder left empty, and nothing is fetched
    // from where they would have come.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task OfferLeavesTheSubmodulesOutWhenDeclined()
    {
        using var code = new CodeCommandsTests.Scratch();
        WithSubmodule(code, "Lode");
        var codeFolder = code.At("Code");
        Directory.CreateDirectory(codeFolder);
        var headings = new List<string>();

        string? opened;
        using (Rewrite("https://github.com/LodeTests/Lode", code.Origin("Lode"), ("protocol.file.allow", "always")))
        {
            opened = OfferAnswering(
                "x-github-client://openRepo/https://github.com/LodeTests/Lode",
                codeFolder,
                null,
                page =>
                {
                    headings.Add(page.Heading ?? "");
                    Click(page, "Clone it into the code folder");
                    Click(page, "Open it without them");
                });
        }

        await Assert.That(opened).IsEqualTo(Path.Combine(codeFolder, "Lode"));
        await Assert.That(headings).IsEquivalentTo(
            [
                "LodeTests/Lode isn't cloned in the code folder",
                "Cloning LodeTests/Lode",
                "Fetch the submodules of LodeTests/Lode?"
            ],
            CollectionOrdering.Matching);
        await Assert.That(File.Exists(Path.Combine(codeFolder, "Lode", "tracked.txt"))).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(codeFolder, "Lode", "Library"))).IsEmpty();
    }

    // Through the dialog, a submodule that can't be fetched says why, and the clone, there all
    // the same, is opened.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task OfferSaysWhyTheSubmodulesCouldNotBeFetched()
    {
        using var code = new CodeCommandsTests.Scratch();
        WithSubmodule(code, "Lode", Slashed(code.Origin("Missing")));
        var codeFolder = code.At("Code");
        Directory.CreateDirectory(codeFolder);
        var headings = new List<string>();
        string? reason = null;

        string? opened;
        using (Rewrite("https://github.com/LodeTests/Lode", code.Origin("Lode"), ("protocol.file.allow", "always")))
        {
            opened = OfferAnswering(
                "x-github-client://openRepo/https://github.com/LodeTests/Lode",
                codeFolder,
                null,
                page =>
                {
                    headings.Add(page.Heading ?? "");
                    Click(page, "Clone it into the code folder");
                    Click(page, "Fetch them");
                    if (page.Icon == TaskDialogIcon.Warning)
                    {
                        reason = page.Text;
                        Click(page, "Open it as it is");
                    }
                });
        }

        await Assert.That(opened).IsEqualTo(Path.Combine(codeFolder, "Lode"));
        await Assert.That(headings).IsEquivalentTo(
            [
                "LodeTests/Lode isn't cloned in the code folder",
                "Cloning LodeTests/Lode",
                "Fetch the submodules of LodeTests/Lode?",
                "Fetching the submodules of LodeTests/Lode",
                "Cloned LodeTests/Lode, but couldn't fetch its submodules"
            ],
            CollectionOrdering.Matching);
        await Assert.That(reason).StartsWith("fatal:");
        await Assert.That(File.Exists(Path.Combine(codeFolder, "Lode", "tracked.txt"))).IsTrue();
    }

    // The dialog answered by answer, with forward as the command of the app the link was for.
    // Put up for real, as answer clicks its buttons.
    static string? OfferAnswering(string link, string codeFolder, string? forward, Action<TaskDialogPage> answer)
    {
        var links = new RepositoryLinks(
            @"Software\Lode.Tests.NotTaken",
            "lode",
            @"Software\Lode.Tests.NotTaken.Associations",
            (_, _) => forward);
        var showing = MessageBoxes.ShowingDialog;
        MessageBoxes.ShowingDialog = MessageBoxes.ShowDialogNow;
        RepositoryClone.PageShown = answer;
        try
        {
            return RepositoryClone.Offer(RepositoryLink.Parse(link)!, codeFolder, links);
        }
        finally
        {
            RepositoryClone.PageShown = null;
            MessageBoxes.ShowingDialog = showing;
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

    // Git, and so the clone, gets origin in the temp folder for the site's url, and whatever more
    // of git's config is given, as the dialog gives git nothing of its own.
    static Restore Rewrite(string url, string origin, params (string Key, string Value)[] more)
    {
        var restore = new Restore();
        (string Key, string Value)[] entries = [($"url.{origin.Replace('\\', '/')}.insteadOf", url), .. more];
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", entries.Length.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < entries.Length; index++)
        {
            Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{index}", entries[index].Key);
            Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{index}", entries[index].Value);
        }

        return restore;
    }

    sealed class Restore : IDisposable
    {
        static readonly string[] names = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1"];
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
