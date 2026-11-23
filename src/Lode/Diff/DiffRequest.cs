/// <summary>
/// What the Changes or History pane asks to be shown of its files: the one file picked, the
/// files listed beside it, for the diff window's own list, and how many are picked, none or
/// several when there is no one file. <paramref name="Open"/> when asked for outright, by a
/// double-click, Enter, or Show changes, rather than by the file being picked.
/// </summary>
sealed record DiffRequest(string? Repo, DiffSource? Source, IReadOnlyList<Change> Files, GitCommit? Commit, int Picked, bool Open)
{
    /// <summary>
    /// Whether it asks for the same as <paramref name="other"/>: the same file of the same
    /// change, or as many of the same repository's picked.
    /// </summary>
    public bool Same(DiffRequest? other)
    {
        if (other == null ||
            Open ||
            other.Open ||
            Picked != other.Picked ||
            !ChangesPane.SameRepo(Repo, other.Repo) ||
            Commit?.Sha != other.Commit?.Sha)
        {
            return false;
        }

        if (Source == null ||
            other.Source == null)
        {
            return Source == null && other.Source == null;
        }

        return Source.SameFile(other.Source) &&
               Source.File == other.Source.File &&
               Source.Merging == other.Source.Merging;
    }
}
