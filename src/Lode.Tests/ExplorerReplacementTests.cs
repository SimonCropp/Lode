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
