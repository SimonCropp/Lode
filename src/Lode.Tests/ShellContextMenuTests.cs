/// <summary>
/// The entries Lode leaves off the shell's menu. What else is on it depends on the
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
        var sendTo = texts.Contains("Send to");

        try
        {
            await Verify(
                new
                {
                    sendTo,
                    includeInLibrary = texts.Contains("Include in library"),
                    restorePreviousVersions = entries.Any(_ => _.Verb == "PreviousVersions"),
                    editInNotepad = entries.Any(_ => _.Verb == "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"),
                    doubledSeparator = texts.Zip(texts.Skip(1)).Any(_ => _.First.Length == 0 && _.Second.Length == 0)
                });
        }
        catch (Exception exception) when (sendTo)
        {
            // Temporary: CI keeps Send to on a text file's menu where a local run removes it.
            // Says what the submenu held and what it was compared with.
            throw new(SendToDiagnostics(entries), exception);
        }
    }

    static string SendToDiagnostics(List<ShellContextMenu.MenuEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Send to was left on the menu.");
        var submenu = entries.First(_ => _.Text.Replace("&", "") == "Send to").Submenu;
        builder.AppendLine($"Submenu, {submenu.Count} entries:");
        foreach (var entry in submenu)
        {
            builder.AppendLine($"  '{entry.Text}' verb={entry.Verb ?? "(none)"}");
        }

        AppendFolder(builder, "SendTo folder", Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        AppendFolder(
            builder,
            "Libraries folder",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Libraries"));
        return builder.ToString();
    }

    static void AppendFolder(StringBuilder builder, string label, string folder)
    {
        if (!Directory.Exists(folder))
        {
            builder.AppendLine($"{label} '{folder}': missing");
            return;
        }

        builder.AppendLine($"{label} '{folder}':");
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            var hidden = File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
            builder.AppendLine($"  '{Path.GetFileName(path)}' displayName='{ShellIcons.Describe(path).DisplayName}' hidden={hidden}");
        }
    }
}
