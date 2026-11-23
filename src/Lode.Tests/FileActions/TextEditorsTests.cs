/// <summary>
/// Finding editors from a described registry and file system, and which files offer Edit.
/// </summary>
public class TextEditorsTests
{
    const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Test]
    public Task NothingInstalledLeavesNotepad() =>
        Verify(Find([], []));

    [Test]
    public Task FindsEditorsFromTheirUninstallKeys() =>
        Verify(
            Find(
                [
                    // VS Code, per user: the executable is in InstallLocation.
                    (RegistryHive.CurrentUser, $@"{uninstall}\{{771FD6B0-FA20-440A-A002-3B3BAC16DC50}}_is1",
                        new()
                        {
                            ["DisplayName"] = "Microsoft Visual Studio Code (User)",
                            ["Publisher"] = "Microsoft Corporation",
                            ["InstallLocation"] = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\"
                        }),
                    // Notepad++: the executable is DisplayIcon, quoted and with an icon index.
                    (RegistryHive.LocalMachine, $@"{uninstall}\Notepad++",
                        new()
                        {
                            ["DisplayName"] = "Notepad++ (64-bit x64)",
                            ["Publisher"] = "Notepad++ Team",
                            ["DisplayIcon"] = "\"C:\\Program Files\\Notepad++\\notepad++.exe\",0"
                        })
                ],
                [
                    @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe",
                    @"C:\Program Files\Notepad++\notepad++.exe"
                ]));

    // Another product reusing a key, or a key left behind after uninstalling, is not the editor.
    [Test]
    public Task SkipsKeysThatDoNotMatchOrPointNowhere() =>
        Verify(
            Find(
                [
                    (RegistryHive.CurrentUser, $@"{uninstall}\{{771FD6B0-FA20-440A-A002-3B3BAC16DC50}}_is1",
                        new()
                        {
                            ["DisplayName"] = "Microsoft Visual Studio Code (User)",
                            ["Publisher"] = "Someone Else",
                            ["InstallLocation"] = @"C:\Code"
                        }),
                    (RegistryHive.LocalMachine, $@"{uninstall}\Sublime Text_is1",
                        new()
                        {
                            ["DisplayName"] = "Sublime Text",
                            ["Publisher"] = "Sublime HQ Pty Ltd",
                            ["InstallLocation"] = @"C:\Program Files\Sublime Text"
                        })
                ],
                [@"C:\Code\Code.exe"]));

    [Test]
    public Task WhichFilesAreText()
    {
        string[] paths =
        [
            @"C:\notes.txt",
            @"C:\Program.CS",
            @"C:\settings.json",
            @"C:\photo.jpg",
            @"C:\setup.exe",
            @"C:\Makefile",
            @"C:\readme.custom"
        ];

        // Windows knows .custom as text; this machine knows nothing else.
        return Verify(
            paths.ToDictionary(
                _ => _,
                _ => TextEditors.IsText(_, extension => extension == ".custom" ? "text" : null)));
    }

    static List<string> Find(
        (RegistryHive Hive, string SubKey, Dictionary<string, string> Values)[] keys,
        string[] files) =>
        TextEditors
            .Find(
                (hive, subKey) => keys
                    .Where(_ => _.Hive == hive && _.SubKey == subKey)
                    .Select(_ => _.Values)
                    .FirstOrDefault(),
                path => files.Contains(path))
            // Notepad's path depends on where this machine keeps Windows.
            .Select(_ => _ == TextEditors.Notepad ? "Notepad" : $"{_.Name}: {_.Path}")
            .ToList();

    // Find in Files' results open each editor at the line, as it is told to.
    [Test]
    public Task OpeningAtALine() =>
        Verify(
            new[]
                {
                    @"C:\Programs\VS Code\Code.exe",
                    @"C:\Programs\VS Code Insiders\Code - Insiders.exe",
                    @"C:\Programs\Cursor\Cursor.exe",
                    @"C:\Program Files\Sublime Text\subl.exe",
                    @"C:\Programs\Zed\Zed.exe",
                    @"C:\Program Files\Notepad++\notepad++.exe",
                    @"C:\Windows\notepad.exe",
                    @"C:\Tools\Other.exe"
                }
                .ToDictionary(_ => Path.GetFileName(_), _ => string.Join(" ", TextEditors.AtLine(_, @"C:\Code\App.cs", 12, 5))));

    // VS Code and those made from it show two files' changes side by side; the rest can't.
    [Test]
    public async Task Diff()
    {
        await Assert.That(TextEditors.Diff(@"C:\Programs\VS Code\Code.exe", "a.txt", "b.txt")).IsEquivalentTo(["--diff", "a.txt", "b.txt"]);
        await Assert.That(TextEditors.Diff(@"C:\Programs\Cursor\Cursor.exe", "a.txt", "b.txt")).IsNotNull();
        await Assert.That(TextEditors.Diff(@"C:\Windows\notepad.exe", "a.txt", "b.txt")).IsNull();
        await Assert.That(TextEditors.Diff(@"C:\Program Files\Sublime Text\subl.exe", "a.txt", "b.txt")).IsNull();
    }
}