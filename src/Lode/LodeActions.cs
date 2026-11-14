/// <summary>
/// Everything the file list's right-click menus and the actions bar offer, each by an id, with
/// its name, icon, and whether it shows as a button and in the right-click menu unless Options
/// says otherwise. The menus keep their own order and show only what applies to what was
/// clicked. The bar shows its buttons in this order, with a separator between groups, each
/// enabled when it applies to the selection, or to the folder when nothing is selected.
/// </summary>
static class LodeActions
{
    /// <param name="Submenu">Opens a list of its own (New, Git): a drop-down on the bar.</param>
    public sealed record Definition(
        string Id,
        string Text,
        char Glyph,
        int Group,
        bool Button,
        bool Menu,
        string Shortcut = "",
        bool Submenu = false);

    /// <summary>
    /// Show more options: the full Windows menu. Not a command of its own in the menus, which
    /// always end with it.
    /// </summary>
    public const string More = "more";

    public static IReadOnlyList<Definition> All { get; } =
    [
        new("new", "New", Icons.New, 0, Button: true, Menu: true, Submenu: true),
        new("cut", "Cut", Icons.Cut, 1, Button: true, Menu: true, "Ctrl+X"),
        new("copy", "Copy", Icons.Copy, 1, Button: true, Menu: true, "Ctrl+C"),
        new("paste", "Paste", Icons.Paste, 1, Button: true, Menu: true, "Ctrl+V"),
        new("rename", "Rename", Icons.Rename, 1, Button: true, Menu: true, "F2"),
        new("delete", "Delete", Icons.Delete, 1, Button: true, Menu: true, "Delete"),
        new("open", "Open", Icons.Open, 2, Button: false, Menu: true),
        new("openInNewTab", "Open in new tab", Icons.OpenInNewTab, 2, Button: false, Menu: true),
        new("edit", "Edit", Icons.Edit, 2, Button: false, Menu: true),
        new("terminal", "Terminal", Icons.Terminal, 2, Button: true, Menu: true),
        new("terminalAdmin", "Terminal (admin)", Icons.AdminTerminal, 2, Button: true, Menu: true),
        new("pin", "Pin to Quick access", Icons.Pin, 3, Button: false, Menu: true),
        new("createShortcut", "Create shortcut", Icons.Shortcut, 3, Button: false, Menu: true),
        new("copyAsPath", "Copy as path", Icons.CopyAsPath, 3, Button: false, Menu: true),
        new("properties", "Properties", Icons.Properties, 3, Button: true, Menu: true, "Alt+Enter"),
        new("git", "Git", Icons.Git, 4, Button: false, Menu: true, Submenu: true),
        new("updateNugets", "Update NuGets", Icons.Package, 4, Button: false, Menu: true),
        new("updateNugetsAndBuild", "Update NuGets and build", Icons.Build, 4, Button: false, Menu: true),
        new(More, "Show more options", Icons.MoreOptions, 5, Button: false, Menu: true)
    ];

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
    /// The commands without those Options keeps off the right-click menu, and without the
    /// separators that leaves doubled or at either end.
    /// </summary>
    public static List<QuickMenu.Command?> ForMenu(IEnumerable<QuickMenu.Command?> commands, Settings settings)
    {
        var result = new List<QuickMenu.Command?>();
        foreach (var command in commands)
        {
            if (command == null)
            {
                if (result.Count > 0 &&
                    result[^1] != null)
                {
                    result.Add(null);
                }

                continue;
            }

            if (command.Id is { } id &&
                !settings.ShowsInMenu(id))
            {
                continue;
            }

            result.Add(command);
        }

        if (result.Count > 0 &&
            result[^1] == null)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
