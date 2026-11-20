/// <summary>
/// Everything the file list's right-click menus and the actions bar offer, each by an id, with
/// its name, icon, and whether it shows as a button and in the right-click menu unless Options
/// says otherwise. The menus keep their own order and show only what applies to what was
/// clicked. The bar shows its buttons in this order, with a separator between groups, each
/// enabled when it applies to the selection, or to the folder when nothing is selected.
/// </summary>
static class LodeActions
{
    /// <param name="Submenu">Opens a list of its own (New, Open with, Coding agent): a drop-down on the bar.</param>
    /// <param name="Parent">What it can be nested under, in the right-click menus and on the bar.</param>
    /// <param name="MenuNested">Whether it is nested under its parent in the right-click menus
    /// unless Options says otherwise. On the bar, nothing is nested unless Options says so.</param>
    public sealed record Definition(
        string Id,
        string Text,
        char Glyph,
        int Group,
        bool Button,
        bool Menu,
        string Shortcut = "",
        bool Submenu = false,
        Parent? Parent = null,
        bool MenuNested = false);

    /// <summary>
    /// A submenu in the right-click menus, and a drop-down on the bar, that actions can be nested
    /// under, each as Options says.
    /// </summary>
    public sealed record Parent(string Text, char Glyph);

    static readonly Parent clipboard = new("Clipboard", Icons.Paste);
    static readonly Parent undoAndRedo = new("Undo and redo", Icons.Undo);
    static readonly Parent file = new("File", Icons.Document);
    static readonly Parent open = new("Open", Icons.Open);
    static readonly Parent terminal = new("Terminal", Icons.Terminal);
    static readonly Parent ai = new("AI", Icons.Agent);
    static readonly Parent git = new("Git", Icons.Git);
    static readonly Parent packages = new("Packages", Icons.Package);

    /// <summary>
    /// The git actions. Pull, push, discard and purge were one, Git, a submenu of them. See
    /// <see cref="Settings"/>.
    /// </summary>
    public static IReadOnlyList<string> GitIds { get; } = ["gitPull", "gitSwitchToDefault", "gitPush", "gitTag", "gitDeleteTag", "gitDiscard", "gitPurge"];

    /// <summary>
    /// Show more options: the full Windows menu. Not a command of its own in the menus, which
    /// always end with it.
    /// </summary>
    public const string More = "more";

    // The right-click menus start with the less used nested: both terminals, so there is never a
    // Terminal entry beside a Terminal submenu, the AI actions, the git actions and the NuGet
    // updates.
    public static IReadOnlyList<Definition> All { get; } =
    [
        new("new", "New", Icons.New, 0, Button: true, Menu: true, Submenu: true),
        new("undo", "Undo", Icons.Undo, 1, Button: false, Menu: true, "Ctrl+Z", Parent: undoAndRedo),
        new("redo", "Redo", Icons.Redo, 1, Button: false, Menu: true, "Ctrl+Y", Parent: undoAndRedo),
        new("cut", "Cut", Icons.Cut, 1, Button: true, Menu: true, "Ctrl+X", Parent: clipboard),
        new("copy", "Copy", Icons.Copy, 1, Button: true, Menu: true, "Ctrl+C", Parent: clipboard),
        new("paste", "Paste", Icons.Paste, 1, Button: true, Menu: true, "Ctrl+V", Parent: clipboard),
        new("rename", "Rename", Icons.Rename, 1, Button: true, Menu: true, "F2", Parent: file),
        new("delete", "Delete", Icons.Delete, 1, Button: true, Menu: true, "Delete", Parent: file),
        new("open", "Open", Icons.Open, 2, Button: false, Menu: true, Parent: open),
        new("openInNewTab", "Open in new tab", Icons.OpenInNewTab, 2, Button: false, Menu: true, Parent: open),
        new("run", "Run", Icons.Run, 2, Button: false, Menu: true, Parent: open),
        new("edit", "Edit", Icons.Edit, 2, Button: false, Menu: true, Parent: open),
        new("openWith", "Open with", Icons.OpenWith, 2, Button: false, Menu: true, Submenu: true, Parent: open),
        new("terminal", "Terminal", Icons.Terminal, 2, Button: true, Menu: true, Parent: terminal, MenuNested: true),
        new("terminalAdmin", "Terminal (admin)", Icons.AdminTerminal, 2, Button: true, Menu: true, Parent: terminal, MenuNested: true),
        new("claude", "Open in Claude", Icons.Claude, 2, Button: false, Menu: true, Parent: ai, MenuNested: true),
        new("agent", "Coding agent", Icons.Agent, 2, Button: false, Menu: true, Submenu: true, Parent: ai, MenuNested: true),
        new("pin", "Pin to Quick access", Icons.Pin, 3, Button: false, Menu: true, Parent: file),
        new("createShortcut", "Create shortcut", Icons.Shortcut, 3, Button: false, Menu: true, Parent: file),
        new("compress", "Compress to ZIP file", Icons.Zip, 3, Button: false, Menu: true, Parent: file),
        new("extractAll", "Extract All", Icons.Zip, 3, Button: false, Menu: true, Parent: file),
        new("extractHere", "Extract here", Icons.Zip, 3, Button: false, Menu: true, Parent: file),
        new("copyAsPath", "Copy as path", Icons.CopyAsPath, 3, Button: false, Menu: true, "Ctrl+Shift+C", Parent: clipboard),
        new("properties", "Properties", Icons.Properties, 3, Button: true, Menu: true, "Alt+Enter"),
        new("gitPull", "Git pull", Icons.Pull, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitSwitchToDefault", "Git switch to default branch", Icons.SwitchBranch, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitPush", "Git push", Icons.Push, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitTag", "Git tag", Icons.Tag, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitDeleteTag", "Git delete tag", Icons.Delete, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitDiscard", "Git discard", Icons.Discard, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("gitPurge", "Git purge", Icons.Purge, 4, Button: false, Menu: true, Parent: git, MenuNested: true),
        new("updateNugets", "Update NuGets", Icons.Package, 4, Button: false, Menu: true, Parent: packages, MenuNested: true),
        new("updateNugetsAndBuild", "Update NuGets and build", Icons.Build, 4, Button: false, Menu: true, Parent: packages, MenuNested: true),
        new(More, "Show more options", Icons.MoreOptions, 5, Button: false, Menu: true)
    ];

    /// <summary>
    /// The actions this machine can offer, for the bar, See more and Options: all of them, less
    /// Open in Claude without the Claude desktop app, and Coding agent without an agent.
    /// </summary>
    public static IEnumerable<Definition> Offered()
    {
        var claude = ClaudeDesktop.IsInstalled();
        var agents = CodingAgents.Installed().Count > 0;
        return All.Where(_ => (claude || _.Id != "claude") &&
                              (agents || _.Id != "agent"));
    }

    static readonly Dictionary<string, Definition> byId = All.ToDictionary(_ => _.Id);

    public static Definition? Find(string id) =>
        byId.GetValueOrDefault(id);

    static Definition Get(string id) =>
        byId[id];

    /// <summary>
    /// A right-click menu entry for the action, with its icon. <paramref name="text"/> stands in
    /// for the action's name where the menu words it differently.
    /// </summary>
    public static QuickMenu.Command Command(string id, Action action, bool enabled = true, string? text = null)
    {
        var definition = Get(id);
        return new(text ?? definition.Text, action, enabled)
        {
            Id = id,
            Glyph = definition.Glyph
        };
    }

    /// <summary>
    /// Pin to Quick access for <paramref name="folders"/>, or Unpin when every one of them is
    /// pinned already. <paramref name="setPinned"/> pins or unpins one folder.
    /// </summary>
    public static QuickMenu.Command Pin(IReadOnlyList<string> folders, Places places, Action<string, bool> setPinned)
    {
        var allPinned = folders.All(folder => places.Pinned.Any(_ => Locations.Same(_, folder)));
        if (allPinned)
        {
            return Command(
                    "pin",
                    () =>
                    {
                        foreach (var folder in folders)
                        {
                            setPinned(folder, false);
                        }
                    },
                    text: "Unpin from Quick access") with
                {
                    Glyph = Icons.Unpin
                };
        }

        return Command(
            "pin",
            () =>
            {
                foreach (var folder in folders)
                {
                    setPinned(folder, true);
                }
            });
    }

    /// <summary>
    /// A right-click menu entry that opens <paramref name="children"/>.
    /// </summary>
    public static QuickMenu.Command Submenu(string id, IReadOnlyList<QuickMenu.Command?> children) =>
        QuickMenu.Submenu(Get(id).Text, children) with
        {
            Id = id,
            Glyph = Get(id).Glyph
        };

    /// <summary>
    /// A parent's submenu or drop-down: <paramref name="children"/> under its name and icon,
    /// greyed out when none of them applies.
    /// </summary>
    public static QuickMenu.Command Submenu(Parent parent, IReadOnlyList<QuickMenu.Command?> children) =>
        QuickMenu.Submenu(parent.Text, children) with
        {
            Glyph = parent.Glyph,
            Enabled = children.Any(_ => _ is { Enabled: true })
        };

    /// <summary>
    /// <paramref name="command"/> as its parent's submenu or drop-down lists it, by what it is
    /// called there.
    /// </summary>
    public static QuickMenu.Command Nested(QuickMenu.Command command)
    {
        if (command.NestedText is { } text)
        {
            return command with
            {
                Text = text
            };
        }

        return command;
    }

    /// <summary>
    /// The commands without those Options keeps off the right-click menu, those Options nests in
    /// their parent's submenu, and without the separators that leaves doubled or at either end.
    /// A parent's submenu stands where its first member was, and takes in the rest in the
    /// menu's own order.
    /// </summary>
    public static List<QuickMenu.Command?> ForMenu(IEnumerable<QuickMenu.Command?> commands, Settings settings)
    {
        // Each entry is a command, a separator (neither), or the place of a parent's submenu,
        // which is made once all its members are known.
        var placed = new List<(QuickMenu.Command? Command, Parent? Parent)>();
        var nested = new Dictionary<Parent, List<QuickMenu.Command?>>();
        foreach (var command in commands)
        {
            if (command?.Id is { } id)
            {
                if (!settings.ShowsInMenu(id))
                {
                    continue;
                }

                if (Find(id)?.Parent is { } parent &&
                    settings.NestsInMenu(id))
                {
                    if (!nested.TryGetValue(parent, out var members))
                    {
                        members = [];
                        nested[parent] = members;
                        placed.Add((null, parent));
                    }

                    members.Add(Nested(command));
                    continue;
                }
            }

            placed.Add((command, null));
        }

        var result = new List<QuickMenu.Command?>();
        foreach (var (command, parent) in placed)
        {
            var entry = command;
            if (parent != null)
            {
                entry = Submenu(parent, nested[parent]);
            }

            if (entry == null)
            {
                if (result.Count > 0 &&
                    result[^1] != null)
                {
                    result.Add(null);
                }

                continue;
            }

            result.Add(entry);
        }

        if (result.Count > 0 &&
            result[^1] == null)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
