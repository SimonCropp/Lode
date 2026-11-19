/// <summary>
/// Copy, move, delete and rename through the Windows shell, so the user gets the standard
/// progress dialog, conflict prompts, Recycle Bin and undo.
/// </summary>
static class FileOperations
{
    public record ClipboardFiles(string[] Paths, bool Move);

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
        var flags = undoFlags;
        if (move)
        {
            sources = sources.Except(inPlace).ToList();
        }
        else if (inPlace.Count > 0)
        {
            // Pasting into the folder the files came from makes "name - Copy" duplicates.
            flags |= ShellFileOperation.RenameOnCollision;
        }

        if (sources.Count == 0)
        {
            return Task.FromResult(true);
        }

        var kind = move ? ShellFileOperation.Kind.Move : ShellFileOperation.Kind.Copy;
        return RunInBackground(
            owner,
            kind,
            sources,
            destination,
            flags,
            outcome =>
            {
                if (move)
                {
                    UndoHistory.Instance.RecordMove(Pairs(outcome), Emptied(outcome));
                    return;
                }

                // A folder merged into one already there is not among the changes, so undoing
                // the copy sends to the Recycle Bin what it brought, not what was there before.
                UndoHistory.Instance.RecordCreated("copy", Pairs(outcome).Select(_ => _.To).ToList());
            });
    }

    // The items done, from and to: for a delete, from where to where in the Recycle Bin.
    static List<(string From, string To)> Pairs(ShellFileOperation.Outcome outcome) =>
        outcome.Changes
            .Where(_ => _.Result != null)
            .Select(_ => (_.Source, _.Result!))
            .ToList();

    // The folders a move merged into one already there, which Windows emptied and deleted.
    static List<string> Emptied(ShellFileOperation.Outcome outcome) =>
        outcome.Merged
            .Select(_ => _.Source)
            .ToList();

    // Undoable with Ctrl+Z in Explorer, as SHFileOperation's allow-undo flag was.
    const uint undoFlags = ShellFileOperation.AllowUndo | ShellFileOperation.AddUndoRecord;

    public static Task<bool> Delete(IntPtr owner, IReadOnlyCollection<string> paths, bool permanent)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(true);
        }

        if (permanent)
        {
            return RunInBackground(owner, ShellFileOperation.Kind.Delete, paths, null, 0, _ => { });
        }

        // Only what went to the Recycle Bin can be undone; an item too big for it is deleted
        // outright, and has no place there.
        return RunInBackground(
            owner,
            ShellFileOperation.Kind.Delete,
            paths,
            null,
            undoFlags | ShellFileOperation.RecycleOnDelete,
            outcome => UndoHistory.Instance.RecordDelete(Pairs(outcome)));
    }

    /// <summary>
    /// Renames <paramref name="path"/>, on the calling thread. False, having said why where it
    /// was not there or could not be, when it was not renamed.
    /// </summary>
    public static bool Rename(IntPtr owner, string path, string newName)
    {
        // Not there, it is left out, which the shell counts a success.
        var there = false;
        var succeeded = Report(
            owner,
            ShellFileOperation.Kind.Rename,
            () => ShellFileOperation.Run(owner, ShellFileOperation.Kind.Rename, [path], newName, undoFlags),
            outcome =>
            {
                there = outcome.Missing.Count == 0;

                // Renamed to the name of a folder beside it, a folder merges into that one: what
                // was in it is moved there item by item, and the folder deleted.
                if (outcome.Merged.Count > 0)
                {
                    UndoHistory.Instance.RecordMove(Pairs(outcome), Emptied(outcome), "rename");
                    return;
                }

                if (outcome.Changes is [{ Result: { } renamed }, ..])
                {
                    UndoHistory.Instance.RecordRename(path, renamed);
                }
            });
        return succeeded && there;
    }

    /// <summary>
    /// Pastes what was copied or cut into <paramref name="destination"/>. What was cut is taken
    /// off the clipboard once it has been moved, unless something else has been put there since.
    /// </summary>
    public static async Task Paste(IntPtr owner, ClipboardFiles clipboard, string destination)
    {
        var moved = await Transfer(owner, clipboard.Paths, destination, clipboard.Move);
        if (!clipboard.Move ||
            !moved ||
            ReadClipboard() is not { Move: true } now ||
            !now.Paths.SequenceEqual(clipboard.Paths, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Another process is holding the clipboard open.
        }
    }

    // The shell runs the operation on the calling thread, so a large copy on the UI thread
    // would freeze the window. Each operation gets its own STA thread instead. What could not
    // be done is said there, as the shell's own dialogs are, since nothing waits on the task.
    static Task<bool> RunInBackground(IntPtr owner, ShellFileOperation.Kind kind, IReadOnlyCollection<string> sources, string? destination, uint flags, Action<ShellFileOperation.Outcome> done) =>
        OnStaThread(() => Report(owner, kind, () => ShellFileOperation.Run(owner, kind, sources, destination, flags), done));

    // Runs the operation and says what was not there, and why none of it was done when it
    // could not be. False then.
    static bool Report(IntPtr owner, ShellFileOperation.Kind kind, Func<ShellFileOperation.Outcome> run, Action<ShellFileOperation.Outcome> done)
    {
        ShellFileOperation.Outcome outcome;
        try
        {
            outcome = run();
        }
        catch (Exception exception)
        {
            Warn(owner, exception.Message);
            return false;
        }

        done(outcome);
        if (MissingMessage(kind, outcome.Missing) is { } message)
        {
            Warn(owner, message);
        }

        return outcome.Succeeded;
    }

    /// <summary>
    /// What to say of items that were not there, and so were left out. Null when all were.
    /// </summary>
    internal static string? MissingMessage(ShellFileOperation.Kind kind, IReadOnlyList<string> missing)
    {
        var verb = kind switch
        {
            ShellFileOperation.Kind.Copy => "copied",
            ShellFileOperation.Kind.Move => "moved",
            ShellFileOperation.Kind.Delete => "deleted",
            _ => "renamed"
        };
        switch (missing.Count)
        {
            case 0:
                return null;
            case 1:
                return $"Lode can't find '{NameOf(missing[0])}', so it wasn't {verb}.";
        }

        const int listed = 10;
        var names = missing
            .Take(listed)
            .Select(_ => $"  {NameOf(_)}");
        var message = $"Lode can't find these {missing.Count} items, so they weren't {verb}:\n\n{string.Join("\n", names)}";
        if (missing.Count > listed)
        {
            message += $"\n  and {missing.Count - listed} more";
        }

        return message;
    }

    // A drive has no name of its own, so is its path.
    static string NameOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0)
        {
            return path;
        }

        return name;
    }

    // On whichever thread, owned by the window, as Windows' own dialogs for the operation are.
    static void Warn(IntPtr owner, string message) =>
        MessageBox.Show(new Owner(owner), message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // A window by its handle. Not the control itself, which Windows Forms would refuse to own a
    // dialog from another thread.
    sealed record Owner(IntPtr Handle) : IWin32Window;

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own, set up for the shell's COM objects.
    /// </summary>
    internal static Task<T> OnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            })
        {
            Name = "Lode file operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static string CreateFolder(string parent)
    {
        var path = Path.Combine(parent, "New folder");
        for (var counter = 2; Directory.Exists(path) || File.Exists(path); counter++)
        {
            path = Path.Combine(parent, $"New folder ({counter})");
        }

        Directory.CreateDirectory(path);
        UndoHistory.Instance.RecordCreated("new folder", [path]);
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
        }
        else
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            if (entry.Data != null)
            {
                stream.Write(entry.Data);
            }
        }

        UndoHistory.Instance.RecordCreated("new file", [path]);
        return path;
    }

    /// <summary>
    /// A shortcut to <paramref name="target"/> beside it, named as Explorer names one
    /// ("notes - Shortcut.lnk", then "notes - Shortcut (2).lnk"). Returns its path.
    /// </summary>
    public static string CreateShortcut(string target)
    {
        var parent = Path.GetDirectoryName(target)!;
        var stem = Directory.Exists(target) ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
        var path = Path.Combine(parent, $"{stem} - Shortcut.lnk");
        for (var counter = 2; Directory.Exists(path) || File.Exists(path); counter++)
        {
            path = Path.Combine(parent, $"{stem} - Shortcut ({counter}).lnk");
        }

        ShellLink.Save(path, target);
        UndoHistory.Instance.RecordCreated("create shortcut", [path]);
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

    /// <summary>
    /// Windows Terminal in <paramref name="directory"/>, or a command prompt where it is not
    /// installed. With <paramref name="admin"/>, elevated, after the UAC prompt.
    /// </summary>
    public static void OpenTerminal(string directory, bool admin = false) =>
        OpenTerminal([directory], admin);

    /// <summary>
    /// A tab of Windows Terminal: the folder it opens in, and what it runs there in place of the
    /// default profile's shell, a program then its arguments, when <paramref name="Command"/>
    /// is not empty.
    /// </summary>
    public sealed record TerminalTab(string Directory, IReadOnlyList<string> Command);

    /// <summary>
    /// One Windows Terminal window with a tab in each of <paramref name="directories"/>, so
    /// there is one UAC prompt for the lot with <paramref name="admin"/>; or a command prompt
    /// in each where it is not installed. With <paramref name="command"/>, each tab or prompt
    /// runs it, rather than the shell.
    /// </summary>
    public static void OpenTerminal(IReadOnlyList<string> directories, bool admin = false, string? command = null) =>
        OpenTerminal(
            directories
                .Select(_ => new TerminalTab(_, command == null ? [] : [command]))
                .ToList(),
            admin);

    /// <summary>
    /// One Windows Terminal window with <paramref name="tabs"/>, so there is one UAC prompt for
    /// the lot with <paramref name="admin"/>; or a command prompt for each where it is not
    /// installed, which runs the tab's command.
    /// </summary>
    public static void OpenTerminal(IReadOnlyList<TerminalTab> tabs, bool admin = false) =>
        StartTerminal(tabs, admin);

    /// <summary>
    /// Starts the terminal for <see cref="OpenTerminal(IReadOnlyList{TerminalTab}, bool)"/>.
    /// Tests swap it to see where one would open, without opening it.
    /// </summary>
    internal static Action<IReadOnlyList<TerminalTab>, bool> StartTerminal { get; set; } = Start;

    static void Start(IReadOnlyList<TerminalTab> tabs, bool admin)
    {
        var verb = admin ? "runas" : "";
        try
        {
            var startInfo = new ProcessStartInfo("wt.exe")
            {
                UseShellExecute = true,
                Verb = verb
            };
            foreach (var argument in TerminalArguments(tabs))
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == cancelledByUser)
        {
            // The user said no to the UAC prompt; a prompt that is not elevated would not be
            // what they asked for.
        }
        catch (Exception)
        {
            foreach (var (directory, command) in tabs)
            {
                // An elevated process starts in System32 whatever its working directory, so the
                // prompt changes to the folder itself.
                var fallback = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = true,
                    Verb = verb,
                    WorkingDirectory = directory
                };
                fallback.ArgumentList.Add("/k");
                fallback.ArgumentList.Add("cd");
                fallback.ArgumentList.Add("/d");
                fallback.ArgumentList.Add(directory);
                if (command.Count > 0)
                {
                    fallback.ArgumentList.Add("&&");
                    foreach (var argument in command)
                    {
                        fallback.ArgumentList.Add(argument);
                    }
                }

                try
                {
                    Process.Start(fallback)?.Dispose();
                }
                catch (Win32Exception fallbackException) when (fallbackException.NativeErrorCode == cancelledByUser)
                {
                    // No to one prompt is no to the rest.
                    return;
                }
            }
        }
    }

    /// <summary>
    /// What Windows Terminal is started with: the first tab, then a new tab for each of the
    /// rest, the commands split by semicolons. A tab with a command runs it in place of the
    /// default profile's shell.
    /// </summary>
    internal static List<string> TerminalArguments(IReadOnlyList<TerminalTab> tabs)
    {
        var arguments = new List<string>();
        foreach (var (directory, command) in tabs)
        {
            if (arguments.Count > 0)
            {
                arguments.Add(";");
                arguments.Add("new-tab");
            }

            arguments.Add("-d");
            arguments.Add(EscapeSemicolons(directory));
            arguments.AddRange(command.Select(EscapeSemicolons));
        }

        return arguments;
    }

    // Windows Terminal takes a semicolon anywhere in its arguments, even in a folder's name, as
    // the start of another command, unless a backslash is before it.
    static string EscapeSemicolons(string argument) =>
        argument.Replace(";", @"\;", StringComparison.Ordinal);

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
