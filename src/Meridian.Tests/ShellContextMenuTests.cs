/// <summary>
/// The entries Meridian leaves off the shell's menu. What else is on it depends on the
/// software installed, so only the absences are checked. The text checks assume English
/// Windows; elsewhere they pass without proving anything.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ShellContextMenuTests
{
    [Test]
    public Task Folder() =>
        VerifyAbsences(_ => _.At("Docs"));

    [Test]
    public Task TextFile() =>
        VerifyAbsences(
            _ =>
            {
                var path = _.At("notes.txt");
                File.WriteAllText(path, "notes");
                return path;
            });

    // Takes the caller's name so each test gets its own fixture folder and they can run side by side.
    static async Task VerifyAbsences(Func<Fixture, string> pathFor, [CallerMemberName] string member = "")
    {
        using var fixture = Fixture.Create(member: member);
        var entries = ShellContextMenu.Describe(IntPtr.Zero, [pathFor(fixture)]);
        var texts = entries.Select(_ => _.Text.Replace("&", "")).ToList();

        await Verify(
            new
            {
                sendTo = texts.Contains("Send to"),
                includeInLibrary = texts.Contains("Include in library"),
                restorePreviousVersions = entries.Any(_ => _.Verb == "PreviousVersions"),
                editInNotepad = entries.Any(_ => _.Verb == "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"),
                doubledSeparator = texts.Zip(texts.Skip(1)).Any(_ => _.First.Length == 0 && _.Second.Length == 0)
            });
    }
}
