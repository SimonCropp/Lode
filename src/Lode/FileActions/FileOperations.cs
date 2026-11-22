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

    const int rightButtonKeyState = 2;
    const int shiftKeyState = 4;
    const int controlKeyState = 8;
    const int cancelledByUser = 1223;

    public static async Task<bool> Transfer(IntPtr owner, IReadOnlyCollection<string> sources, string destination, bool move) =>
        (await TransferPlacing(owner, sources, destination, move)).Succeeded;

    /// <summary>
    /// As <see cref="Transfer"/>, giving too what is in <paramref name="destination"/> for it:
    /// each item copied or moved there, under the name it was given, a folder merged into one
    /// already there included, and on a move each already there.
    /// </summary>
    static Task<(bool Succeeded, List<string> Placed)> TransferPlacing(IntPtr owner, IReadOnlyCollection<string> sources, string destination, bool move)
    {
        var batches = Batches(sources, destination, move);
        List<string> already = [];
        if (move)
        {
            already = sources
                .Where(_ => Locations.IsChildOf(_, destination))
                .ToList();
        }

        if (batches.Count == 0)
        {
            return Task.FromResult((true, already));
        }

        var kind = ShellFileOperation.Kind.Copy;
        if (move)
        {
            kind = ShellFileOperation.Kind.Move;
        }

        return OnStaThread(
            () =>
            {
                var outcomes = new List<ShellFileOperation.Outcome>();
                var succeeded = true;
                foreach (var (batch, flags) in batches)
                {
                    succeeded &= Report(
                        owner,
                        kind,
                        () => ShellFileOperation.Run(owner, kind, batch, destination, flags),
                        outcomes.Add);
                }

                // One change, for one Ctrl+Z, however many batches it took.
                var pairs = outcomes.SelectMany(Pairs).ToList();
                var placed = Placed(pairs.Select(_ => _.To), destination)
                    .Concat(already)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (move)
                {
                    UndoHistory.Instance.RecordMove(pairs, outcomes.SelectMany(Emptied).ToList());
                    return (succeeded, placed);
                }

                // A folder merged into one already there is not among the changes, so undoing
                // the copy sends to the Recycle Bin what it brought, not what was there before.
                UndoHistory.Instance.RecordCreated("copy", pairs.Select(_ => _.To).ToList());
                return (succeeded, placed);
            });
    }

    // What is directly in the destination for what was done: a folder merged into one there is
    // done item by item, each inside it, so that folder.
    static IEnumerable<string> Placed(IEnumerable<string> done, string destination)
    {
        foreach (var path in done)
        {
            var relative = Path.GetRelativePath(destination, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                continue;
            }

            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            yield return Path.Combine(destination, first);
        }
    }

    /// <summary>
    /// How a paste is done: each batch with its flags. Moving into the folder an item is in
    /// already leaves it out. Copying into it makes a "name - Copy" duplicate, as Explorer does,
    /// in a batch of its own: the rest, from other folders, a search say, keep the shell's prompt
    /// for a clash rather than being renamed too.
    /// </summary>
    internal static List<(List<string> Sources, uint Flags)> Batches(IReadOnlyCollection<string> sources, string destination, bool move)
    {
        var inPlace = sources
            .Where(_ => Locations.IsChildOf(_, destination))
            .ToList();
        var others = sources
            .Except(inPlace)
            .ToList();
        var batches = new List<(List<string>, uint)>();
        if (!move &&
            inPlace.Count > 0)
        {
            batches.Add((inPlace, undoFlags | ShellFileOperation.RenameOnCollision));
        }

        if (others.Count > 0)
        {
            batches.Add((others, undoFlags));
        }

        return batches;
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

                if (outcome.Changes is [{Result: { } renamed}, ..])
                {
                    UndoHistory.Instance.RecordRename(path, renamed);
                }
            });
        return succeeded &&
               there;
    }

    /// <summary>
    /// Pastes what was copied or cut into <paramref name="destination"/>. What was cut is taken
    /// off the clipboard once it has been moved, unless something else has been put there since.
    /// </summary>
    /// <returns>What is in <paramref name="destination"/> for the paste, for it to be selected.</returns>
    public static async Task<IReadOnlyList<string>> Paste(IntPtr owner, ClipboardFiles clipboard, string destination)
    {
        var (moved, placed) = await TransferPlacing(owner, clipboard.Paths, destination, clipboard.Move);
        if (!clipboard.Move ||
            !moved ||
            ReadClipboard() is not { Move: true } now ||
            !now.Paths.SequenceEqual(clipboard.Paths, StringComparer.OrdinalIgnoreCase))
        {
            return placed;
        }

        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Another process is holding the clipboard open.
        }

        return placed;
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
        if (template == null)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            if (entry.Data != null)
            {
                stream.Write(entry.Data);
            }
        }
        else
        {
            File.Copy(template, path);
        }

        UndoHistory.Instance.RecordCreated("new file", [path]);
        return path;
    }

    /// <summary>
    /// A shortcut to <paramref name="target"/> beside it, named as Explorer names one
    /// ("notes - Shortcut.lnk", then "notes - Shortcut (2).lnk"). Returns its path.
    /// </summary>
    public static string CreateShortcut(string target) =>
        CreateShortcuts([target], Path.GetDirectoryName(target)!)[0];

    /// <summary>
    /// A shortcut in <paramref name="folder"/> to each of <paramref name="targets"/>, named as
    /// Explorer names them, as one change to undo. Returns their paths.
    /// </summary>
    public static List<string> CreateShortcuts(IReadOnlyList<string> targets, string folder)
    {
        List<string> created = [];
        try
        {
            foreach (var target in targets)
            {
                var stem = Directory.Exists(target) ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
                if (stem.Length == 0)
                {
                    // A drive: "C - Shortcut.lnk".
                    stem = target.TrimEnd('\\', ':');
                }

                var path = Path.Combine(folder, $"{stem} - Shortcut.lnk");
                for (var counter = 2; Directory.Exists(path) || File.Exists(path); counter++)
                {
                    path = Path.Combine(folder, $"{stem} - Shortcut ({counter}).lnk");
                }

                ShellLink.Save(path, target);
                created.Add(path);
            }
        }
        finally
        {
            if (created.Count > 0)
            {
                UndoHistory.Instance.RecordCreated("create shortcut", created);
            }
        }

        return created;
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
            if (Directory.Exists(target))
            {
                return target;
            }

            return null;
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
                var fallback = new ProcessStartInfo("cmd.exe", PromptArguments(new(directory, command)))
                {
                    UseShellExecute = true,
                    Verb = verb,
                    WorkingDirectory = directory
                };

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
    /// <summary>
    /// What a command prompt is given for <paramref name="tab"/>: to change to its folder, then
    /// run its command. cmd reads the line itself, so the folder and each argument are quoted
    /// where they hold anything cmd would act on, such as the &amp; in R&amp;D, which would
    /// otherwise end the cd there and run the rest as a command.
    /// </summary>
    internal static string PromptArguments(TerminalTab tab)
    {
        var builder = new StringBuilder($"/k cd /d {PromptQuoted(tab.Directory)}");
        if (tab.Command.Count > 0)
        {
            builder.Append(" &&");
            foreach (var argument in tab.Command)
            {
                builder.Append(' ');
                builder.Append(PromptQuoted(argument));
            }
        }

        return builder.ToString();
    }

    // Left as it is only where it is plain: a switch such as /c, say, means nothing to cmd in
    // quotes.
    static string PromptQuoted(string argument)
    {
        if (argument.Length > 0 &&
            argument.All(_ => char.IsAsciiLetterOrDigit(_) || _ is '\\' or ':' or '.' or '-' or '_' or '/'))
        {
            return argument;
        }

        return $"\"{argument}\"";
    }

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

    /// <summary>
    /// Whether a drag is with the right button, which on dropping asks what to do. The button is
    /// up by the drop, so a drop target notes it as the drag goes over.
    /// </summary>
    public static bool RightButton(DragEventArgs args) =>
        (args.KeyState & rightButtonKeyState) != 0;

    /// <summary>
    /// What can be done with what is dragged to <paramref name="destination"/>, of what the
    /// source allows: none of it where any of it is the destination itself, and no move where
    /// all of it is there already.
    /// </summary>
    internal record Possible(string[] Sources, bool Copy, bool Move, bool Link);

    internal static Possible? PossibleDrop(DragEventArgs args, string? destination)
    {
        if (destination == null ||
            args.Data?.GetData(DataFormats.FileDrop) is not string[] {Length: > 0} sources ||
            sources.Any(_ => Locations.IsWithin(destination, _)))
        {
            return null;
        }

        var allowed = args.AllowedEffect;
        return new(
            sources,
            allowed.HasFlag(DragDropEffects.Copy),
            allowed.HasFlag(DragDropEffects.Move) &&
            !sources.All(_ => Locations.IsChildOf(_, destination)),
            allowed.HasFlag(DragDropEffects.Link));
    }

    public static DragDropEffects GetDropEffect(DragEventArgs args, string? destination)
    {
        var effect = DefaultDropEffect(args, destination);
        if (effect != DragDropEffects.None ||
            !RightButton(args) ||
            PossibleDrop(args, destination) is not { } possible)
        {
            return effect;
        }

        // Dragged with the right button, it can go where a left drag couldn't, such as the
        // folder it is in, for the menu to offer a copy or a shortcut there.
        if (possible.Copy)
        {
            return DragDropEffects.Copy;
        }

        if (possible.Link)
        {
            return DragDropEffects.Link;
        }

        return DragDropEffects.None;
    }

    // What a drop with the left button does.
    static DragDropEffects DefaultDropEffect(DragEventArgs args, string? destination)
    {
        if (destination == null ||
            args.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } sources)
        {
            return DragDropEffects.None;
        }

        // Not into itself, nor into a folder inside it, which the shell would refuse.
        if (sources.Any(_ => Locations.IsWithin(destination, _)))
        {
            return DragDropEffects.None;
        }

        // Explorer's rules: Ctrl copies, Shift moves, otherwise move within a drive and copy across drives.
        var control = (args.KeyState & controlKeyState) != 0;
        var shift = (args.KeyState & shiftKeyState) != 0;
        var move = !control &&
                   (shift || Locations.SameRoot(sources[0], destination));
        if (move)
        {
            if (sources.All(_ => Locations.IsChildOf(_, destination)))
            {
                return DragDropEffects.None;
            }

            if (args.AllowedEffect.HasFlag(DragDropEffects.Move))
            {
                return DragDropEffects.Move;
            }
        }

        if (args.AllowedEffect.HasFlag(DragDropEffects.Copy))
        {
            return DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    /// <param name="rightButton">Whether it was dragged with the right button, as noted while
    /// it was dragged over: if so the drop asks what to do, as Explorer does.</param>
    public static void Drop(IntPtr owner, DragEventArgs args, string? destination, bool rightButton = false)
    {
        if (rightButton)
        {
            // Done, if at all, once a choice is made, so the source is told nothing was moved
            // for it to delete.
            args.Effect = DragDropEffects.None;
            if (PossibleDrop(args, destination) is { } possible &&
                DropMenu(owner, possible, destination!, DefaultDropEffect(args, destination)) is { } menu)
            {
                ShowDropMenu(menu);
            }

            return;
        }

        var effect = GetDropEffect(args, destination);
        if (effect == DragDropEffects.None)
        {
            args.Effect = DragDropEffects.None;
            return;
        }

        var sources = (string[])args.Data!.GetData(DataFormats.FileDrop)!;
        _ = Transfer(owner, sources, destination!, effect == DragDropEffects.Move);

        // We move the files ourselves. Reporting Move back would tell the drag source
        // (Explorer, for one) to delete the originals as well.
        if (effect == DragDropEffects.Move)
        {
            args.Effect = DragDropEffects.None;
        }
        else
        {
            args.Effect = effect;
        }
    }

    /// <summary>
    /// What a drop with the right button asks, as Explorer's does: Copy here, Move here and Create
    /// shortcuts here, those that can be done, the one a left drag would do in bold, then Cancel.
    /// Null when none can be.
    /// </summary>
    internal static ExplorerMenu? DropMenu(IntPtr owner, Possible possible, string destination, DragDropEffects effect)
    {
        if (possible is
            {
                Copy: false,
                Move: false,
                Link: false
            })
        {
            return null;
        }

        var menu = new ExplorerMenu();
        void Add(string text, DragDropEffects itemEffect, Action action)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) => action());
            if (itemEffect == effect)
            {
                item.Font = new(item.Font, FontStyle.Bold);
            }

            menu.Items.Add(item);
        }

        var sources = possible.Sources;
        if (possible.Copy)
        {
            Add("Copy here", DragDropEffects.Copy, () => _ = Transfer(owner, sources, destination, false));
        }

        if (possible.Move)
        {
            Add("Move here", DragDropEffects.Move, () => _ = Transfer(owner, sources, destination, true));
        }

        if (possible.Link)
        {
            Add("Create shortcuts here", DragDropEffects.Link, () => CreateShortcutsIn(owner, sources, destination));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Cancel");
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        return menu;
    }

    static void CreateShortcutsIn(IntPtr owner, string[] targets, string folder)
    {
        try
        {
            CreateShortcuts(targets, folder);
        }
        catch (Exception exception)
        {
            MessageBox.Show(Control.FromHandle(owner), exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Shows the menu a drop with the right button asks with, at the pointer, once the drop is
    /// over, so the drag's source isn't held while it is up. Tests swap it, to show nothing.
    /// </summary>
    internal static Action<ExplorerMenu> ShowDropMenu = menu =>
    {
        var at = Cursor.Position;
        if (SynchronizationContext.Current is { } context)
        {
            context.Post(_ => menu.Show(at), null);
            return;
        }

        menu.Show(at);
    };
}
