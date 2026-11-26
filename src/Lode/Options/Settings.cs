sealed class Settings
{
    static JsonSerializerOptions serializerOptions = new()
    {
        WriteIndented = true
    };

    // Null for settings that live only in memory, which is what tests use.
    string? filePath;

    public bool ShowHidden { get; set; } = true;

    // Windows' own files and folders, Program Files, and other people's folders. See SystemItems.
    public bool ShowSystem { get; set; }

    /// <summary>
    /// What to leave out of listings for Show system files, or null when it is on.
    /// </summary>
    public SystemItems? HiddenSystemItems()
    {
        if (ShowSystem)
        {
            return null;
        }

        return SystemItems.ForThisMachine;
    }

    // Light or dark, or whichever Windows is set to. See Theme.
    public Theme Theme { get; set; }

    // The tree's Recent section: folders Explorer adds to Quick access because they are used often.
    public bool ShowRecent { get; set; } = true;

    // The tree opens out down to the folder the tab shows and selects it there, as Explorer's
    // does only with its Expand to open folder setting, which is off by default. Off, the tree is
    // left as it is, and the folder is selected only where it already shows. See FolderTree.
    public bool ExpandToOpenFolder { get; set; } = true;

    // Where each action shows, for those changed in Options: as a button on the actions bar, in
    // the right-click menu, both or neither. Those missing show where LodeActions says.
    public Dictionary<string, ActionPlacement> Actions { get; set; } = [];

    public bool ShowsAsButton(string id)
    {
        if (Actions.TryGetValue(id, out var placement))
        {
            return placement.Button;
        }

        return LodeActions.Find(id)?.Button ?? false;
    }

    public bool ShowsInMenu(string id)
    {
        if (Actions.TryGetValue(id, out var placement))
        {
            return placement.Menu;
        }

        return LodeActions.Find(id)?.Menu ?? true;
    }

    // Whether an action shown as a button is in its parent's drop-down on the bar, rather than a
    // button of its own.
    public bool NestsButton(string id)
    {
        if (Actions.TryGetValue(id, out var placement) &&
            placement.ButtonNested is { } nested)
        {
            return nested;
        }

        return LodeActions.Find(id)?.ButtonNested ?? false;
    }

    // Whether an action in the right-click menus is in its parent's submenu, rather than an
    // entry of its own.
    public bool NestsInMenu(string id)
    {
        if (Actions.TryGetValue(id, out var placement) &&
            placement.MenuNested is { } nested)
        {
            return nested;
        }

        return LodeActions.Find(id)?.MenuNested ?? false;
    }

    // Git was one action, a submenu of pull, push, discard and purge, which are now actions of
    // their own under Git. Where Git was placed, each git action is, nested, so the menu and the
    // bar are as they were, with switch to the default branch beside them rather than a Git
    // submenu of its own where Git was not.
    void SplitGit()
    {
        if (!Actions.Remove("git", out var git))
        {
            return;
        }

        foreach (var id in LodeActions.GitIds)
        {
            Actions.TryAdd(
                id,
                new()
                {
                    Button = git.Button,
                    Menu = git.Menu,
                    ButtonNested = true,
                    MenuNested = true
                });
        }
    }

    /// <param name="buttonNested">Null for LodeActions' default, as for an action without a parent.</param>
    /// <param name="menuNested">Null for LodeActions' default, as for an action without a parent.</param>
    public void SetPlacement(string id, bool button, bool menu, bool? buttonNested = null, bool? menuNested = null) =>
        Actions[id] = new()
        {
            Button = button,
            Menu = menu,
            ButtonNested = buttonNested,
            MenuNested = menuNested
        };

    /// <summary>
    /// Whether an action is a button on the bar, and nested under its parent there, as the bar's
    /// own right-click menu and the Button actions page of Options change it: where it shows in
    /// the right-click menus stays as it is. <paramref name="nested"/> is ignored for an action
    /// without a parent.
    /// </summary>
    public void SetButtonPlacement(string id, bool button, bool nested)
    {
        if (LodeActions.Find(id)?.Parent == null)
        {
            SetPlacement(id, button, ShowsInMenu(id));
            return;
        }

        SetPlacement(id, button, ShowsInMenu(id), nested, NestsInMenu(id));
    }

    /// <summary>
    /// Whether an action is in the right-click menus, and nested under its parent there, as the
    /// Menu actions page of Options changes it: whether it is a button stays as it is.
    /// <paramref name="nested"/> is ignored for an action without a parent.
    /// </summary>
    public void SetMenuPlacement(string id, bool menu, bool nested)
    {
        if (LodeActions.Find(id)?.Parent == null)
        {
            SetPlacement(id, ShowsAsButton(id), menu);
            return;
        }

        SetPlacement(id, ShowsAsButton(id), menu, NestsButton(id), nested);
    }

    /// <summary>
    /// Whether any action is on the bar other than as LodeActions has it, which Reset on the
    /// Button actions page of Options undoes.
    /// </summary>
    public bool ButtonPlacementsChanged() =>
        PlacementsChanged(ButtonDiffers);

    /// <summary>
    /// Whether any action is in the right-click menus other than as LodeActions has it, which
    /// Reset on the Menu actions page of Options undoes.
    /// </summary>
    public bool MenuPlacementsChanged() =>
        PlacementsChanged(MenuDiffers);

    /// <summary>
    /// Every action on the bar as LodeActions has it, as Reset on the Button actions page of
    /// Options puts them: where each shows in the right-click menus stays as it is.
    /// </summary>
    public void ResetButtonPlacements() =>
        ResetPlacements(static (placement, action) =>
        {
            placement.Button = action.Button;
            placement.ButtonNested = null;
        });

    /// <summary>
    /// Every action in the right-click menus as LodeActions has it, as Reset on the Menu actions
    /// page of Options puts them: whether each is a button stays as it is.
    /// </summary>
    public void ResetMenuPlacements() =>
        ResetPlacements(static (placement, action) =>
        {
            placement.Menu = action.Menu;
            placement.MenuNested = null;
        });

    bool PlacementsChanged(Func<ActionPlacement, LodeActions.Definition, bool> differs) =>
        Actions.Any(_ => LodeActions.Find(_.Key) is { } action &&
                         differs(_.Value, action));

    // A placement left no different from LodeActions' is forgotten, as one never changed is.
    void ResetPlacements(Action<ActionPlacement, LodeActions.Definition> reset)
    {
        foreach (var (id, placement) in Actions.ToList())
        {
            if (LodeActions.Find(id) is not { } action)
            {
                continue;
            }

            reset(placement, action);
            if (!ButtonDiffers(placement, action) &&
                !MenuDiffers(placement, action))
            {
                Actions.Remove(id);
            }
        }
    }

    static bool ButtonDiffers(ActionPlacement placement, LodeActions.Definition action) =>
        placement.Button != action.Button ||
        (placement.ButtonNested ?? action.ButtonNested) != action.ButtonNested;

    static bool MenuDiffers(ActionPlacement placement, LodeActions.Definition action) =>
        placement.Menu != action.Menu ||
        (placement.MenuNested ?? action.MenuNested) != action.MenuNested;

    // The file list's columns hidden from its header's right-click menu: Date, Type, Size or Git.
    public List<string> HiddenColumns { get; set; } = [];

    // How wide each of the file list's columns is, at 100% scale, for those widened or narrowed:
    // one for every tab and window. The rest are as wide as FolderView makes them.
    public Dictionary<string, int> ColumnWidths { get; set; } = [];

    // How each folder was last sorted, for those sorted other than by Name, A to Z, which the
    // rest are. The most recently sorted last, so the longest ago go first once there are more
    // than maximumFolderSorts.
    public List<FolderSort> FolderSorts { get; set; } = [];

    const int maximumFolderSorts = 500;

    public FolderSort? SortOf(string folder) =>
        FolderSorts.FindLast(_ => Locations.Same(_.Folder, folder));

    /// <summary>
    /// Notes how a folder is sorted, by the key of a column, or forgets it when that is by
    /// Name, A to Z, which every folder is unless noted.
    /// </summary>
    public void SetSort(string folder, string column, bool ascending)
    {
        FolderSorts.RemoveAll(_ => Locations.Same(_.Folder, folder));
        if (column == "Name" &&
            ascending)
        {
            return;
        }

        FolderSorts.Add(new(folder, column, ascending));
        if (FolderSorts.Count > maximumFolderSorts)
        {
            FolderSorts.RemoveRange(0, FolderSorts.Count - maximumFolderSorts);
        }
    }

    // Which of the Changes and History panes shows, as tabs, where the window has room beside the
    // list for only one, for each repository History was last chosen or used in. Changes is the
    // default, and isn't noted. The most recently changed last, as FolderSorts are.
    public List<PaneTab> PaneTabs { get; set; } = [];

    const int maximumPaneTabs = 500;

    public GitPane PaneTabOf(string repo) =>
        PaneTabs.FindLast(_ => Locations.Same(_.Repo, repo))?.Tab ?? GitPane.Changes;

    /// <summary>
    /// Notes which pane shows for a repository, or forgets it when that is Changes.
    /// </summary>
    public void SetPaneTab(string repo, GitPane tab)
    {
        PaneTabs.RemoveAll(_ => Locations.Same(_.Repo, repo));
        if (tab == GitPane.Changes)
        {
            return;
        }

        PaneTabs.Add(new(repo, tab));
        if (PaneTabs.Count > maximumPaneTabs)
        {
            PaneTabs.RemoveRange(0, PaneTabs.Count - maximumPaneTabs);
        }
    }

    // How wide each of the Changes and History panes is, at 100% scale, beside the list or slid
    // out. 0 for the width they start at.
    public int PaneWidth { get; set; }

    // How much of the room the two panes have side by side Changes takes, from 0 to 1. 0 for half.
    public double PaneSplit { get; set; }

    // How the tabs down the window's right edge are labelled: each pane's icon with its name
    // written down the strip, its name across under the icon, or the icon alone. Chosen from the
    // strip's right-click menu. See EdgeStrip.
    public StripLabels StripLabels { get; set; }

    // Where a file's changes show, picked in the Changes or History pane: in the Diff tab, in a
    // window of their own, over the folder list, or in the text editor or git's diff tool. See
    // DiffHost.
    public DiffPlacement DiffPlacement { get; set; }

    // How a diff is shown, as GitHub Desktop's diff options set it: the old file beside the new
    // rather than one above the other, and whitespace changes left out.
    public bool DiffSplit { get; set; }
    public bool DiffHideWhitespace { get; set; }

    // The Azure DevOps organizations added in Clone, to list the repositories of beside those of
    // the organizations the code folder has clones from.
    public List<string> AzureDevOpsOrganizations { get; set; } = [];

    // The tree's sections (Pinned, Recent) the user collapsed, so they stay that way.
    public List<string> CollapsedSections { get; set; } = [];

    // What Quick access had pinned, and its recent folders, when it was last read. A window
    // opens with these in its tree, rather than waiting for Quick access to be read. See Places.
    public List<string> PinnedFolders { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];

    // The folders removed from the taskbar's jump list, which it leaves out while Quick access
    // has them. See JumpList.
    public List<string> JumpListRemoved { get; set; } = [];

    // Where a new window opens when it is given nothing else to show. Null for the user folder.
    public string? DefaultDirectory { get; set; }

    // The executable Edit opens text files in. Null for the first editor installed, or Notepad.
    public string? TextEditor { get; set; }

    // The folder holding the user's git repositories. Folders in it get git and NuGet commands
    // on their right-click menu. Null for none.
    public string? CodeDirectory { get; set; }

    // Git purge deletes what git doesn't track outright, rather than to the Recycle Bin.
    public bool PurgePermanently { get; set; }

    // The files a folder down from a repository's root that are listed at the root too, as
    // shortcuts, one pattern each: a whole name, or one with a * for how it starts and ends,
    // *.slnx say. None unless added in Options. See PromotedFiles.
    public List<string> PromotedFiles { get; set; } = [];

    // Find in Files' toggles, as last set in its strip: what it looks for is a regular
    // expression, in the case typed, as a whole word; Replace keeps each match's case; lines
    // either side of each match are shown, FindContextLines of them; hidden files are searched
    // (on by default); and what git ignores is searched too (off by default). See FindPanel.
    public bool FindRegex { get; set; }
    public bool FindCaseSensitive { get; set; }
    public bool FindWholeWord { get; set; }
    public bool FindPreserveCase { get; set; }
    public bool FindContext { get; set; } = true;
    public int FindContextLines { get; set; } = 2;
    public bool FindHidden { get; set; } = true;
    public bool FindIgnored { get; set; }

    /// <summary>
    /// Whether the search box, by name, goes through what git ignores too, bin and node_modules
    /// say, rather than leaving it out as Find in Files does.
    /// </summary>
    public bool SearchIgnored { get; set; }

    // What was searched for, where, and replaced with, newest first, for Up and Down in the
    // strip's boxes to go back through. No more than maximumHistory of each.
    public List<string> FindHistory { get; set; } = [];
    public List<string> WhereHistory { get; set; } = [];
    public List<string> ReplaceHistory { get; set; } = [];

    const int maximumHistory = 20;

    /// <summary>
    /// Puts <paramref name="entry"/> first in <paramref name="history"/>, once, keeping no more
    /// than the most kept.
    /// </summary>
    public static void Remember(List<string> history, string entry)
    {
        if (entry.Length == 0)
        {
            return;
        }

        history.Remove(entry);
        history.Insert(0, entry);
        if (history.Count > maximumHistory)
        {
            history.RemoveRange(maximumHistory, history.Count - maximumHistory);
        }
    }

    // Tabs nobody has looked at for UnusedTabHours are closed, as other apps opening folders in
    // Lode leave a tab for each. Off unless chosen in Options. See MainForm.CloseUnusedTabs.
    public bool CloseUnusedTabs { get; set; }
    public int UnusedTabHours { get; set; } = 24;

    // When NuGet was last asked for a newer Lode, and the newer version it had then, if any.
    // See Updates.
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? AvailableUpdate { get; set; }

    /// <summary>
    /// True when <paramref name="folder"/> is the code folder or inside it.
    /// </summary>
    public bool IsCode(string folder) =>
        CodeDirectory != null &&
        !Locations.IsThisPC(folder) &&
        Locations.IsWithin(folder, CodeDirectory);

    /// <summary>
    /// <see cref="CodeDirectory"/> when it is set and no longer there: renamed, moved or deleted
    /// while Lode wasn't running, or on a drive that isn't plugged in. Not one on the network,
    /// as asking after that waits on it.
    /// </summary>
    public string? MissingCodeDirectory()
    {
        if (CodeDirectory == null ||
            Locations.IsRemote(CodeDirectory) ||
            Directory.Exists(CodeDirectory))
        {
            return null;
        }

        return CodeDirectory;
    }

    /// <summary>
    /// <see cref="DefaultDirectory"/> when it is set and still there, otherwise the user folder.
    /// </summary>
    public string StartLocation()
    {
        if (DefaultDirectory != null &&
            (Locations.IsThisPC(DefaultDirectory) || Locations.IsRemote(DefaultDirectory) || Directory.Exists(DefaultDirectory)))
        {
            return DefaultDirectory;
        }

        return Locations.Home;
    }

    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int SplitterDistance { get; set; }
    public List<string> Tabs { get; set; } = [];

    // When each of Tabs was last looked at, in the same order, so closing unused tabs counts from
    // then after a restart, an update's say, rather than starting the hours again. Empty, or not
    // one for each tab, in settings saved before Lode kept them.
    public List<DateTimeOffset> TabsLastShown { get; set; } = [];
    public int ActiveTab { get; set; }

    internal static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lode");

    // A Debug build keeps its own, so one run from an IDE beside the installed Lode doesn't
    // save its tabs and window over the installed one's.
    const string fileName =
#if DEBUG
        "settings.debug.json";
#else
        "settings.json";
#endif

    public static string DefaultPath =>
        Path.Combine(Folder, fileName);

    /// <summary>
    /// On a Debug build's first run, starts its settings as a copy of the installed Lode's, so it
    /// opens with the same options and tabs. Nothing once it has settings of its own.
    /// </summary>
    public static void SeedFromInstalled()
    {
        var installed = Path.Combine(Folder, "settings.json");
        if (DefaultPath == installed ||
            File.Exists(DefaultPath) ||
            !File.Exists(installed))
        {
            return;
        }

        try
        {
            File.Copy(installed, DefaultPath);
        }
        catch (IOException)
        {
            // Starting with defaults is fine.
        }
    }

    /// <summary>
    /// What went wrong reading the settings file, for Lode to tell the user as it starts. Null
    /// when it was read, or there was none. Neither this nor <see cref="LoadBug"/> is saved.
    /// </summary>
    internal string? LoadProblem;

    /// <summary>
    /// What reading the settings file threw that was neither the disk nor the file being corrupt,
    /// which is Lode's own doing and so offered as an issue.
    /// </summary>
    internal Exception? LoadBug;

    /// <summary>
    /// A settings file that cannot be read never stops Lode starting, nor is it lost: the first
    /// save would otherwise write the defaults over every tab and option in it. A corrupt one is
    /// moved aside and the settings saved afresh. One that could not be opened, locked or not
    /// permitted, is left as it is and not saved over while Lode runs.
    /// </summary>
    public static Settings Load(string path)
    {
        Settings? settings = null;
        string? problem = null;
        Exception? bug = null;
        var savable = true;
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            }
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
        {
            problem = $"""
                       Lode couldn't read its settings from {path}

                       {exception.Message}

                       It started with the defaults, and leaves the file as it is rather than save over it.
                       """;
            savable = false;
        }
        catch (Exception exception)
        {
            // A JsonException is the file, corrupt. Anything else is Lode's.
            if (exception is not JsonException)
            {
                bug = exception;
            }

            (problem, savable) = KeepAside(path, exception);
        }

        settings ??= new();
        if (savable)
        {
            settings.filePath = path;
        }

        settings.LoadProblem = problem;
        settings.LoadBug = bug;
        settings.SplitGit();
        return settings;
    }

    internal static string UnreadablePath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.unreadable{Path.GetExtension(path)}");

    static (string Problem, bool Savable) KeepAside(string path, Exception exception)
    {
        var kept = UnreadablePath(path);
        try
        {
            File.Move(path, kept, overwrite: true);
        }
        catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException or SecurityException)
        {
            return ($"""
                     Lode couldn't read its settings in {path}, so it started with the defaults.

                     {exception.Message}

                     Nor could it move the file aside ({moveException.Message}), so it leaves it as it is rather than save over it.
                     """, false);
        }

        return ($"""
                 Lode couldn't read its settings in {path}, so it started with the defaults.

                 {exception.Message}

                 The file is kept as {kept}, and the settings are saved afresh.
                 """, true);
    }

    // Each reason told once a run: a disk that is full or read only fails every save, and every
    // window that closes saves.
    static ConcurrentDictionary<string, byte> unsavedReasons = new();

    /// <summary>
    /// Tells the user the settings could not be written. A field so that a test is not left
    /// waiting on a modal box nobody is there to answer.
    /// </summary>
    internal static Action<string, Exception> Unsaved = static (path, exception) =>
        MessageBox.Show(
            $"""
             Lode couldn't save its settings to {path}

             {exception.Message}

             Changes to Lode's settings are lost when it closes, unless a later save gets through.
             """,
            "Lode",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

    public void Save()
    {
        if (filePath == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            // Written beside the file, then put in its place. Written over the file where it
            // is, a save cut short, by the power going or Lode being ended, left it empty or
            // half written, and the next start with no settings at all.
            var writing = $"{filePath}.writing";
            File.WriteAllText(writing, JsonSerializer.Serialize(this, serializerOptions));
            File.Move(writing, filePath, overwrite: true);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
        {
            // The disk, or the file's permissions: nothing Lode did wrong, but what was changed
            // is lost when Lode ends unless a later save gets through, so the user is told.
            if (unsavedReasons.TryAdd(exception.Message, 0))
            {
                Unsaved(filePath, exception);
            }
        }
        catch (Exception exception)
        {
            // Not the disk: settings that do not serialize, which is Lode's own doing and would
            // stop them being saved from then on. Reported rather than thrown, as a save is
            // often the last thing a window does as it closes.
            IssueLauncher.LaunchForException("Failed to save settings", exception);
        }
    }
}