/// <summary>
/// Which of the Changes and History panes shows for a repository, where the window has room for
/// only one of them beside the list, as tabs.
/// </summary>
sealed record PaneTab(string Repo, GitPane Tab);
