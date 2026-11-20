public class TypedCommandTests
{
    // A program and what it is given, the program in quotes where it has spaces. A path, or a
    // URL, is no command.
    [Test]
    public async Task Parse()
    {
        static string? Parsed(string text)
        {
            if (TypedCommand.Parse(text) is not { } command)
            {
                return null;
            }

            return $"{command.Program}|{command.Arguments}";
        }

        await Assert.That(Parsed("cmd")).IsEqualTo("cmd|");
        await Assert.That(Parsed("  code .  ")).IsEqualTo("code|.");
        await Assert.That(Parsed("git  log --oneline")).IsEqualTo("git|log --oneline");
        await Assert.That(Parsed("\"my tool\" -x")).IsEqualTo("my tool|-x");
        await Assert.That(Parsed("C:\\Tools\\run.exe")).IsNull();
        await Assert.That(Parsed("..\\src")).IsNull();
        await Assert.That(Parsed("https://example.com")).IsNull();
        await Assert.That(Parsed("\"unclosed")).IsNull();
        await Assert.That(Parsed("   ")).IsNull();
    }
}
