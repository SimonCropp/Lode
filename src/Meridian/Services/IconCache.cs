using System.Collections.Concurrent;
using System.Windows.Media;
using Meridian.Interop;

namespace Meridian.Services;

class IconCache
{
    readonly ConcurrentDictionary<string, ImageSource?> smallIcons = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ImageSource?> largeIcons = new(StringComparer.OrdinalIgnoreCase);

    static readonly ImageSource? folderSmall = ShellApi.GetIcon("folder", true, true);
    static readonly ImageSource? folderLarge = ShellApi.GetIcon("folder", true, false);

    internal ImageSource? GetSmallIcon(string path, bool isDirectory) =>
        isDirectory ? folderSmall : smallIcons.GetOrAdd(GetKey(path), _ => ShellApi.GetIcon(path, false, true));

    internal ImageSource? GetLargeIcon(string path, bool isDirectory) =>
        isDirectory ? folderLarge : largeIcons.GetOrAdd(GetKey(path), _ => ShellApi.GetIcon(path, false, false));

    static string GetKey(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? path : ext;
    }
}
