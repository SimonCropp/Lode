public class CodingAgentsTests
{
    // Each agent is found in the first folder that has it as a program, .cmd or .bat file, as
    // Windows would run it. npm's script without an extension, which only other shells can run,
    // does not count, and a folder that is not there is passed over.
    [Test]
    public async Task Find()
    {
        using var fixture = Fixture.Create();
        var first = Directory.CreateDirectory(fixture.At("First")).FullName;
        var second = Directory.CreateDirectory(fixture.At("Second")).FullName;
        foreach (var path in new[]
                 {
                     Path.Combine(first, "codex.cmd"),
                     Path.Combine(first, "gemini"),
                     Path.Combine(second, "claude.exe"),
                     Path.Combine(second, "codex.exe")
                 })
        {
            await File.WriteAllTextAsync(path, "");
        }

        var agents = CodingAgents.Find([fixture.At("Missing"), first, second]);

        await Verify(agents.Select(_ => $"{_.Name}: {Path.GetRelativePath(fixture.Root, _.Path)}"))
            .Snapshot(
                """
                [
                  Claude Code: Second\claude.exe,
                  Codex CLI: First\codex.cmd
                ]
                """);
    }

    // The PATH is looked through once, off the thread that asks: a folder on it that waits on a
    // share keeps nothing waiting. Until it is done there are none, after it the same answer,
    // however often asked, an agent installed meanwhile included.
    [Test]
    public async Task ThePathIsLookedThroughOnceBehindTheWindow()
    {
        using var fixture = Fixture.Create();
        var folder = Directory.CreateDirectory(fixture.At("Agents")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "claude.exe"), "");
        using var release = new ManualResetEventSlim();
        var searched = 0;
        var installed = CodingAgents.FoundOnce(() =>
        {
            Interlocked.Increment(ref searched);
            release.Wait(TimeSpan.FromSeconds(1));
            return [folder];
        });

        var started = Stopwatch.GetTimestamp();
        var whileLooking = installed();
        var took = Stopwatch.GetElapsedTime(started);
        release.Set();
        await Pump.Until(() => installed().Count > 0);
        await File.WriteAllTextAsync(Path.Combine(folder, "codex.exe"), "");
        var later = installed();

        await Assert.That(took).IsLessThan(TimeSpan.FromMilliseconds(200));
        await Assert.That(whileLooking).IsEmpty();
        await Assert.That(later.Select(_ => _.Name)).IsEquivalentTo(["Claude Code"]);
        await Assert.That(searched).IsEqualTo(1);
    }

    // Each tab runs the agent in place of the shell.
    [Test]
    public async Task TerminalArguments() =>
        await Assert.That(FileOperations.TerminalArguments([new(@"C:\Code\Lode", [@"C:\Agents\claude.exe"]), new(@"C:\Code\Two Words", [@"C:\Agents\claude.exe"])]))
            .IsEquivalentTo(
                ["-d", @"C:\Code\Lode", @"C:\Agents\claude.exe", ";", "new-tab", "-d", @"C:\Code\Two Words", @"C:\Agents\claude.exe"],
                CollectionOrdering.Matching);
}
