using IEnumString = System.Runtime.InteropServices.ComTypes.IEnumString;

/// <summary>
/// What the address bar suggests as a path is typed into it, in Windows' own list under the box:
/// the folders in the folder the text so far is in, taken from the folder open as Enter takes
/// it, so `src\Lo` offers `src\Lode`, and `..\` the folders beside the folder open; the folders
/// in a folder given in full; hidden ones, and Windows' own, as the options show them; and,
/// once the text starts with `shell:`, the shell's names for its folders, `shell:Downloads` say.
/// Windows' list for the file system, which the box had, knew nothing of the folder open and
/// worked from Lode's own, System32, and knew no shell names.
/// </summary>
/// <remarks>
/// Windows asks for the suggestions on a thread of its own, the folder the text is in when a
/// separator is typed, then each suggestion, which it narrows to those starting with the text
/// itself as more is typed. So the folders are read on that thread, and a share or a drive slow
/// to answer holds up the list, never the typing.
/// </remarks>
sealed class AddressCompletion :
    IEnumString,
    AddressCompletion.IACList
{
    // ACO_AUTOSUGGEST: the list drops down under the box as it is typed in, as the box's
    // AutoCompleteMode.Suggest had it.
    const int autoSuggest = 0x1;

    // ACDD_VISIBLE: the list is showing.
    const int dropDownVisible = 0x1;

    const string shellPrefix = "shell:";

    // Where Windows describes its known folders, in the user's hive and the machine's, each
    // with the name shell: takes it by.
    const string folderDescriptions = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions";

    Settings settings;
    Func<string?> openFolder;
    TextBox? box;

    // Windows' list, for the box's window as it is now.
    object? autoComplete;

    // What the box says, with the folder open and the options, as the UI thread last saw them,
    // for Windows' thread to suggest from.
    volatile Typing typing = new("", null, false, null);

    // Whether the text last started with shell:.
    bool typingShellName;

    // Windows' thread's: the folder the text is in, as Windows last asked for it, null before it
    // has asked, or once the text has none; what is suggested, worked out as Windows first asks
    // for it; and how far through it Windows has got. Windows can start on what was typed since
    // on another thread while a read holds up the last, so the two take turns.
    Lock gate = new();
    string? expanded;
    List<string>? suggestions;
    int position;

    /// <param name="settings">Whether hidden folders, and Windows' own, are suggested.</param>
    /// <param name="openFolder">The folder open, which text that isn't a full path is taken
    /// from. Asked on the UI thread.</param>
    public AddressCompletion(Settings settings, Func<string?> openFolder)
    {
        this.settings = settings;
        this.openFolder = openFolder;
    }

    /// <summary>
    /// Reads the folders in a folder, as <see cref="Breadcrumb.FoldersIn"/> does, on Windows'
    /// thread for the list. Tests wrap it, to see where it runs, and to hold it while they type.
    /// </summary>
    internal Func<string, bool, SystemItems?, List<string>> ReadFolders { get; set; } = Breadcrumb.FoldersIn;

    /// <summary>
    /// Suggests for <paramref name="textBox"/>, each time its window is made.
    /// </summary>
    public void Attach(TextBox textBox)
    {
        box = textBox;
        textBox.HandleCreated += (_, _) => Bind();
        textBox.TextChanged += (_, _) => Typed();
        if (textBox.IsHandleCreated)
        {
            Bind();
        }
    }

    void Bind()
    {
        var complete = (IAutoComplete2) new AutoComplete();
        complete.Init(box!.Handle, this, null, null);
        complete.SetOptions(autoSuggest);
        autoComplete = complete;
    }

    // Before Windows asks for what to suggest for the text: it asks once the text is in the box.
    void Typed()
    {
        // Only as it is typed in: the bar puts each folder gone to in the box, unseen, and
        // starting the list again for that would read the folder for nothing.
        if (!box!.Focused)
        {
            return;
        }

        var text = box.Text;
        typing = new(text, openFolder(), settings.ShowHidden, settings.HiddenSystemItems());
        var shellName = IsShellName(text);
        if (shellName == typingShellName)
        {
            return;
        }

        // Windows works out what to suggest only when the folder the text is in changes, and
        // narrows that as more is typed: shell: has no separator to tell it, but the shell's
        // names in place of the folders are another list altogether.
        typingShellName = shellName;
        (autoComplete as IAutoCompleteDropDown)?.ResetEnumerator();
    }

    /// <summary>
    /// Whether Windows' list of suggestions is showing under the box. For tests.
    /// </summary>
    internal bool Suggesting
    {
        get
        {
            if (autoComplete is not IAutoCompleteDropDown dropDown)
            {
                return false;
            }

            dropDown.GetDropDownStatus(out var flags, out var selected);
            Marshal.FreeCoTaskMem(selected);
            return (flags & dropDownVisible) != 0;
        }
    }

    static bool IsShellName(string text) =>
        text.TrimStart().StartsWith(shellPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What is suggested for <paramref name="typed"/>, the text in the box, Windows having asked
    /// for the folder it is in, <paramref name="expanded"/>, the text up to its last separator,
    /// or null for none. The folders in that folder, each as the text names it, taken from
    /// <paramref name="folder"/>, the folder open, unless it is a full path; with no separator,
    /// those in the folder open, and the one above it. Windows narrows them to those that start
    /// with the text. Text starting with shell: is given the shell's names instead. Nothing for
    /// a folder that can't be read, or for a path not given in full where the folder open is
    /// This PC, which Enter would take from Lode's own folder.
    /// </summary>
    internal static List<string> Suggestions(string typed, string? expanded, string? folder, bool showHidden, SystemItems? hiddenSystem, Func<string, bool, SystemItems?, List<string>> read)
    {
        if (IsShellName(typed))
        {
            return shellNames.Value;
        }

        var prefix = expanded ?? "";
        var full = Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(prefix));
        if (!full &&
            (folder == null ||
             (Locations.IsThisPC(folder) && prefix.Length > 0)))
        {
            return [];
        }

        var location = folder;
        if (prefix.Length > 0)
        {
            location = Locations.NormalizeTyped(prefix, folder);
        }

        var suggested = new List<string>();
        if (location != null)
        {
            try
            {
                suggested.AddRange(read(location, showHidden, hiddenSystem).Select(_ => prefix + LastName(_)));
            }
            catch (Exception)
            {
                // A drive not ready, a share offline, a folder Lode is denied or one that isn't
                // there: nothing to suggest, as there is nothing there to go to.
            }
        }

        if (prefix.Length == 0 &&
            folder != null &&
            Locations.Parent(folder) != null)
        {
            suggested.Add("..");
        }

        return suggested;
    }

    // What a folder is called in the one it is in: a drive by its root, C:\, as This PC lists it.
    static string LastName(string location)
    {
        var trimmed = location.TrimEnd(Path.DirectorySeparatorChar);
        var separator = trimmed.LastIndexOf(Path.DirectorySeparatorChar);
        if (separator < 0)
        {
            return location;
        }

        return trimmed[(separator + 1)..];
    }

    // The shell's names for its folders, as shell: takes them: shell:Downloads, shell:AppData,
    // shell:Personal for Documents, and the rest Windows knows, by the names it describes its
    // known folders with, the user's and the machine's. Read once, on Windows' thread, the first
    // time shell: is typed.
    static Lazy<List<string>> shellNames = new(ReadShellNames);

    static List<string> ReadShellNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in (RegistryKey[]) [Registry.CurrentUser, Registry.LocalMachine])
        {
            using var descriptions = hive.OpenSubKey(folderDescriptions);
            if (descriptions == null)
            {
                continue;
            }

            foreach (var id in descriptions.GetSubKeyNames())
            {
                using var description = descriptions.OpenSubKey(id);
                if (description?.GetValue("Name") is string { Length: > 0 } name)
                {
                    names.Add(shellPrefix + name);
                }
            }
        }

        return [.. names];
    }

    // Windows' thread, from here down.

    public void Reset()
    {
        lock (gate)
        {
            expanded = null;
            suggestions = null;
            position = 0;
        }
    }

    public void Expand(string text)
    {
        lock (gate)
        {
            expanded = text;
            suggestions = null;
            position = 0;
        }
    }

    public int Next(int count, string[] elements, IntPtr fetched)
    {
        lock (gate)
        {
            if (suggestions == null)
            {
                var now = typing;
                suggestions = Suggestions(now.Text, expanded, now.Folder, now.ShowHidden, now.HiddenSystem, ReadFolders);
            }

            return Give(suggestions, ref position, count, elements, fetched);
        }
    }

    public int Skip(int count)
    {
        lock (gate)
        {
            position += count;
            if (position <= (suggestions?.Count ?? 0))
            {
                return 0;
            }

            return 1;
        }
    }

    public void Clone(out IEnumString clone)
    {
        lock (gate)
        {
            clone = new Copy(suggestions ?? [], position);
        }
    }

    // S_OK with as many as were asked for, S_FALSE with fewer, at the end.
    static int Give(List<string> from, ref int position, int count, string[] elements, IntPtr fetched)
    {
        var given = 0;
        while (given < count &&
               position < from.Count)
        {
            elements[given] = from[position];
            given++;
            position++;
        }

        if (fetched != IntPtr.Zero)
        {
            Marshal.WriteInt32(fetched, given);
        }

        if (given == count)
        {
            return 0;
        }

        return 1;
    }

    // What a copy of the suggestions goes through, from where the copy was made.
    sealed class Copy(List<string> suggestions, int position) : IEnumString
    {
        public int Next(int count, string[] elements, IntPtr fetched) =>
            Give(suggestions, ref position, count, elements, fetched);

        public int Skip(int count)
        {
            position += count;
            if (position <= suggestions.Count)
            {
                return 0;
            }

            return 1;
        }

        public void Reset() =>
            position = 0;

        public void Clone(out IEnumString clone) =>
            clone = new Copy(suggestions, position);
    }

    sealed record Typing(string Text, string? Folder, bool ShowHidden, SystemItems? HiddenSystem);

    [ComImport]
    [Guid("00BB2763-6A77-11D0-A535-00C04FD7D062")]
    class AutoComplete;

    [ComImport]
    [Guid("EAC04BC0-3791-11D2-BB95-0060977B464C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAutoComplete2
    {
        void Init(IntPtr edit, [MarshalAs(UnmanagedType.IUnknown)] object source, [MarshalAs(UnmanagedType.LPWStr)] string? registryPath, [MarshalAs(UnmanagedType.LPWStr)] string? quickComplete);

        void Enable(bool enable);

        void SetOptions(int options);

        void GetOptions(out int options);
    }

    [ComImport]
    [Guid("3CD141F4-3C6A-11D2-BCAA-00C04FD929DB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAutoCompleteDropDown
    {
        void GetDropDownStatus(out int flags, out IntPtr selected);

        void ResetEnumerator();
    }

    // How Windows asks for the suggestions in the folder the text is in, as a separator is
    // typed.
    [ComImport]
    [Guid("77A130B0-94FD-11D0-A544-00C04FD7D062")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IACList
    {
        void Expand([MarshalAs(UnmanagedType.LPWStr)] string text);
    }
}
