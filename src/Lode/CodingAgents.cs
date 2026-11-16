/// <summary>
/// The coding agents that run in a terminal (Claude Code, GitHub Copilot CLI, Codex CLI and
/// Gemini CLI), found as Windows finds a command, and each started in Windows Terminal in a
/// folder, as Terminal opens one there. Without any, Coding agent is not offered.
/// </summary>
static class CodingAgents
{
    /// <param name="Path">The program, or the .cmd file npm installs in its place.</param>
    public sealed record Agent(string Name, string Path);

    // Each agent's name, and the command it is run as, which is the file it is found by.
    static readonly (string Name, string Command)[] known =
    [
        ("Claude Code", "claude"),
        ("GitHub Copilot CLI", "copilot"),
        ("Codex CLI", "codex"),
        ("Gemini CLI", "gemini")
    ];

    // As Windows tries a command's extensions. npm puts a .cmd file on the PATH for each tool it
    // installs, beside a script without an extension that only other shells can run.
    static readonly string[] extensions = [".exe", ".cmd", ".bat"];

    /// <summary>
    /// The agents installed, in the order of <see cref="known"/>.
    /// </summary>
    internal static Func<IReadOnlyList<Agent>> Installed { get; set; } = FindCached;

    // Looking checks every folder on the PATH for every agent, a few milliseconds, and the
    // actions bar asks each time the selection changes, so an answer stands for half a minute.
    const long answerLifetime = 30_000;
    static (long At, IReadOnlyList<Agent> Agents)? answer;

    static IReadOnlyList<Agent> FindCached()
    {
        var now = Environment.TickCount64;
        if (answer is { } last &&
            now - last.At < answerLifetime)
        {
            return last.Agents;
        }

        var agents = Find(SearchFolders());
        answer = (now, agents);
        return agents;
    }

    /// <summary>
    /// The agents in <paramref name="folders"/>: for each, the first folder holding its command
    /// with one of the extensions, as Windows would run it.
    /// </summary>
    internal static List<Agent> Find(IReadOnlyList<string> folders)
    {
        var found = new List<Agent>();
        foreach (var (name, command) in known)
        {
            if (Locate(command, folders) is { } path)
            {
                found.Add(new(name, path));
            }
        }

        return found;
    }

    static string? Locate(string command, IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            foreach (var extension in extensions)
            {
                var path = Path.Combine(folder, command + extension);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where to look: the PATH as Windows has it now, the machine's then the user's, rather than
    /// as it was when Lode started, so an agent installed since is found; then Lode's own; then
    /// the folder Claude Code's own installer puts it in.
    /// </summary>
    static List<string> SearchFolders()
    {
        var folders = new List<string>();
        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Machine,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Process
                 })
        {
            var value = Environment.GetEnvironmentVariable("Path", target) ?? "";
            foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var folder = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
                if (Path.IsPathFullyQualified(folder))
                {
                    folders.Add(folder);
                }
            }
        }

        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Coding agent, opening a list of the agents installed, or nothing without one or a folder.
    /// Each agent starts in a Windows Terminal tab of its own in each of
    /// <paramref name="folders"/>.
    /// </summary>
    public static List<QuickMenu.Command?> Commands(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return [];
        }

        var agents = Installed();
        if (agents.Count == 0)
        {
            return [];
        }

        // Each with its program's icon, as Open with shows apps, read only when the list opens:
        // the actions bar asks for these commands each time the selection changes.
        return
        [
            LodeActions.Submenu(
                "agent",
                QuickMenu.Deferred(() =>
                    agents
                        .Select(QuickMenu.Command? (_) =>
                            new(_.Name, () => Open(_, folders))
                            {
                                ShellIcon = ShellIcons.GetActualIcon(_.Path)
                            })
                        .ToList()))
        ];
    }

    public static void Open(Agent agent, IReadOnlyList<string> folders) =>
        FileOperations.OpenTerminal(folders, command: agent.Path);
}
