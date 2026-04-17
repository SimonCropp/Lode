namespace Meridian.Services;

public static class ClipboardService
{
    static readonly byte[] moveEffect = [2, 0, 0, 0];
    static readonly byte[] copyEffect = [5, 0, 0, 0];

    internal static void CopyFiles(string[] paths)
    {
        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(paths);
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(copyEffect));
        Clipboard.SetDataObject(data, true);
    }

    internal static void CutFiles(string[] paths)
    {
        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(paths);
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(moveEffect));
        Clipboard.SetDataObject(data, true);
    }

    internal static void PasteFiles(string destination)
    {
        if (!Clipboard.ContainsFileDropList())
        {
            return;
        }

        var files = Clipboard.GetFileDropList();
        var isCut = IsClipboardCut();

        foreach (var file in files)
        {
            if (file == null)
            {
                continue;
            }

            var name = Path.GetFileName(file);
            var dest = Path.Combine(destination, name);

            if (isCut)
            {
                FileSystemService.Move(file, dest);
            }
            else
            {
                FileSystemService.Copy(file, dest);
            }
        }

        if (isCut)
        {
            Clipboard.Clear();
        }
    }

    internal static bool CanPaste() =>
        Clipboard.ContainsFileDropList();

    static bool IsClipboardCut()
    {
        var data = Clipboard.GetDataObject();
        if (data?.GetData("Preferred DropEffect") is MemoryStream stream)
        {
            var bytes = new byte[4];
            var read = stream.Read(bytes, 0, 4);
            return read == 4 && bytes[0] == 2;
        }

        return false;
    }
}
