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

        await Verify(agents.Select(_ => $"{_.Name}: {Path.GetRelativePath(fixture.Root, _.Path)}"));
    }

    // Each tab runs the agent in place of the shell.
    [Test]
    public async Task TerminalArguments() =>
        await Assert.That(FileOperations.TerminalArguments([@"C:\Code\Lode", @"C:\Code\Two Words"], @"C:\Agents\claude.exe"))
            .IsEquivalentTo(
                ["-d", @"C:\Code\Lode", @"C:\Agents\claude.exe", ";", "new-tab", "-d", @"C:\Code\Two Words", @"C:\Agents\claude.exe"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
}
