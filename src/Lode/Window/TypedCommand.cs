/// <summary>
/// What is typed in the address bar as a command rather than a path, as Explorer runs one:
/// `cmd`, `powershell`, `wt` or `code .`, a program and what it is given.
/// </summary>
static class TypedCommand
{
    public sealed record Command(string Program, string Arguments);

    /// <summary>
    /// The program and its arguments, the program in quotes where it has spaces. Null for what
    /// names somewhere: a program with a folder or drive in it, or a URL, is a path.
    /// </summary>
    internal static Command? Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string program;
        string rest;
        if (trimmed[0] == '"')
        {
            var close = trimmed.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = trimmed[1..close];
            rest = trimmed[(close + 1)..];
        }
        else
        {
            var space = trimmed.IndexOfAny([' ', '\t']);
            if (space < 0)
            {
                program = trimmed;
                rest = "";
            }
            else
            {
                program = trimmed[..space];
                rest = trimmed[space..];
            }
        }

        if (program.Length == 0 ||
            program.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return null;
        }

        return new(program, rest.Trim());
    }
}
