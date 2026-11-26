/// <summary>
/// How a folder is sorted: by which of the file list's columns, by its key, and which way.
/// </summary>
sealed record FolderSort(string Folder, string Column, bool Ascending);