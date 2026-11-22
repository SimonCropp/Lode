public class RepositoryLinksTests
{
    const string gitHubDesktop = """
                                 "C:\Users\Someone\AppData\Local\GitHubDesktop\app-3.6.11\GitHubDesktop.exe" --protocol-launcher "%1"
                                 """;

    // GitHub Desktop registers its links in the user's classes, as Electron apps do.
    static void SeedGitHubDesktop(RegistryScratch scratch)
    {
        scratch.Set("x-github-client", null, "URL:x-github-client");
        scratch.Set("x-github-client", "URL Protocol", "");
        scratch.Set(@"x-github-client\shell\open\command", null, gitHubDesktop);
    }

    // Lode's command replaces GitHub Desktop's, which is kept to put back, and to hand on links
    // for repositories not in the code folder.
    [Test]
    public async Task TakeThenGiveBackGitHubDesktopsLinks()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.Dump();
        var links = scratch.Links();
        var handler = RepositoryLinks.GitHubDesktop;

        links.Take(handler);
        var taken = scratch.Dump();
        var state = new
        {
            taken = links.IsTaken(handler),
            here = links.IsTakenHere(handler),
            forward = links.ForwardCommand(handler)
        };
        links.GiveBack(handler);

        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(before));
        await Verify(
            new
            {
                taken,
                state
            });
    }

    // Visual Studio's links are only in the machine's classes. Take adds the scheme to the user's,
    // and GiveBack takes it all away again.
    [Test]
    public async Task TakeThenGiveBackVisualStudiosLinks()
    {
        using var scratch = new RegistryScratch();
        var links = scratch.Links();
        var handler = RepositoryLinks.VisualStudio;

        links.Take(handler);
        var taken = scratch.Dump();
        var forward = links.ForwardCommand(handler);
        links.GiveBack(handler);

        await Verify(
            new
            {
                taken,
                forward,
                givenBack = scratch.Dump()
            });
    }

    // A second Take, by another copy of Lode, points the links at it and still keeps what the
    // first one displaced.
    [Test]
    public async Task TakingAgainKeepsWhatWasDisplaced()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.Dump();
        var handler = RepositoryLinks.GitHubDesktop;
        scratch.Links().Take(handler);
        var other = scratch.Links(
            """
            "D:\Other\Lode.exe"
            """);

        var hereBefore = scratch.Links().IsTakenHere(handler);
        other.Take(handler);
        var hereAfter = scratch.Links().IsTakenHere(handler);
        var forward = other.ForwardCommand(handler);
        other.GiveBack(handler);

        await Assert.That(hereBefore).IsTrue();
        await Assert.That(hereAfter).IsFalse();
        await Assert.That(forward).IsEqualTo("\"C:\\Apps\\GitHub Desktop.exe\" \"%1\"");
        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(before));
    }

    // Giving back links Lode never took leaves them alone.
    [Test]
    public async Task GiveBackLeavesAnotherAppsLinksAlone()
    {
        using var scratch = new RegistryScratch();
        SeedGitHubDesktop(scratch);
        var before = scratch.Dump();

        scratch.Links().GiveBack(RepositoryLinks.GitHubDesktop);

        await Assert.That(JsonSerializer.Serialize(scratch.Dump())).IsEqualTo(JsonSerializer.Serialize(before));
    }

    [Test]
    public async Task HandlerOf()
    {
        await Assert.That(RepositoryLinks.HandlerOf("x-github-client://openRepo/https://github.com/owner/repo")).IsEqualTo(RepositoryLinks.GitHubDesktop);
        await Assert.That(RepositoryLinks.HandlerOf("GIT-CLIENT://clone?repo=x")).IsEqualTo(RepositoryLinks.VisualStudio);
        await Assert.That(RepositoryLinks.HandlerOf(@"D:\Code")).IsNull();
        await Assert.That(RepositoryLinks.HandlerOf("x-github-desktop-auth://oauth")).IsNull();
    }
}
