/// <summary>
/// Run, on the right-click menu of PowerShell scripts: each runs in a Windows Terminal tab of
/// its own, in its folder, by PowerShell 7 where it is installed, otherwise by Windows
/// PowerShell. The tab stays at a prompt once the script ends, so what it wrote can be read.
/// </summary>
static class PowerShellScripts
{
    static string windowsPowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>
    /// Whether every one of <paramref name="paths"/> is a script Run applies to.
    /// </summary>
    public static bool AreScripts(IReadOnlyList<string> paths) =>
        paths.All(_ => Path.GetExtension(_.AsSpan()).Equals(".ps1", StringComparison.OrdinalIgnoreCase));

    public static void Run(IReadOnlyList<string> paths) =>
        FileOperations.OpenTerminal(Tabs(paths, Shell()));

    /// <summary>
    /// PowerShell 7 as Windows would find <c>pwsh</c>, so the copy the PATH has is used whether
    /// it came from its installer, the Store or a dotnet tool; otherwise Windows PowerShell,
    /// which every Windows has. Looked for when a script is run rather than when the menu is
    /// built: the actions bar builds it each time the selection changes.
    /// </summary>
    static string Shell() =>
        SearchPath.Folders()
            .Select(_ => Path.Combine(_, "pwsh.exe"))
            .FirstOrDefault(File.Exists) ?? windowsPowerShell;

    /// <summary>
    /// A tab in each script's folder running it by <paramref name="shell"/>, left at a prompt
    /// after (-NoExit). The script is invoked by a command, which -File could not put the
    /// execution policy before, encoded, so the path in it is quoted only as PowerShell
    /// quotes, not again for Windows Terminal's command line and PowerShell's.
    /// </summary>
    internal static List<FileOperations.TerminalTab> Tabs(IReadOnlyList<string> paths, string shell) =>
        paths
            .Select(_ => new FileOperations.TerminalTab(
                Path.GetDirectoryName(_)!,
                [shell, "-NoExit", "-EncodedCommand", PowerShell.Encode(Command(_))]))
            .ToList();

    /// <summary>
    /// What runs the script, as Explorer's Run with PowerShell does: unless the execution policy
    /// is AllSigned, it is bypassed for this session alone, so a script is not refused for being
    /// unsigned or downloaded. One set by Group Policy still stands, and the script is refused
    /// as PowerShell would refuse it.
    /// </summary>
    internal static string Command(string path) =>
        $$"""
          if ((Get-ExecutionPolicy) -ne 'AllSigned') { try { Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction Stop } catch { } }
          & {{PowerShell.Quote(path)}}
          """;
}
