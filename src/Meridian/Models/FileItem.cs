namespace Meridian.Models;

record FileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime DateModified,
    string Extension);
