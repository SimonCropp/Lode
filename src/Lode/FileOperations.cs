/// <summary>
/// Copy, move, delete and rename through the Windows shell, so the user gets the standard
/// progress dialog, conflict prompts, Recycle Bin and undo.
/// </summary>
static class FileOperations
{
    public record ClipboardFiles(string[] Paths, bool Move);

    const uint moveFunction = 1;
    const uint copyFunction = 2;
    const uint deleteFunction = 3;
    const uint renameFunction = 4;

    const ushort renameOnCollision = 0x8;
    const ushort allowUndo = 0x40;

    const string dropEffectFormat = "Preferred DropEffect";
    const int dropEffectMove = 2;
    const int dropEffectCopyOrLink = 5;

    const int shiftKeyState = 4;
    const int controlKeyState = 8;
    const int cancelledByUser = 1223;

    public static Task<bool> Transfer(IntPtr owner, IReadOnlyCollection<string> sources, string destination, bool move)
    {
        var inPlace = sources
            .Where(_ => Locations.IsChildOf(_, destination))
            .ToList();
        var flags = allowUndo;
        if (move)
        {
            sources = sources.Except(inPlace).ToList();
        }
        else if (inPlace.Count > 0)
        {
            // Pasting into the folder the files came from makes "name - Copy" duplicates.
            flags |= renameOnCollision;
        }

        if (sources.Count == 0)
        {
            return Task.FromResult(true);
        }

        return RunInBackground(owner, move ? moveFunction : copyFunction, sources, destination, flags);
    }

    public static Task<bool> Delete(IntPtr owner, IReadOnlyCollection<string> paths, bool permanent)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(true);
        }

        return RunInBackground(owner, deleteFunction, paths, null, permanent ? (ushort)0 : allowUndo);
    }

    public static bool Rename(IntPtr owner, string path, string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        return Execute(owner, renameFunction, [path], target, allowUndo);
    }

    // The shell runs the operation on the calling thread, so a large copy on the UI thread
    // would freeze the window. Each operation gets its own STA thread instead.
    static Task<bool> RunInBackground(IntPtr owner, uint function, IReadOnlyCollection<string> sources, string? destination, ushort flags)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => completion.SetResult(Execute(owner, function, sources, destination, flags)))
        {
            Name = "Lode file operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    static bool Execute(IntPtr owner, uint function, IReadOnlyCollection<string> sources, string? destination, ushort flags)
    {
        // Path lists are null separated and double-null terminated.
        var from = Marshal.StringToHGlobalUni(string.Join('\0', sources) + "\0\0");
        var to = destination == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(destination + "\0\0");
        try
        {
            var info = new FileOperationInfo
            {
                Window = owner,
                Function = function,
                From = from,
                To = to,
                Flags = flags
            };
            return SHFileOperation(ref info) == 0 && info.AnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
            Marshal.FreeHGlobal(to);
        }
    }

    public static string CreateFolder(string parent)
    {
        var path = Path.Combine(parent, "New folder");
        for (var counter = 2; Directory.Exists(path) || File.Exists(path); counter++)
        {
            path = Path.Combine(parent, $"New folder ({counter})");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A new file of <paramref name="entry"/>'s type in <paramref name="parent"/>, named as
    /// Explorer names one ("New Text Document.txt", then "New Text Document (2).txt"). Returns its path.
    /// </summary>
    public static string CreateFile(string parent, ShellNew.Entry entry)
    {
        var stem = $"New {entry.TypeName}";
        var path = Path.Combine(parent, stem + entry.Extension);
        for (var counter = 2; Directory.Exists(path) || File.Exists(path); counter++)
        {
            path = Path.Combine(parent, $"{stem} ({counter}){entry.Extension}");
        }

        var template = entry.Template == null ? null : ShellNew.ResolveTemplate(entry.Template);
        if (template != null)
        {
            File.Copy(template, path);
            return path;
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        if (entry.Data != null)
        {
            stream.Write(entry.Data);
        }

        return path;
    }

    public static void Open(IWin32Window owner, string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            };
            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == cancelledByUser)
        {
            // The user dismissed an elevation prompt.
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Returns the folder a .lnk shortcut points at, or null when it is not a folder shortcut.
    /// </summary>
    public static string? ResolveFolderShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            string target = shell.CreateShortcut(path).TargetPath;
            return Directory.Exists(target) ? target : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void OpenTerminal(string directory)
    {
        try
        {
            var startInfo = new ProcessStartInfo("wt.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception)
        {
            var fallback = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = directory
            };
            Process.Start(fallback)?.Dispose();
        }
    }

    public static void CopyToClipboard(IReadOnlyCollection<string> paths, bool cut)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData(dropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? dropEffectMove : dropEffectCopyOrLink)));
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (ExternalException)
        {
            // Another process is holding the clipboard open.
        }
    }

    public static ClipboardFiles? ReadClipboard()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return null;
            }

            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length == 0)
            {
                return null;
            }

            var move = false;
            if (Clipboard.GetDataObject()?.GetData(dropEffectFormat) is MemoryStream stream)
            {
                var buffer = new byte[4];
                if (stream.Read(buffer, 0, 4) == 4)
                {
                    move = (BitConverter.ToInt32(buffer) & dropEffectMove) != 0;
                }
            }

            return new(paths, move);
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    public static DragDropEffects GetDropEffect(DragEventArgs eventArgs, string? destination)
    {
        if (destination == null ||
            eventArgs.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } sources)
        {
            return DragDropEffects.None;
        }

        if (sources.Any(_ => Locations.Same(_, destination)))
        {
            return DragDropEffects.None;
        }

        // Explorer's rules: Ctrl copies, Shift moves, otherwise move within a drive and copy across drives.
        var control = (eventArgs.KeyState & controlKeyState) != 0;
        var shift = (eventArgs.KeyState & shiftKeyState) != 0;
        var move = !control &&
                   (shift || Locations.SameRoot(sources[0], destination));
        if (move)
        {
            if (sources.All(_ => Locations.IsChildOf(_, destination)))
            {
                return DragDropEffects.None;
            }

            if (eventArgs.AllowedEffect.HasFlag(DragDropEffects.Move))
            {
                return DragDropEffects.Move;
            }
        }

        if (eventArgs.AllowedEffect.HasFlag(DragDropEffects.Copy))
        {
            return DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    public static void Drop(IntPtr owner, DragEventArgs eventArgs, string? destination)
    {
        var effect = GetDropEffect(eventArgs, destination);
        if (effect == DragDropEffects.None)
        {
            eventArgs.Effect = DragDropEffects.None;
            return;
        }

        var sources = (string[])eventArgs.Data!.GetData(DataFormats.FileDrop)!;
        Transfer(owner, sources, destination!, effect == DragDropEffects.Move);

        // We move the files ourselves. Reporting Move back would tell the drag source
        // (Explorer, for one) to delete the originals as well.
        if (effect == DragDropEffects.Move)
        {
            eventArgs.Effect = DragDropEffects.None;
        }
        else
        {
            eventArgs.Effect = effect;
        }
    }
}
