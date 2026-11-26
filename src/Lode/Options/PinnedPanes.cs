/// <summary>
/// Which of the Changes and History panes are pinned for a repository.
/// </summary>
sealed record PinnedPanes(string Repo, bool Changes, bool History);