/// <summary>
/// The issue URL Lode opens after an error. Its title is built from a message that can carry a
/// file path, so it holds whatever characters the path does.
/// </summary>
public class IssueLauncherTests
{
    [Test]
    public async Task A_hash_in_the_title_does_not_start_a_fragment()
    {
        // Perfectly ordinary in a solution directory, and everything after it would be read by
        // the browser as a fragment: no body, and a title ending at the hash
        const string message = @"Could not find a part of the path 'C:\code\C#\readme.md'";

        var url = IssueLauncher.BuildUrl(message);

        await Assert.That(url).DoesNotContain("#");
        await Assert.That(Query(url)["title"]).IsEqualTo(message);
        await Assert.That(Query(url)["body"]).IsNotEmpty();
    }

    [Test]
    public async Task An_ampersand_in_the_title_does_not_truncate_it()
    {
        const string message = "Could not rename 'R&D.txt'";

        var url = IssueLauncher.BuildUrl(message);

        await Assert.That(Query(url)["title"]).IsEqualTo(message);
        await Assert.That(Query(url)["body"]).IsNotEmpty();
    }

    /// <summary>
    /// Handed over as plain text and encoded once, after the version and OS.
    /// </summary>
    [Test]
    public async Task Keeps_the_body_it_is_given()
    {
        var url = IssueLauncher.BuildUrl("TheTitle", "\n * Action: R&D #1");

        var body = Query(url)["body"];
        await Assert.That(url).StartsWith("https://github.com/SimonCropp/Lode/issues/new?");
        await Assert.That(body).Contains("* Lode Version:");
        await Assert.That(body).EndsWith("* Action: R&D #1");
    }

    /// <summary>
    /// GitHub turns away a link that is too long, which a deep stack trace makes one: it is cut
    /// short from its end, keeping the frames nearest what threw, and says so.
    /// </summary>
    [Test]
    public async Task A_long_stack_trace_is_cut_to_what_a_link_can_hold()
    {
        var frames = Enumerable
            .Range(1, 200)
            .Select(_ => $@"   at Lode.Some.Type.Method{_}(String path) in C:\Code\Lode\src\Lode\Some\Type.cs:line {_}");
        var exception = $"System.InvalidOperationException: Broken\n{string.Join('\n', frames)}";

        var whole = IssueLauncher.UrlForException("Search failed", "System.InvalidOperationException: Broken\n   at Lode.Some.Type.Method1()");
        var cut = IssueLauncher.UrlForException("Search failed", exception);

        var body = Query(cut)["body"].ReplaceLineEndings("\n");
        await Assert.That(Query(whole)["body"].ReplaceLineEndings("\n")).EndsWith("   at Lode.Some.Type.Method1()\n```");
        await Assert.That(cut.Length).IsLessThanOrEqualTo(7500);
        await Assert.That(cut.Length).IsGreaterThan(7000);
        await Assert.That(body).Contains("Type.Method1(String path)");
        await Assert.That(body).DoesNotContain("Type.Method200(");
        await Assert.That(body).EndsWith("… cut short, the whole of it being too long for a link.\n```");
    }

    [Test]
    public async Task A_message_is_raised_once_a_run()
    {
        var message = Guid.NewGuid().ToString();

        await Assert.That(IssueLauncher.FirstTime(message)).IsTrue();
        await Assert.That(IssueLauncher.FirstTime(message)).IsFalse();
        await Assert.That(IssueLauncher.FirstTime(Guid.NewGuid().ToString())).IsTrue();
    }

    static Dictionary<string, string> Query(string url) =>
        url[(url.IndexOf('?') + 1)..]
            .Split('&')
            .Select(_ => _.Split('=', 2))
            .ToDictionary(_ => _[0], _ => WebUtility.UrlDecode(_[1]));
}
