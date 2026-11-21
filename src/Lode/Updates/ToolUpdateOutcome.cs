/// <summary>
/// How the last <see cref="ToolUpdate"/> went, for the Lode it started again to say. The update
/// runs in a shell nobody sees, after Lode has exited, so without this there is nothing to see at
/// either end of it: Lode goes away and comes back, and whether that is the new version or the old
/// one after a failure looks exactly the same.
/// <para>
/// Vendored from PlatformHeads.Core's ToolUpdateOutcome, saying it as an <see cref="UpdateNotice"/>
/// for the tray rather than PlatformHeads' tray notification.
/// </para>
/// </summary>
/// <param name="Succeeded">Whether <c>dotnet tool update</c> exited with success.</param>
/// <param name="Output">Everything it wrote.</param>
sealed record ToolUpdateOutcome(bool Succeeded, string Output)
{
    /// <summary>
    /// The first line the shell writes, which says which of the two the rest of the file is.
    /// </summary>
    public const string SucceededLine = "ok";

    public const string FailedLine = "failed";

    /// <summary>
    /// The outcome <paramref name="path"/> holds, or null where there is none, which is every
    /// start that did not follow an update. Removed once read, so an outcome is said once rather
    /// than at every start. Throws what reading or deleting the file throws.
    /// </summary>
    public static ToolUpdateOutcome? Take(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        File.Delete(path);
        return Parse(text);
    }

    /// <summary>
    /// The notice about this outcome. A success says the version rather than what the tool said,
    /// because the Lode showing it is the one the update started: it is the installed one, whether
    /// the update moved it or found it already current. And it is not shown as a warning, which
    /// would make an update that worked look like one that had not. A failure says the
    /// <see cref="Reason"/>.
    /// </summary>
    /// <param name="version">The version running, which is the installed one.</param>
    public UpdateNotice Notice(string version)
    {
        if (Succeeded)
        {
            return new("Lode updated", $"Now running {version}.", ToolTipIcon.Info);
        }

        return new("Lode did not update", Reason, ToolTipIcon.Warning);
    }

    /// <summary>
    /// A file with no status line at all is a failure, the whole of it being the output.
    /// </summary>
    public static ToolUpdateOutcome Parse(string text)
    {
        var lines = text.Split('\n');
        var first = lines[0].Trim();
        if (first == SucceededLine)
        {
            return new(true, string.Join('\n', lines.Skip(1)));
        }

        if (first == FailedLine)
        {
            return new(false, string.Join('\n', lines.Skip(1)));
        }

        return new(false, text);
    }

    /// <summary>
    /// The last line <c>dotnet tool update</c> wrote, which is where it puts the cause, under a
    /// first line that only says the update failed.
    /// </summary>
    public string Reason
    {
        get
        {
            var last = Output
                .Split('\n')
                .Select(_ => _.Trim())
                .LastOrDefault(_ => _.Length > 0);
            return last ?? "dotnet tool update failed without saying why.";
        }
    }
}

/// <summary>
/// What the tray icon says after an update.
/// </summary>
sealed record UpdateNotice(string Title, string Message, ToolTipIcon Icon);
