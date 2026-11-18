/// <summary>
/// What starting Windows PowerShell with a script of Lode's needs, for the update
/// (<see cref="ToolUpdate"/>), the Defender exclusion (<see cref="DefenderExclusion"/>) and
/// running the user's own scripts (<see cref="PowerShellScripts"/>).
/// </summary>
static class PowerShell
{
    /// <summary>
    /// <paramref name="script"/> as <c>-EncodedCommand</c> takes it, base64 of its UTF-16, so no
    /// quoting of it reaches the command line.
    /// </summary>
    public static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// <paramref name="value"/> as a single-quoted string, in which nothing is expanded and a
    /// quote is doubled.
    /// </summary>
    public static string Quote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
