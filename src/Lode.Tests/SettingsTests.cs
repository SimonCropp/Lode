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
}
