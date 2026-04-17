using Meridian.Models;

namespace Meridian.Services;

public class FileSystemService
{
    public static IReadOnlyList<FileItem> GetDirectoryContents(string path)
    {
        var directory = new DirectoryInfo(path);
        var items = new List<FileItem>();

        foreach (var dir in directory.EnumerateDirectories())
        {
            if ((dir.Attributes & FileAttributes.Hidden) != 0 ||
                (dir.Attributes & FileAttributes.System) != 0)
            {
                continue;
            }

            items.Add(new(
                dir.Name,
                dir.FullName,
                true,
                0,
                dir.LastWriteTime,
                ""));
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var files = new List<FileItem>();

        foreach (var file in directory.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.Hidden) != 0 ||
                (file.Attributes & FileAttributes.System) != 0)
            {
                continue;
            }

            files.Add(new(
                file.Name,
                file.FullName,
                false,
                file.Length,
                file.LastWriteTime,
                file.Extension));
        }

        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        items.AddRange(files);

        return items;
    }

    public static IReadOnlyList<DriveInfo> GetDrives() =>
        DriveInfo.GetDrives()
            .Where(_ => _.IsReady)
            .ToList();

    public static void Copy(string source, string destination)
    {
        if (File.GetAttributes(source).HasFlag(FileAttributes.Directory))
        {
            CopyDirectory(source, destination);
        }
        else
        {
            File.Copy(source, destination, true);
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    public static void Move(string source, string destination)
    {
        if (File.GetAttributes(source).HasFlag(FileAttributes.Directory))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, true);
        }
    }

    public static void Delete(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, true);
        }
        else
        {
            File.Delete(path);
        }
    }

    public static void Rename(string path, string newName)
    {
        var parent = Path.GetDirectoryName(path)!;
        var destination = Path.Combine(parent, newName);
        Move(path, destination);
    }

    public static void CreateDirectory(string path) =>
        Directory.CreateDirectory(path);

    public static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path);
}
