/// <summary>
/// A branch's or tag's name as git takes it, made of what was typed as GitHub Desktop makes it:
/// each run of what git refuses in a name, a space say, a dash.
/// </summary>
static partial class RefName
{
    // What git check-ref-format refuses: control characters, space and DEL, ~ ^ : ? * [ \, @{,
    // two dots or more, a dot first or last, .lock last, and / last. And | " < >, which git
    // takes but Windows doesn't, as a branch is a file under .git.
    [GeneratedRegex(@"[\x00-\x20\x7F~^:?*\[\\|""<>]+|@\{|\.\.+|^\.|\.\z|\.lock\z|/\z")]
    private static partial Regex Refused();

    // Git takes a name starting with - or + as an option.
    [GeneratedRegex("^[-+]+")]
    private static partial Regex Leading();

    /// <summary>
    /// <paramref name="typed"/> made a name git takes: "SponsorCheck 0.28.0" SponsorCheck-0.28.0.
    /// Empty where nothing of it would do, "..." say.
    /// </summary>
    public static string Sanitized(string typed) =>
        Leading().Replace(Refused().Replace(typed, "-"), "");
}
