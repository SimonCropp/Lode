/// <summary>
/// What Find in Files looks for, where, and how, as the find strip has it.
/// </summary>
/// <param name="Find">The text, or with <paramref name="Regex"/> the pattern.</param>
/// <param name="Where">The folders and files to look in, and the patterns that narrow them:
/// see <see cref="SearchWhere"/>.</param>
/// <param name="ContextLines">How many lines to show either side of each that matches.</param>
/// <param name="Hidden">Whether hidden files and folders are searched.</param>
/// <param name="IncludeIgnored">Whether what git ignores is searched too.</param>
sealed record FindQuery(
    string Find,
    string Where,
    bool Regex = false,
    bool CaseSensitive = false,
    bool WholeWord = false,
    int ContextLines = 2,
    bool Hidden = true,
    bool IncludeIgnored = false)
{
    /// <summary>
    /// How it is looked for, in brackets after it, as Sublime says: "(regex, case sensitive)".
    /// Nothing when it is plain text, any case.
    /// </summary>
    public string Describe()
    {
        var how = new List<string>();
        if (Regex)
        {
            how.Add("regex");
        }

        if (CaseSensitive)
        {
            how.Add("case sensitive");
        }

        if (WholeWord)
        {
            how.Add("whole word");
        }

        if (!Hidden)
        {
            how.Add("no hidden files");
        }

        if (IncludeIgnored)
        {
            how.Add("ignored by git too");
        }

        if (how.Count == 0)
        {
            return "";
        }

        return $" ({string.Join(", ", how)})";
    }
}
