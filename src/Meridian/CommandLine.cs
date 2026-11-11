enum CommandKind
{
    Open,
    ReplaceExplorer,
    RestoreExplorer,
    Help
}

/// <summary>
/// What Meridian was started to do. Arguments that are not switches are folders or files to open.
/// </summary>
sealed record CommandLine(CommandKind Kind, IReadOnlyList<string> Paths, string? Error = null)
{
    public const string Usage =
        """
        Usage: meridian [folder or file ...]
               meridian --replace-explorer
               meridian --restore-explorer

          folder or file      Open each in a tab. A file opens its folder with the file selected.
                              With none, the tabs from last time are reopened.
          --replace-explorer  Open folders, drives and Win+E in Meridian instead of File Explorer,
                              for the current Windows account.
          --restore-explorer  Undo --replace-explorer, so File Explorer opens them again.
          --help              Show this help.
        """;

    public static CommandLine Parse(IReadOnlyList<string> arguments)
    {
        var switches = arguments.Where(IsSwitch).ToList();
        if (switches.Count == 0)
        {
            return new(CommandKind.Open, arguments);
        }

        if (arguments.Count > 1)
        {
            return new(CommandKind.Help, [], "Pass one switch on its own, without folders or files.");
        }

        return switches[0].ToLowerInvariant() switch
        {
            "--replace-explorer" => new(CommandKind.ReplaceExplorer, []),
            "--restore-explorer" => new(CommandKind.RestoreExplorer, []),
            "--help" or "-h" or "-?" or "/?" => new(CommandKind.Help, []),
            var unknown => new(CommandKind.Help, [], $"Unknown option: {unknown}")
        };
    }

    // "/?" is the Windows way of asking for help. Other arguments that start with a slash are
    // left as paths, since "/" can begin one.
    static bool IsSwitch(string argument) =>
        argument.StartsWith('-') || argument == "/?";
}
