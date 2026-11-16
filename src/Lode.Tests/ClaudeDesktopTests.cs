public class ClaudeDesktopTests
{
    // The folder is escaped whole, so a + or & in it is not read as a space or another parameter.
    [Test]
    public async Task Link()
    {
        await Assert.That(ClaudeDesktop.Link(@"C:\Code\Lode")).IsEqualTo("claude://code/new?folder=C%3A%5CCode%5CLode");
        await Assert.That(ClaudeDesktop.Link(@"C:\Code\C++ & Co")).IsEqualTo("claude://code/new?folder=C%3A%5CCode%5CC%2B%2B%20%26%20Co");
    }

    // Windows' own Settings app takes ms-settings: links on every Windows 10 and 11, and, like
    // Claude, is a packaged app, which has a name but no command; nothing takes the other.
    [Test]
    public async Task HasApp()
    {
        await Assert.That(ClaudeDesktop.HasApp("ms-settings")).IsTrue();
        await Assert.That(ClaudeDesktop.HasApp("lode-no-such-scheme")).IsFalse();
    }
}
