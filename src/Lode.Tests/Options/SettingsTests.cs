public class SettingsTests
{
    // Renamed, moved or deleted while Lode wasn't running, the code folder is missed as Lode
    // starts, which then says so. Not one on a share, which would wait on the network.
    [Test]
    public async Task ACodeFolderNoLongerThereIsMissing()
    {
        using var fixture = Fixture.Create();
        var gone = fixture.At("Gone");

        await Assert.That(new Settings().MissingCodeDirectory()).IsNull();
        await Assert.That(new Settings {CodeDirectory = fixture.Root}.MissingCodeDirectory()).IsNull();
        await Assert.That(new Settings {CodeDirectory = gone}.MissingCodeDirectory()).IsEqualTo(gone);
        await Assert.That(new Settings {CodeDirectory = @"\\NotAServer\Share\Code"}.MissingCodeDirectory()).IsNull();
    }

    // Which panes are pinned, for each repository, however its path is written; neither pinned
    // isn't kept.
    [Test]
    public async Task PinnedPanesForEachRepository()
    {
        var settings = new Settings();
        settings.SetPinnedPanes(@"D:\Code\Lode", true, false);
        settings.SetPinnedPanes(@"D:\Code\Other", false, true);
        settings.SetPinnedPanes(@"d:\code\lode\", true, true);

        await Assert.That(settings.PinnedPanes.Count).IsEqualTo(2);
        await Assert.That(settings.PinnedPanesOf(@"D:\Code\Lode")).IsEqualTo(new PinnedPanes(@"d:\code\lode\", true, true));

        settings.SetPinnedPanes(@"D:\Code\Other", false, false);
        await Assert.That(settings.PinnedPanesOf(@"D:\Code\Other")).IsNull();

        for (var index = 0; index < 600; index++)
        {
            settings.SetPinnedPanes($@"D:\Code\Repo{index}", true, false);
        }

        await Assert.That(settings.PinnedPanes.Count).IsEqualTo(500);
        await Assert.That(settings.PinnedPanesOf(@"D:\Code\Repo599")).IsNotNull();
        await Assert.That(settings.PinnedPanesOf(@"D:\Code\Lode")).IsNull();
    }

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
        settings.ColumnWidths["Name"] = 420;
        settings.SetSort(@"C:\Code", "Date", ascending: false);

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
        settings.ColumnWidths["Type"] = 140;
        settings.SetSort(@"D:\Work", "Size", ascending: true);
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
