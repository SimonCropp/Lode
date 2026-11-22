/// <summary>
/// The Clone dialog, its sites answered for: what it lists, leaving out what is cloned, and what
/// it clones where.
/// </summary>
[STAThreadExecutor]
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
            .IsEquivalentTo(["Your repositories", "octo/AngleSharp", "octo/notes", "dotnet", "dotnet/runtime", "dotnet/winforms"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        dialog.FilterBox.Text = "win";
        await Assert.That(dialog.Rows.Select(_ => _.Text)).IsEquivalentTo(["dotnet", "dotnet/winforms"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
        var dialog = new CloneDialog(
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

    // A clone in the code folder, as its origin in its config says.
    static void Cloned(Fixture fixture, string name, string origin)
    {
        var git = fixture.At(name, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = {origin}\n");
    }
}
