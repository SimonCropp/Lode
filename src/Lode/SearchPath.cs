/// <summary>
/// The folders Windows looks in for a command, for the programs Lode starts by name: the coding
/// agents (<see cref="CodingAgents"/>) and PowerShell 7 (<see cref="PowerShellScripts"/>).
/// </summary>
static class SearchPath
{
    /// <summary>
    /// The PATH as Windows has it now, the machine's then the user's, rather than as it was when
    /// Lode started, so a program installed since is found; then Lode's own.
    /// </summary>
    public static List<string> Folders()
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

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
