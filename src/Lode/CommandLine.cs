/// <summary>
/// What Lode was started to do. Arguments that are not switches are folders or files to open,
/// apart from virtual folders Lode cannot show.
/// </summary>
sealed record CommandLine(CommandKind Kind, IReadOnlyList<string> Paths, string? Error = null)
{
    /// <summary>
    /// Virtual folders, Control Panel or the Recycle Bin say, named as the shell names them
    /// ("::{CLSID}" and what follows), which open in File Explorer. Replace takes the explore
    /// verb on Folder, which apps also ask for these, so they reach Lode beside real folders.
    /// This PC is not one of them: Lode shows it.
    /// </summary>
    public IReadOnlyList<string> ForExplorer { get; init; } = [];

    /// <summary>
    /// <see cref="Paths"/> as full paths. A relative one is from the folder this launch was
    /// started in, which only this launch knows: a copy of Lode already running, which the
    /// paths are handed to, was started somewhere else.
    /// </summary>
    public IReadOnlyList<string> FullPaths() =>
        Paths
            .Select(_ => Locations.Normalize(_) ?? _)
            .ToList();

    public const string Usage =
        """
        Usage: lode [folder or file ...]
               lode --replace-explorer
               lode --restore-explorer

          folder or file      Open each in a tab. A file opens its folder with the file selected.
                              With none, the tabs from last time are reopened.
          --replace-explorer  Open folders, drives and Win+E in Lode instead of File Explorer,
                              for the current Windows account.
          --restore-explorer  Undo --replace-explorer, so File Explorer opens them again.
          --help              Show this help.
        """;

    public static CommandLine Parse(IReadOnlyList<string> arguments)
    {
        var switches = arguments.Where(IsSwitch).ToList();
        if (switches.Count == 0)
        {
            return new(CommandKind.Open, arguments.Where(_ => !Locations.IsVirtual(_)).ToList())
            {
                ForExplorer = arguments.Where(Locations.IsVirtual).ToList()
            };
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
