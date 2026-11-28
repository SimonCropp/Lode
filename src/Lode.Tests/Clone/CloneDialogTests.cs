/// <summary>
/// The Clone dialog, its sites answered for: what it lists, leaving out what is cloned, and what
/// it clones where.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class CloneDialogTests
{
    static HostedListing listing = new(
    [
        new(HostedRepos.GitHub, HostedRepos.YourRepositories, "octo/Lode", "https://github.com/octo/Lode.git", "https://github.com/octo/Lode", false, false),
        new(HostedRepos.GitHub, HostedRepos.YourRepositories, "octo/AngleSharp", "https://github.com/octo/AngleSharp.git", "https://github.com/octo/AngleSharp", true, false),
        new(HostedRepos.GitHub, HostedRepos.YourRepositories, "octo/notes", "https://github.com/octo/notes.git", "https://github.com/octo/notes", false, true),
        new(HostedRepos.GitHub, "dotnet", "dotnet/runtime", "https://github.com/dotnet/runtime.git", "https://github.com/dotnet/runtime", false, false),
        new(HostedRepos.GitHub, "dotnet", "dotnet/winforms", "https://github.com/dotnet/winforms.git", "https://github.com/dotnet/winforms", false, false)
    ],
    []);

    // What is listed, under its headings, less the one the code folder has a clone of; picking
    // one names the folder it goes in.
    [Test]
    public async Task ListsWhatIsntCloned()
    {
        using var fixture = Fixture.Create();
        Cloned(fixture, "Lode", "https://github.com/octo/Lode.git");
        using var dialog = await Open(fixture);

        await Assert.That(dialog.Rows.Select(_ => _.Text))
            .IsEquivalentTo(["Your repositories", "octo/AngleSharp", "octo/notes", "dotnet", "dotnet/runtime", "dotnet/winforms"], CollectionOrdering.Matching);

        dialog.FilterBox.Text = "win";
        await Assert.That(dialog.Rows.Select(_ => _.Text)).IsEquivalentTo(["dotnet", "dotnet/winforms"], CollectionOrdering.Matching);
        await Assert.That(dialog.CloneButton.Enabled).IsFalse();

        dialog.List.SelectedIndex = 1;
        await Assert.That(dialog.PathBox.Text).IsEqualTo(fixture.At("winforms"));
        await Assert.That(dialog.CloneButton.Enabled).IsTrue();
    }

    // A folder already at the path can't be cloned into.
    [Test]
    public async Task NotIntoSomethingThere()
    {
        using var fixture = Fixture.Create();
        using var dialog = await Open(fixture);
        dialog.List.SelectedIndex = 1;
        dialog.PathBox.Text = fixture.At("Docs");

        await Assert.That(dialog.CloneButton.Enabled).IsFalse();
    }

    // A listing that found nothing, only why, as one made offline or before signing in does, is
    // read again the next time Clone opens, rather than shown again until Refresh; one that
    // listed repositories is kept, and shown at once.
    [Test]
    public async Task AFailedListingIsReadAgainNextTime()
    {
        using var fixture = Fixture.Create();
        var asked = 0;
        var list = HostedRepos.ListGitHub;
        HostedRepos.ListGitHub = _ =>
        {
            asked++;
            if (asked == 1)
            {
                return Task.FromResult(HostedListing.Failed("Couldn't list GitHub's repositories: No such host is known."));
            }

            return Task.FromResult(listing);
        };
        CloneDialog.Forget();
        try
        {
            using (var offline = Create(fixture))
            {
                offline.Show();
                await Pump.Until(() => asked == 1 && offline.Loaded.IsCompleted);

                await Assert.That(offline.Rows).IsEmpty();
            }

            using (var online = Create(fixture))
            {
                online.Show();
                await Pump.Until(() => online.Rows.Count > 1 && online.Loaded.IsCompleted);

                await Assert.That(asked).IsEqualTo(2);
            }

            using var again = Create(fixture);
            again.Show();
            await Pump.Until(() => again.Rows.Count > 1 && again.Loaded.IsCompleted);

            await Assert.That(asked).IsEqualTo(2);
        }
        finally
        {
            HostedRepos.ListGitHub = list;
        }
    }

    // Each site says it is finding repositories till it has listed them, whichever finishes
    // first, and going back to one still listing waits for that listing, rather than starting
    // another.
    [Test]
    public async Task EachSiteSaysItIsListingTillItHas()
    {
        using var fixture = Fixture.Create();
        var gitHub = new TaskCompletionSource<HostedListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var azureDevOps = new TaskCompletionSource<HostedListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gitHubAsked = 0;
        var azureDevOpsAsked = 0;
        var listGitHub = HostedRepos.ListGitHub;
        var listAzureDevOps = HostedRepos.ListAzureDevOps;
        HostedRepos.ListGitHub = _ =>
        {
            gitHubAsked++;
            return gitHub.Task;
        };
        HostedRepos.ListAzureDevOps = (_, _) =>
        {
            azureDevOpsAsked++;
            return azureDevOps.Task;
        };
        CloneDialog.Forget();
        try
        {
            using var dialog = Create(fixture);
            dialog.Show();
            await Pump.Until(() => gitHubAsked == 1);
            await Assert.That(Texts(dialog)).IsEquivalentTo([finding]);

            dialog.Pick(CloneDialog.Source.AzureDevOps);
            await Pump.Until(() => azureDevOpsAsked == 1);
            await Assert.That(Texts(dialog)).IsEquivalentTo([finding]);

            dialog.Pick(CloneDialog.Source.GitHub);
            await Assert.That(Texts(dialog)).IsEquivalentTo([finding]);

            dialog.Pick(CloneDialog.Source.AzureDevOps);
            gitHub.SetResult(listing);
            await Pump.For(TimeSpan.FromMilliseconds(200));
            // Shown again, as typing in the filter shows it.
            dialog.FilterBox.Text = "o";
            dialog.FilterBox.Text = "";
            await Assert.That(Texts(dialog)).IsEquivalentTo([finding]);

            dialog.Pick(CloneDialog.Source.GitHub);
            await Pump.Until(() => dialog.Loaded.IsCompleted);
            await Assert.That(Texts(dialog)).IsEquivalentTo(["Your repositories", "octo/AngleSharp", "octo/Lode", "octo/notes", "dotnet", "dotnet/runtime", "dotnet/winforms"], CollectionOrdering.Matching);

            dialog.Pick(CloneDialog.Source.AzureDevOps);
            azureDevOps.SetResult(new([new(HostedRepos.AzureDevOps, "contoso / Shop", "Shop/Api", "https://dev.azure.com/contoso/Shop/_git/Api", "https://dev.azure.com/contoso/Shop/_git/Api", false, false)], []));
            await Pump.Until(() => dialog.Loaded.IsCompleted && dialog.Rows.Count > 1);
            await Assert.That(Texts(dialog)).IsEquivalentTo(["contoso / Shop", "Shop/Api"], CollectionOrdering.Matching);

            await Assert.That(gitHubAsked).IsEqualTo(1);
            await Assert.That(azureDevOpsAsked).IsEqualTo(1);
        }
        finally
        {
            HostedRepos.ListGitHub = listGitHub;
            HostedRepos.ListAzureDevOps = listAzureDevOps;
        }
    }

    const string finding = "Finding repositories…";

    static List<string> Texts(CloneDialog dialog) =>
        dialog.Rows.Select(_ => _.Text).ToList();

#if DEBUG
    [Test]
    public async Task GitHub()
    {
        using var fixture = Fixture.CreateAtFixedPath();
        Cloned(fixture, "Lode", "https://github.com/octo/Lode.git");
        using var dialog = await Open(fixture);
        dialog.List.SelectedIndex = 1;
        dialog.ActiveControl = null;
        Application.DoEvents();

        await Verify(dialog);
    }
#endif

    static async Task<CloneDialog> Open(Fixture fixture)
    {
        var list = HostedRepos.ListGitHub;
        HostedRepos.ListGitHub = _ => Task.FromResult(listing);
        CloneDialog.Forget();
        var dialog = Create(fixture);
        try
        {
            dialog.Show();
            await Pump.Until(() => dialog.Rows.Count > 1 && dialog.Loaded.IsCompleted);
            return dialog;
        }
        finally
        {
            HostedRepos.ListGitHub = list;
        }
    }

    // Off the screen, cloning into the fixture.
    static CloneDialog Create(Fixture fixture) =>
        new(
            new()
            {
                CodeDirectory = fixture.Root
            },
            fixture.Root)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };

    // A clone in the code folder, as its origin in its config says.
    static void Cloned(Fixture fixture, string name, string origin)
    {
        var git = fixture.At(name, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = {origin}\n");
    }
}
