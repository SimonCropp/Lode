/// <summary>
/// What is typed for a new branch or tag made a name git takes, as GitHub Desktop makes it: its
/// sanitize-ref-name tests, and what New branch says of it.
/// </summary>
[STAThreadExecutor]
[NotInParallel("windows")]
public class RefNameTests
{
    [Test]
    [Arguments("this-is/fine", "this-is/fine")]
    [Arguments("branch--name", "branch--name")]
    [Arguments("SponsorCheck 0.28.0", "SponsorCheck-0.28.0")]
    [Arguments(".this..is\\not fine:yo?|is-it", "this-is-not-fine-yo-is-it")]
    [Arguments("hello/", "hello-")]
    [Arguments("++but-can-still-keep-the-rest", "but-can-still-keep-the-rest")]
    [Arguments("--but-can-still-keep-the-rest", "but-can-still-keep-the-rest")]
    [Arguments("foo.lock.lock", "foo.lock-")]
    [Arguments("hello\r\nworld", "hello-world")]
    [Arguments(".first.dot.is.not.ok", "first.dot.is.not.ok")]
    [Arguments("ends.", "ends-")]
    [Arguments("at@{1}", "at-1}")]
    [Arguments("<a|b>\"c\"", "a-b-c-")]
    [Arguments("...", "")]
    public async Task Sanitized(string typed, string name) =>
        await Assert.That(RefName.Sanitized(typed)).IsEqualTo(name);

    // The line under New branch's box: the name it will be, or that none can be made, and
    // nothing where what is typed will do as it is.
    [Test]
    public async Task NameNote()
    {
        await Assert.That(CodeMenu.GitTextForm.NameNote("SponsorCheck 0.28.0", "Will be created as")).IsEqualTo("Will be created as SponsorCheck-0.28.0.");
        await Assert.That(CodeMenu.GitTextForm.NameNote("...", "Will be created as")).IsEqualTo("... is not a valid name.");
        await Assert.That(CodeMenu.GitTextForm.NameNote("feature/fine", "Will be created as")).IsEqualTo("");
        await Assert.That(CodeMenu.GitTextForm.NameNote("", "Will be created as")).IsEqualTo("");
    }

    // Typed with a space, New branch can still be accepted, for the name it says it will be
    // made; typed with nothing that would do, it can't.
    [Test]
    public async Task NewBranchTakesWhatGitWouldMakeOfIt()
    {
        using var form = new CodeMenu.GitTextForm("New branch", "Make a branch.", "Name:", "", "Create branch", [], namedAs: "Will be created as");
        var input = form.Controls.Find("value", true).Single();
        var accept = form.Controls.Find("accept", true).Single();
        var note = form.Controls.Find("note", true).Single();
        await Assert.That(accept.Enabled).IsFalse();

        input.Text = "SponsorCheck 0.28.0";
        await Assert.That(accept.Enabled).IsTrue();
        await Assert.That(form.Value).IsEqualTo("SponsorCheck-0.28.0");
        await Assert.That(note.Text).IsEqualTo("Will be created as SponsorCheck-0.28.0.");

        input.Text = "..";
        await Assert.That(accept.Enabled).IsFalse();
        await Assert.That(note.Text).IsEqualTo(".. is not a valid name.");

        input.Text = "fine";
        await Assert.That(accept.Enabled).IsTrue();
        await Assert.That(note.Text).IsEqualTo("");
    }
}
