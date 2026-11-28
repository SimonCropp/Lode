/// <summary>
/// What a drive's right-click menu offers, in the tree and in This PC's list, beyond what a
/// folder's does, as Explorer's does: Eject for a removable or optical drive, Disconnect for one
/// mapped to a share, and Rename for a local drive's label. Eject and Disconnect are the shell's
/// own verbs, so Windows does what it does for Explorer: it asks, or refuses while a file on the
/// drive is open, in its own windows. Whatever asks Windows anything is a stand-in, which tests
/// swap: they must never eject, disconnect or rename a real drive.
/// </summary>
sealed partial class DriveCommands
{
    /// <summary>
    /// What kind of drive a root such as <c>E:\</c> is. Finding out asks nothing of the disk,
    /// nor of the network for a mapped drive, so it is asked as a menu opens.
    /// </summary>
    internal Func<string, DriveType> TypeOf = root => new DriveInfo(root).DriveType;

    /// <summary>
    /// Runs one of the shell's verbs on a drive, returning once the shell has done it. Called on
    /// a thread of its own: see <see cref="Eject"/>.
    /// </summary>
    internal Action<IntPtr, string, string> RunVerb = (owner, root, verb) =>
        ShellContextMenu.InvokeVerbAndWait(owner, [root], verb);

    /// <summary>
    /// A drive's label, and the file system it is formatted with, which says what label it can
    /// have. Throws for a drive that isn't ready, a card reader with no card in it say.
    /// </summary>
    internal Func<string, (string Label, string FileSystem)> ReadVolume = root =>
    {
        var drive = new DriveInfo(root);
        return (drive.VolumeLabel, drive.DriveFormat);
    };

    /// <summary>
    /// Gives a drive a label, returning 0, or the Windows error it was refused with.
    /// </summary>
    internal Func<string, string, int> WriteLabel = (root, label) =>
        SetVolumeLabelW(root, label) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// Gives a drive a label as administrator, behind Windows' administrator prompt.
    /// </summary>
    internal Func<string, string, Task<Elevated.Outcome>> WriteLabelAsAdministrator = (root, label) =>
        Elevated.RunPowerShell(resultFile => LabelScript(root, label, resultFile));

    /// <summary>
    /// Asks a yes or no question, answering yes or not.
    /// </summary>
    internal Func<IWin32Window, string, bool> Ask = (owner, text) =>
        MessageBoxes.Show(owner, text, "Lode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>
    /// Says why something couldn't be done.
    /// </summary>
    internal Action<IWin32Window, string> Tell = (owner, text) =>
        MessageBoxes.Show(owner, text, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>
    /// Raised on the UI thread once a drive has a new label, for every window to show it by.
    /// </summary>
    public static event Action? Renamed;

    /// <summary>
    /// Whether <paramref name="path"/> is a drive's root, <c>E:\</c>, as This PC lists it: not a
    /// share, nor a folder on a drive.
    /// </summary>
    public static bool IsDrive(string path) =>
        path is [var letter, ':', '\\'] &&
        char.IsAsciiLetter(letter);

    // The drive's type, or none for what is not a drive.
    DriveType? Type(string path)
    {
        if (!IsDrive(path))
        {
            return null;
        }

        return TypeOf(path);
    }

    public bool CanEject(string path) =>
        Type(path) is DriveType.Removable or DriveType.CDRom;

    public bool CanDisconnect(string path) =>
        Type(path) is DriveType.Network;

    /// <summary>
    /// Whether the drive is one of this machine's own disks, whose label can be changed: not an
    /// optical drive, whose disc's label is written with it, nor one mapped to a share.
    /// </summary>
    public bool CanRename(string path) =>
        Type(path) is DriveType.Fixed or DriveType.Removable;

    /// <summary>
    /// Eject for a removable or optical drive, Disconnect for one mapped to a share, or nothing
    /// for any other, or for what is not a drive.
    /// </summary>
    public IEnumerable<QuickMenu.Command> Commands(IntPtr owner, string path)
    {
        if (CanEject(path))
        {
            yield return new("Eject", () => _ = Eject(owner, path))
            {
                Glyph = Icons.Eject
            };
        }

        if (CanDisconnect(path))
        {
            yield return new("Disconnect", () => _ = Disconnect(owner, path))
            {
                Glyph = Icons.DisconnectDrive
            };
        }
    }

    /// <summary>
    /// The shell's Eject for the drive. It waits for Windows to let go of the drive, which asks
    /// whatever has it open, or says it is in use, so it is run off the UI thread, on a thread of
    /// its own, set up for the shell's COM objects as the UI thread is: every window of Lode
    /// shares the one UI thread, and would wait with it. Completes once the shell is done.
    /// </summary>
    public Task Eject(IntPtr owner, string root) =>
        RunInBackground(owner, root, "eject");

    /// <summary>
    /// The shell's Disconnect for a drive mapped to a share, which asks first should a file on it
    /// be open, and can wait on the network: off the UI thread, as <see cref="Eject"/> is.
    /// </summary>
    public Task Disconnect(IntPtr owner, string root) =>
        RunInBackground(owner, root, "disconnect");

    Task RunInBackground(IntPtr owner, string root, string verb)
    {
        var done = new TaskCompletionSource();
        var run = RunVerb;
        // In the background, so a drive slow to let go doesn't keep Lode from exiting.
        var thread = new Thread(
            () =>
            {
                try
                {
                    run(owner, root, verb);
                    done.SetResult();
                }
                catch (COMException)
                {
                    // The shell says what went wrong itself, in its own window.
                    done.SetResult();
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            })
        {
            IsBackground = true,
            Name = $"Lode drive {verb}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// The drive's label, to edit, or null when it can't be read, having said why.
    /// </summary>
    public string? Label(IWin32Window owner, string root)
    {
        try
        {
            return ReadVolume(root).Label;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            Tell(owner, $"{Letter(root)} can't be renamed. {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gives the drive <paramref name="label"/>, saying why where Windows won't: a label too long
    /// or with characters its file system doesn't allow, said before Windows is asked, or what
    /// Windows refused it for. Changing the label of one of the machine's own disks takes an
    /// administrator, which Windows refuses Lode unless it was run as one: then it asks to do it
    /// as administrator, as Explorer does, behind Windows' prompt. True once the drive has the
    /// label, and <see cref="Renamed"/> has been raised.
    /// </summary>
    public async Task<bool> Rename(IWin32Window owner, string root, string label)
    {
        label = label.Trim();
        string current;
        string fileSystem;
        try
        {
            (current, fileSystem) = ReadVolume(root);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            Tell(owner, $"{Letter(root)} can't be renamed. {exception.Message}");
            return false;
        }

        if (label == current)
        {
            return false;
        }

        if (LabelRule(Letter(root), label, fileSystem) is { } rule)
        {
            Tell(owner, rule);
            return false;
        }

        // Off the UI thread: it writes to the disk, which a drive asleep is slow to answer.
        var write = WriteLabel;
        var error = await Task.Run(() => write(root, label));
        if (error == accessDenied)
        {
            if (!Ask(owner, $"Windows needs administrator permission to rename {Letter(root)}. Rename it as administrator?"))
            {
                return false;
            }

            var outcome = await WriteLabelAsAdministrator(root, label);
            if (outcome.Cancelled)
            {
                return false;
            }

            if (!outcome.Succeeded)
            {
                Tell(owner, $"{Letter(root)} couldn't be renamed. {outcome.Error}");
                return false;
            }
        }
        else if (error != 0)
        {
            Tell(owner, $"{Letter(root)} couldn't be renamed. {new Win32Exception(error).Message}");
            return false;
        }

        NotifyShell(root);
        Renamed?.Invoke();
        return true;
    }

    const int accessDenied = 5;

    static string Letter(string root) =>
        root.TrimEnd('\\');

    // The characters a FAT drive's label can't have, as Windows' label command lists them.
    static SearchValues<char> fatForbidden = SearchValues.Create("*?/\\|.,;:+=[]<>\"");

    /// <summary>
    /// Why a drive formatted with <paramref name="fileSystem"/> can't have
    /// <paramref name="label"/>, or null when it can, as far as Lode can tell before asking
    /// Windows: NTFS and ReFS take up to 32 characters, FAT and exFAT 11, and FAT none of some.
    /// </summary>
    internal static string? LabelRule(string letter, string label, string fileSystem)
    {
        switch (fileSystem.ToUpperInvariant())
        {
            case "NTFS" or "REFS" when label.Length > 32:
                return $"{letter} is formatted as {fileSystem}, so its label can be at most 32 characters long.";
            case "FAT" or "FAT32" when label.Length > 11 || label.AsSpan().ContainsAny(fatForbidden):
                return $"{letter} is formatted as {fileSystem}, so its label can be at most 11 characters long, " +
                       "and can't contain any of these characters:  * ? / \\ | . , ; : + = [ ] < > \"";
            case "EXFAT" when label.Length > 11:
                return $"{letter} is formatted as {fileSystem}, so its label can be at most 11 characters long.";
        }

        return null;
    }

    /// <summary>
    /// The PowerShell that gives the drive the label, run elevated, which writes why it couldn't
    /// to the result file.
    /// </summary>
    internal static string LabelScript(string root, string label, string resultFile) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          $result = {{PowerShell.Quote(resultFile)}}
          try {
              (New-Object System.IO.DriveInfo {{PowerShell.Quote(root)}}).VolumeLabel = {{PowerShell.Quote(label)}}
              exit 0
          }
          catch {
              $exception = $_.Exception
              if ($exception.InnerException) {
                  $exception = $exception.InnerException
              }
              Set-Content -LiteralPath $result -Value ('error: ' + $exception.Message) -Encoding UTF8
              exit 1
          }
          """;

    // SHCNE_UPDATEITEM, with the item named by its path in UTF-16, SHCNF_PATHW.
    const int shellChangeUpdateItem = 0x00002000;
    const uint shellChangePath = 0x0005;

    // Explorer, and the file dialogs, show the drive by its new label too.
    static void NotifyShell(string root)
    {
        var path = Marshal.StringToHGlobalUni(root);
        try
        {
            SHChangeNotify(shellChangeUpdateItem, shellChangePath, path, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVolumeLabelW(string root, string label);
}
