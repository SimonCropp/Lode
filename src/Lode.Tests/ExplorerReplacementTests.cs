public class ExplorerReplacementTests
{
    const string displacedDefault = "openinother";

    [Test]
    public async Task ReplaceRegistersFoldersDrivesAndWinE()
    {
        using var scratch = new RegistryScratch();

        scratch.Replacement().Replace();

        await Verify(scratch.Dump());
    }

    [Test]
    public async Task RestoreRemovesWhatReplaceAdded()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        replacement.Replace();

        replacement.Restore();

        await Verify(scratch.Dump());
    }

    // Another tool had made itself the default for folders; restoring hands folders back to it.
    [Test]
    public async Task RestoreBringsBackADisplacedDefault()
    {
        using var scratch = new RegistryScratch();
        SeedOtherTool(scratch);
        var replacement = scratch.Replacement();
        replacement.Replace();
        var replaced = scratch.Dump();

        replacement.Restore();

        await Verify(
            new
            {
                replaced,
                restored = scratch.Dump()
            });
    }

    // A second Replace, say from a copy installed elsewhere, points folders at that copy and
    // still remembers what the first one displaced.
    [Test]
    public async Task ReplacingAgainKeepsTheDisplacedDefault()
    {
        using var scratch = new RegistryScratch();
        SeedOtherTool(scratch);
        scratch.Replacement().Replace();
        var again = scratch.Replacement(
            """
            "D:\Other\Lode.exe"
            """);
        again.Replace();
        var replacedAgain = scratch.Dump();

        again.Restore();

        await Verify(
            new
            {
                replacedAgain,
                restored = scratch.Dump()
            });
    }

    [Test]
    public async Task RestoreLeavesAnotherToolsWinEAlone()
    {
        using var scratch = new RegistryScratch();
        scratch.Set(
            @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command",
            null,
            """
            "C:\Other\other.exe"
            """);

        scratch.Replacement().Restore();

        await Verify(scratch.Dump());
    }

    // Another file manager has Win+E. Replace takes it, keeping what it had, and Restore gives
    // it back as it was, rather than deleting it, which would hand Win+E to Explorer.
    [Test]
    public async Task RestoreGivesWinEBackToTheToolReplaceTookItFrom()
    {
        using var scratch = new RegistryScratch();
        const string winE = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";
        scratch.Set(winE, null, "\"C:\\Other\\other.exe\"");
        scratch.Set(winE, "DelegateExecute", "");
        var before = scratch.Dump();
        var replacement = scratch.Replacement();

        replacement.Replace();
        // Again, which leaves what it kept as it was.
        replacement.Replace();
        var replaced = scratch.Dump();
        replacement.Restore();

        await Verify(
            new
            {
                before,
                replaced,
                restored = scratch.Dump()
            });
    }

    // Another file manager already answers the explore verb; Replace takes the default verb but
    // leaves that tool's explore, and Restore keeps it.
    [Test]
    public async Task ReplaceLeavesAnotherToolsExploreAlone()
    {
        using var scratch = new RegistryScratch();
        scratch.Set(
            @"Directory\shell\explore\command",
            null,
            """
            "C:\Other\other.exe" "%1"
            """);
        var replacement = scratch.Replacement();
        replacement.Replace();
        var replaced = scratch.Dump();
        var complete = replacement.IsComplete;

        replacement.Restore();

        await Verify(
            new
            {
                replaced,
                complete,
                restored = scratch.Dump()
            });
    }

    // A Replace by a Lode from before the explore verb: replacing again adds it, and Restore
    // removes everything either added.
    [Test]
    public async Task ReplacingAfterAnOlderReplaceAddsExplore()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        scratch.OlderReplace(replacement);
        var older = Describe(replacement);

        replacement.Replace();
        var replaced = Describe(replacement);
        replacement.Restore();

        await Verify(
            new
            {
                older,
                replaced,
                restored = scratch.Dump()
            });
    }

    // A Replace by a Lode that added the explore verb to Directory and Drive but not to Folder,
    // which Chromium and Electron apps name: it is not complete, and replacing again adds it.
    [Test]
    public async Task ReplacingAfterAReplaceWithoutFolderExploreAddsIt()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        scratch.ReplaceWithoutFolderExplore(replacement);
        var older = Describe(replacement);

        replacement.Replace();
        var replaced = Describe(replacement);
        var folder = scratch.Dump()[@"Folder\"];
        replacement.Restore();

        await Verify(
            new
            {
                older,
                replaced,
                folder,
                restored = scratch.Dump()
            });
    }

    // Some tools leave a copy of Explorer's explore verb on Folder in the user's classes, which
    // Chromium and Electron apps (GitHub Desktop's Show in Explorer) find, and so open Explorer.
    // It is Explorer's, not another tool's: a Replace that left it is not complete, replacing
    // takes it over, and Restore gives it back to Explorer as it was.
    [Test]
    public async Task ReplaceTakesOverACopyOfExplorersExplore()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        // What a Replace that took them for another tool's left.
        scratch.ReplaceWithoutFolderExplore(replacement);
        scratch.Delete(@"Drive\shell\explore");
        scratch.CopyExplorersExplore("Folder");
        scratch.CopyExplorersExplore("Drive", @"%SystemRoot%\Explorer.exe");
        var copied = scratch.Dump();
        var older = Describe(replacement);

        replacement.Replace();
        var replaced = Describe(replacement);
        var folder = scratch.Dump()[@"Folder\"];
        replacement.Replace();
        var replacedAgain = scratch.Dump()[@"Folder\"];
        replacement.Restore();
        var restored = scratch.Dump();

        await Assert.That(Json(replacedAgain)).IsEqualTo(Json(folder));
        await Assert.That(Json(restored[@"Folder\"])).IsEqualTo(Json(copied[@"Folder\"]));
        await Assert.That(Json(Explore(restored, "Drive"))).IsEqualTo(Json(Explore(copied, "Drive")));
        var (driveCommand, driveCommandKind) = scratch.Raw(@"Drive\shell\explore\command", null);
        await Verify(
            new
            {
                older,
                replaced,
                folder,
                restoredFolder = restored[@"Folder\"],
                // Unexpanded, as Dump would show it expanded.
                driveCommand,
                driveCommandKind
            });
    }

    static object Explore(Dictionary<string, object> dump, string folderClass)
    {
        var shell = (Dictionary<string, object>) ((Dictionary<string, object>) dump[$@"{folderClass}\"])[@"shell\"];
        return shell[@"explore\"];
    }

    // Keys sorted, since the registry need not list values in the order they were written.
    static string Json(object value) =>
        JsonSerializer.Serialize(Sorted(value));

    static object Sorted(object value)
    {
        if (value is not Dictionary<string, object> dictionary)
        {
            return value;
        }

        return new SortedDictionary<string, object>(
            dictionary.ToDictionary(_ => _.Key, _ => Sorted(_.Value)),
            StringComparer.Ordinal);
    }

    [Test]
    public async Task State()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        var anotherCopy = scratch.Replacement(
            """
            "D:\Other\Lode.exe"
            """);

        var before = Describe(replacement);
        replacement.Replace();
        var replaced = Describe(replacement);
        var seenFromAnotherCopy = Describe(anotherCopy);
        replacement.Restore();
        var restored = Describe(replacement);

        await Verify(
            new
            {
                before,
                replaced,
                seenFromAnotherCopy,
                restored
            });
    }

    static object Describe(ExplorerReplacement replacement) =>
        new
        {
            replacement.IsActive,
            replacement.IsActiveHere,
            replacement.IsComplete,
            RegisteredCommand = replacement.RegisteredCommand ?? "(none)"
        };

    static void SeedOtherTool(RegistryScratch scratch)
    {
        scratch.Set(@"Directory\shell", null, displacedDefault);
        scratch.Set(
            $@"Directory\shell\{displacedDefault}\command",
            null,
            """
            "C:\Other\other.exe" "%1"
            """);
    }
}
