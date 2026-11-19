public class SettingsTests
{
    [Test]
    public async Task SaveWritesJson()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings", "settings.json");
        var settings = Settings.Load(path);
        settings.ShowHidden = true;
        settings.Left = 10;
        settings.Top = 20;
        settings.Width = 1200;
        settings.Height = 800;
        settings.SplitterDistance = 250;
        settings.Tabs = [@"C:\Code", "This PC"];
        settings.TabsLastShown = [new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), new(2026, 11, 19, 17, 30, 0, TimeSpan.Zero)];
        settings.ActiveTab = 1;

        settings.Save();

        await VerifyFile(path);
    }

    [Test]
    public async Task LoadReadsWhatSaveWrote()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        var settings = Settings.Load(path);
        settings.Maximized = true;
        settings.Tabs = [@"D:\Work"];
        settings.Save();

        await Verify(Settings.Load(path));
    }

    // Git was one action, a submenu of pull, push, discard and purge. Settings saved then have
    // each of those where Git was, nested under Git, so the menu and the bar are as they were.
    [Test]
    public async Task GitPlacementCarriesOverToItsActions()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, """{ "Actions": { "git": { "Button": true, "Menu": false } } }""");

        var settings = Settings.Load(path);

        await Verify(
            new
            {
                gitKept = settings.Actions.ContainsKey("git"),
                actions = LodeActions.GitIds.Select(_ => new
                {
                    id = _,
                    button = settings.ShowsAsButton(_),
                    menu = settings.ShowsInMenu(_),
                    buttonNested = settings.NestsButton(_),
                    menuNested = settings.NestsInMenu(_)
                })
            });
    }

    [Test]
    public async Task CorruptFileFallsBackToDefaults()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, "{ not json");

        await Verify(Settings.Load(path));
    }

    /// <summary>
    /// The first save would otherwise write the defaults over it, and every tab and option in it
    /// was gone without a word.
    /// </summary>
    [Test]
    public async Task CorruptFileIsKeptAsideAndSaid()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var settings = Settings.Load(path);
        settings.ShowHidden = true;
        settings.Save();

        var kept = Settings.UnreadablePath(path);
        await Assert.That(await File.ReadAllTextAsync(kept)).IsEqualTo("{ not json");
        await Assert.That(Settings.Load(path).ShowHidden).IsTrue();
        await Assert.That(settings.LoadProblem).Contains(kept);
        await Assert.That(settings.LoadBug).IsNull();
    }

    /// <summary>
    /// Locked by another program, say, and quite possibly fine: not saved over with the defaults.
    /// </summary>
    [Test]
    public async Task FileThatCannotBeOpenedIsNotSavedOver()
    {
        using var fixture = Fixture.Create();
        var path = fixture.At("settings.json");
        await File.WriteAllTextAsync(path, """{ "ShowHidden": true }""");

        Settings settings;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = Settings.Load(path);
        }

        settings.Save();

        await Assert.That(settings.LoadProblem).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("""{ "ShowHidden": true }""");
    }
}
