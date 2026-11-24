/// <summary>
/// A window's actions bar, Explorer's command bar under the address bar: buttons for what is
/// most often done to the selection, in LodeActions' order with a separator between groups,
/// those Options nests under a parent in that parent's drop-down, then See more for the actions
/// that are not buttons and those whose buttons don't fit. Each acts on the window's active view.
/// </summary>
sealed class ActionsBar
{
    Form window;
    Settings settings;
    Func<FolderView?> activeView;
    Action<string> showOptions;
    Action<SettingsChange> applyToAllWindows;
    ActionsStrip strip = new();

    // The bar's buttons, by action id.
    Dictionary<string, ToolStripItem> actionButtons = [];

    // The bar's drop-downs for parents that Options nests buttons under, each with the actions
    // in it, in LodeActions' order.
    Dictionary<LodeActions.Parent, (ToolStripItem Button, List<LodeActions.Definition> Members)> parentButtons = [];

    // The bar's buttons, drop-downs and the separators between their groups, in its order: all
    // but See more and the separator before it.
    List<ToolStripItem> barItems = [];
    ToolStripSeparator? seeMoreSeparator;
    ToolStripItem? seeMore;

    // Whether any action is not a button, so is under See more however wide the bar is.
    bool otherActions;

    // The actions whose buttons don't fit on the bar, so are under See more.
    HashSet<string> collapsedActions = [];

    // Icons drawn for the open drop-down's entries.
    List<Image> dropDownImages = [];

    /// <param name="window">The window the bar is in, whose scale its sizes are at.</param>
    /// <param name="activeView">The view the actions act on, null while there is none.</param>
    /// <param name="showOptions">Opens Options at the page named.</param>
    /// <param name="applyToAllWindows">Shows a change to the settings in every window.</param>
    public ActionsBar(Form window, Settings settings, Func<FolderView?> activeView, Action<string> showOptions, Action<SettingsChange> applyToAllWindows)
    {
        this.window = window;
        this.settings = settings;
        this.activeView = activeView;
        this.showOptions = showOptions;
        this.applyToAllWindows = applyToAllWindows;
        strip.Dock = DockStyle.Top;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        // In a menu's font, which a tool strip takes unless told otherwise: smaller than the rest
        // of the window, as Explorer's command bar is.
        strip.Renderer = new ExplorerRenderer();
        // Roomy, and as tall as Explorer's command bar.
        strip.Padding = new(0, window.LogicalToDeviceUnits(6), 0, window.LogicalToDeviceUnits(6));
        // Buttons that don't fit go under See more rather than a chevron of the strip's own.
        strip.CanOverflow = false;
        strip.SizeChanged += (_, _) => Fit();
        strip.MenuRequested += ShowMenu;
        Rebuild();
    }

    /// <summary>
    /// The strip the bar is, for the window to dock under its address bar.
    /// </summary>
    public ToolStrip Strip => strip;

    void ShowMenu(Point screen) =>
        ShowMenu(screen, MenuCommands(strip.GetItemAt(strip.PointToClient(screen))));

    // The menu of an entry in a drop-down, <paramref name="dropDown"/>, is owned by an item of
    // that drop-down, as a submenu is, so the drop-down stays open beneath it, as Windows' menus
    // do: Escape closes only the menu, and choosing from it closes both. Not the entry itself:
    // owned by a menu entry, a menu goes where that entry's own submenu would, which for one
    // without is the top left of the screen. So the owner is a button, hidden, there only while
    // the menu is open.
    void ShowMenu(Point screen, IReadOnlyList<QuickMenu.Command?> commands, ToolStripDropDown? dropDown = null)
    {
        Action? chosen = null;
        var menu = QuickMenu.Build(commands, null, _ => chosen = _);
        MenuShowing = menu;
        ToolStripButton? owner = null;
        // Run the choice once the menu has closed: the bar is made again, and Customize opens a
        // dialog.
        menu.Closed += (_, _) => menu.BeginInvoke(
            () =>
            {
                if (MenuShowing == menu)
                {
                    MenuShowing = null;
                }

                if (owner != null)
                {
                    owner.Owner?.Items.Remove(owner);
                    owner.Dispose();
                }

                menu.Dispose();
                chosen?.Invoke();
            });
        if (dropDown == null)
        {
            menu.Show(strip, strip.PointToClient(screen));
            return;
        }

        owner = new()
        {
            Available = false
        };
        dropDown.Items.Add(owner);
        menu.OwnerItem = owner;
        menu.Show(screen);
    }

    /// <summary>
    /// The bar's right-click menu, or an action's in a drop-down, while it is open, for tests.
    /// </summary>
    internal ContextMenuStrip? MenuShowing { get; private set; }

    // Right-clicking an action in a parent's drop-down, which lists them in their order, one
    // each: what can be done to it.
    void ShowNestedMenu(ChevronDropDownButton dropDown, IReadOnlyList<LodeActions.Definition> nested, Point screen)
    {
        if (dropDown.DropDown.GetItemAt(dropDown.DropDown.PointToClient(screen)) is not { } item ||
            dropDown.DropDownItems.IndexOf(item) is not (var index and >= 0) ||
            index >= nested.Count)
        {
            return;
        }

        ShowMenu(screen, NestedMenuCommands(nested[index].Id), dropDown.DropDown);
    }

    /// <summary>
    /// What right-clicking the action <paramref name="id"/> in its parent's drop-down offers:
    /// hide it, which puts it under See more, still nested there for when it is a button again,
    /// and un-nest it, a button of its own after the drop-down.
    /// </summary>
    internal List<QuickMenu.Command?> NestedMenuCommands(string id) =>
    [
        new("Hide button", () => PlaceButtons([id], shown: false, nested: true)),
        new("Un-nest", () => PlaceButtons([id], shown: true, nested: false)),
        null,
        new("Customize buttons…", () => showOptions("Actions"))
    ];

    /// <summary>
    /// What right-clicking <paramref name="item"/> on the bar offers. For an action's own button:
    /// hide it, which puts it under See more, and nest it under its parent. For a parent's
    /// drop-down: hide it, and un-nest what is in it, all or one, each a button of its own.
    /// Then, wherever the bar is right-clicked, the Actions page of Options, for the rest and to
    /// show again what was hidden. Each changes every window, as a tick on that page does.
    /// </summary>
    internal List<QuickMenu.Command?> MenuCommands(ToolStripItem? item)
    {
        var commands = new List<QuickMenu.Command?>();
        var id = actionButtons
            .Where(_ => _.Value == item)
            .Select(_ => _.Key)
            .FirstOrDefault();
        if (id != null)
        {
            commands.Add(new("Hide button", () => PlaceButtons([id], shown: false, nested: false)));
            if (LodeActions.Find(id)?.Parent is { } parent)
            {
                commands.Add(new($"Nest under {parent.Text}", () => PlaceButtons([id], shown: true, nested: true)));
            }

            commands.Add(null);
        }

        foreach (var (button, members) in parentButtons.Values)
        {
            if (button != item)
            {
                continue;
            }

            var ids = members.Select(_ => _.Id).ToList();
            commands.Add(new("Hide button", () => PlaceButtons(ids, shown: false, nested: true)));
            if (members.Count == 1)
            {
                commands.Add(new("Un-nest", () => PlaceButtons(ids, shown: true, nested: false)));
            }
            else
            {
                commands.Add(new("Un-nest all", () => PlaceButtons(ids, shown: true, nested: false)));
                commands.Add(
                    QuickMenu.Submenu(
                        "Un-nest one",
                        [
                            .. members.Select(member =>
                                new QuickMenu.Command(member.Text, () => PlaceButtons([member.Id], shown: true, nested: false)))
                        ]));
            }

            commands.Add(null);
        }

        commands.Add(new("Customize buttons…", () => showOptions("Actions")));
        return commands;
    }

    /// <summary>
    /// What right-clicking the bar's item <paramref name="text"/> offers, for tests.
    /// </summary>
    internal List<QuickMenu.Command?> MenuCommands(string text) =>
        MenuCommands(Item(text));

    /// <summary>
    /// The bar's item <paramref name="text"/>, for tests.
    /// </summary>
    internal ToolStripItem Item(string text) =>
        strip.Items.OfType<ToolStripItem>().Single(_ => _.Text == text);

    // Each action a button or not, nested under its parent or not, staying where it is in the
    // right-click menus. Saved at once, as a change in Options is, and so in every window.
    void PlaceButtons(IEnumerable<string> ids, bool shown, bool nested)
    {
        foreach (var id in ids)
        {
            settings.SetButtonPlacement(id, shown, nested);
        }

        settings.Save();
        applyToAllWindows(SettingsChange.Actions);
    }

    /// <summary>
    /// Makes the bar again from what Options asks for: the buttons, in LodeActions' order, with a
    /// separator between groups, then See more, as Explorer's command bar ends, for the actions
    /// that are not buttons and those whose buttons don't fit.
    /// </summary>
    public void Rebuild()
    {
        strip.SuspendLayout();
        foreach (var item in strip.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }

        strip.Items.Clear();
        actionButtons.Clear();
        parentButtons.Clear();
        barItems.Clear();
        // Explorer's size for its command bar's icons.
        var iconSize = window.LogicalToDeviceUnits(16);
        strip.ImageScalingSize = new(iconSize, iconSize);
        int? group = null;
        foreach (var action in LodeActions.Offered().Where(_ => settings.ShowsAsButton(_.Id)))
        {
            // A nested button is in its parent's drop-down, which stands where its first member
            // would have.
            var parent = settings.NestsButton(action.Id) ? action.Parent : null;
            if (parent != null &&
                parentButtons.TryGetValue(parent, out var known))
            {
                known.Members.Add(action);
                continue;
            }

            if (group != null &&
                group != action.Group)
            {
                barItems.Add(Separator());
            }

            group = action.Group;
            if (parent != null)
            {
                List<LodeActions.Definition> members = [action];
                var dropDown = CreateDropDown(parent.Text, parent.Glyph, null, iconSize, () => ParentEntries(members), members);
                parentButtons[parent] = (dropDown, members);
                barItems.Add(dropDown);
                continue;
            }

            var button = CreateButton(action, iconSize);
            actionButtons[action.Id] = button;
            barItems.Add(button);
        }

        foreach (var item in barItems)
        {
            strip.Items.Add(item);
        }

        otherActions = LodeActions.Offered().Any(_ => !settings.ShowsAsButton(_.Id));
        seeMoreSeparator = Separator();
        strip.Items.Add(seeMoreSeparator);
        // Larger than the rest: at their size the dots are smaller than Explorer's.
        seeMore = CreateOverflowButton(window.LogicalToDeviceUnits(20));
        strip.Items.Add(seeMore);
        strip.Visible = barItems.Count > 0 || otherActions;

        strip.ResumeLayout();
        Fit();
        Update();
    }

    // As in Explorer's command bar, the buttons that don't fit go under See more, from the end of
    // the bar, and See more shows while there is anything under it.
    void Fit()
    {
        if (seeMore == null ||
            seeMoreSeparator == null)
        {
            return;
        }

        var available = strip.DisplayRectangle.Width;
        var seeMoreWidth = BarWidth(seeMoreSeparator) + BarWidth(seeMore);
        var needed = barItems.Sum(BarWidth);
        if (otherActions)
        {
            needed += seeMoreWidth;
        }

        var shown = barItems.Count;
        if (needed > available)
        {
            shown = 0;
            var used = seeMoreWidth;
            while (shown < barItems.Count &&
                   used + BarWidth(barItems[shown]) <= available)
            {
                used += BarWidth(barItems[shown]);
                shown++;
            }

            // Never a group's separator with none of the group after it.
            while (shown > 0 &&
                   barItems[shown - 1] is ToolStripSeparator)
            {
                shown--;
            }
        }

        strip.SuspendLayout();
        for (var index = 0; index < barItems.Count; index++)
        {
            barItems[index].Available = index < shown;
        }

        seeMore.Available = otherActions || shown < barItems.Count;
        seeMoreSeparator.Available = seeMore.Available && shown > 0;
        strip.ResumeLayout();

        collapsedActions.Clear();
        foreach (var (id, button) in actionButtons)
        {
            if (!button.Available)
            {
                collapsedActions.Add(id);
            }
        }

        foreach (var (button, members) in parentButtons.Values)
        {
            if (!button.Available)
            {
                collapsedActions.UnionWith(members.Select(_ => _.Id));
            }
        }
    }

    // The room an item takes on the bar, its margins included.
    static int BarWidth(ToolStripItem item) =>
        item.GetPreferredSize(Size.Empty).Width + item.Margin.Horizontal;

    // Explorer's See more: the actions not shown as buttons, in a list of their own.
    ToolStripDropDownButton CreateOverflowButton(int iconSize)
    {
        var button = new ToolStripDropDownButton("See more")
        {
            Name = "seeMore",
            Image = Icons.Bitmap(Icons.More, iconSize, SystemColors.ControlText),
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ShowDropDownArrow = false,
            ToolTipText = "See more",
            Padding = new(window.LogicalToDeviceUnits(8), window.LogicalToDeviceUnits(5), window.LogicalToDeviceUnits(8), window.LogicalToDeviceUnits(5)),
            Margin = new(window.LogicalToDeviceUnits(2), 0, window.LogicalToDeviceUnits(2), 0)
        };
        // Without an item it would not open; filled with the real ones as it opens.
        button.DropDownItems.Add("See more");
        button.DropDownOpening += (_, _) => FillOverflow(button);
        ExplorerRenderer.Style(button.DropDown);
        return button;
    }

    void FillOverflow(ToolStripDropDownButton button)
    {
        foreach (var image in dropDownImages)
        {
            image.Dispose();
        }

        dropDownImages.Clear();
        button.DropDownItems.Clear();
        var iconSize = window.LogicalToDeviceUnits(16);
        button.DropDown.ImageScalingSize = new(iconSize, iconSize);
        var below = strip.PointToScreen(new(button.Bounds.Left, button.Bounds.Bottom));
        QuickMenu.AddItems(button.DropDownItems, OverflowCommands(below), RunOnList, iconSize, dropDownImages);
    }

    /// <summary>
    /// What See more lists: each action not shown as a button, or whose button doesn't fit on the
    /// bar, in LodeActions' order with a separator between groups, as the right-click menu would
    /// offer it now, or greyed out where it does not apply. Show more options opens the full
    /// Windows menu at <paramref name="location"/>, on the screen.
    /// </summary>
    internal List<QuickMenu.Command?> OverflowCommands(Point location)
    {
        var commands = ContextCommands();
        var result = new List<QuickMenu.Command?>();
        int? group = null;
        foreach (var action in LodeActions.Offered().Where(_ => !settings.ShowsAsButton(_.Id) || collapsedActions.Contains(_.Id)))
        {
            if (group != null &&
                group != action.Group)
            {
                result.Add(null);
            }

            group = action.Group;
            if (action.Id == LodeActions.More)
            {
                result.Add(
                    LodeActions.Command(
                        action.Id,
                        () => activeView()?.ShowShellMenu(location),
                        activeView()?.CanShowShellMenu == true));
                continue;
            }

            result.Add(ActionEntry(action, commands));
        }

        return result;
    }

    // An action as the right-click menu would offer it now, out of its commands, or greyed out
    // where it does not apply.
    static QuickMenu.Command ActionEntry(LodeActions.Definition action, Dictionary<string, QuickMenu.Command> commands)
    {
        if (commands.TryGetValue(action.Id, out var command))
        {
            return command;
        }

        return LodeActions.Command(action.Id, () => { }, enabled: false);
    }

    // What a parent's drop-down on the bar lists: each action nested in it, as See more would,
    // by what it is called under its parent.
    List<QuickMenu.Command?> ParentEntries(IReadOnlyList<LodeActions.Definition> members)
    {
        var commands = ContextCommands();
        return [.. members.Select(_ => LodeActions.Nested(ActionEntry(_, commands)))];
    }

    /// <summary>
    /// What the drop-down on the bar for the parent named <paramref name="text"/> lists, for
    /// tests.
    /// </summary>
    internal List<QuickMenu.Command?> ParentEntries(string text) =>
        ParentEntries(parentButtons.Single(_ => _.Key.Text == text).Value.Members);

    // As in Explorer's command bar, drop-downs (New, Open with) keep their name beside their
    // icon; the rest show theirs as a tooltip, with their keys. A drop-down has a tooltip only
    // where it has keys, as its name shows already. Disabled buttons grey their icon themselves.
    ToolStripItem CreateButton(LodeActions.Definition action, int iconSize)
    {
        string? withKeys = null;
        if (action.Shortcut.Length > 0)
        {
            withKeys = $"{action.Text} ({action.Shortcut})";
        }

        if (action.Submenu)
        {
            return CreateDropDown(action.Text, action.Glyph, withKeys, iconSize, () => SubmenuEntries(action.Id));
        }

        var item = new ToolStripButton(action.Text)
        {
            Image = Icons.Bitmap(action.Glyph, iconSize, SystemColors.ControlText),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = withKeys ?? action.Text
        };
        item.Click += (_, _) => Run(action.Id, item);
        SpaceButton(item);
        return item;
    }

    // Spaced as Explorer's are, about 48 pixels apart at 100%, and as tall as the bar needs.
    void SpaceButton(ToolStripItem item)
    {
        item.ImageScaling = ToolStripItemImageScaling.None;
        item.Padding = new(window.LogicalToDeviceUnits(13), window.LogicalToDeviceUnits(7), window.LogicalToDeviceUnits(13), window.LogicalToDeviceUnits(7));
        item.Margin = new(window.LogicalToDeviceUnits(2), 0, window.LogicalToDeviceUnits(2), 0);
    }

    // A drop-down on the bar, for a submenu action (New, Open with) or a parent of nested
    // buttons: its name beside its icon, then a chevron, filled with <paramref name="entries"/>
    // as it opens. A parent's lists <paramref name="nested"/>, each of which can be
    // right-clicked there. Without a <paramref name="toolTip"/> it has none, rather than its
    // name again.
    ChevronDropDownButton CreateDropDown(string text, char glyph, string? toolTip, int iconSize, Func<IReadOnlyList<QuickMenu.Command?>> entries, IReadOnlyList<LodeActions.Definition>? nested = null)
    {
        var dropDown = new ChevronDropDownButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ToolTipText = toolTip,
            AutoToolTip = false,
            // Its name as far from its icon as Explorer's New is.
            Image = Icons.Bitmap(glyph, iconSize, SystemColors.ControlText, window.LogicalToDeviceUnits(6))
        };
        if (nested != null)
        {
            var list = new ActionsDropDown();
            list.MenuRequested += screen => ShowNestedMenu(dropDown, nested, screen);
            dropDown.DropDown = list;
            // A drop-down given its list leaves it to be disposed by whoever gave it.
            dropDown.Disposed += (_, _) => list.Dispose();
        }

        // Without an item it would not open; filled with the real ones as it opens.
        dropDown.DropDownItems.Add(text);
        dropDown.DropDownOpening += (_, _) => FillDropDown(dropDown, entries());
        ExplorerRenderer.Style(dropDown.DropDown);
        SpaceButton(dropDown);
        dropDown.Padding = dropDown.Padding with
        {
            Right = dropDown.Padding.Right + dropDown.ChevronWidth
        };
        return dropDown;
    }

    ToolStripSeparator Separator() =>
        new()
        {
            Margin = new(window.LogicalToDeviceUnits(4), 0, window.LogicalToDeviceUnits(4), 0)
        };

    // A submenu action's drop-down lists the submenu the right-click menu would show now.
    IReadOnlyList<QuickMenu.Command?> SubmenuEntries(string id)
    {
        if (ContextCommands().TryGetValue(id, out var command) &&
            command.Children != null)
        {
            return command.Children;
        }

        return [];
    }

    void FillDropDown(ChevronDropDownButton dropDown, IReadOnlyList<QuickMenu.Command?> entries)
    {
        foreach (var image in dropDownImages)
        {
            image.Dispose();
        }

        dropDownImages.Clear();
        dropDown.DropDownItems.Clear();
        var iconSize = window.LogicalToDeviceUnits(16);
        dropDown.DropDown.ImageScalingSize = new(iconSize, iconSize);
        QuickMenu.AddItems(dropDown.DropDownItems, entries, RunOnList, iconSize, dropDownImages);
    }

    // Back on the list first, so Rename has an item to edit and what the action opens has the
    // focus to return to.
    void RunOnList(Action action)
    {
        activeView()?.FocusList();
        action();
    }

    /// <summary>
    /// Runs an action from its button: what the right-click menu would do for the selection, or
    /// for the folder when nothing is selected.
    /// </summary>
    internal void Run(string id, ToolStripItem? button = null)
    {
        if (activeView() is not { } view)
        {
            return;
        }

        if (id == LodeActions.More)
        {
            var below = Point.Empty;
            if (button != null)
            {
                below = strip.PointToScreen(new(button.Bounds.Left, button.Bounds.Bottom));
            }

            view.ShowShellMenu(below);
            return;
        }

        if (ContextCommands().TryGetValue(id, out var command) &&
            command is { Enabled: true, Children: null })
        {
            RunOnList(command.Action);
        }
    }

    // What the buttons act on: the right-click menu's commands for the selection, then the
    // folder's, so New and Paste still work with something selected. The selection's win where
    // both have one (Terminal on a selected folder opens it). The selection is the active view's,
    // read from its list here unless the caller already has it.
    Dictionary<string, QuickMenu.Command> ContextCommands(List<FileEntry>? selection = null)
    {
        var result = new Dictionary<string, QuickMenu.Command>();
        if (activeView() is not { } view)
        {
            return result;
        }

        var commands = new List<QuickMenu.Command?>();
        selection ??= view.SelectedEntries();
        if (selection.Count > 0)
        {
            commands.AddRange(view.ItemCommands(selection));
        }

        if (!view.ListsPlaces)
        {
            // Not tied to the clipboard, which changes without telling Lode. Paste with nothing
            // to paste does nothing.
            commands.AddRange(view.BackgroundCommands(canPaste: true));
        }

        // Undo and Redo work anywhere, This PC included.
        commands.AddRange(view.UndoCommands());

        foreach (var command in commands)
        {
            if (command is { Id: { } id })
            {
                result.TryAdd(id, command);
            }
        }

        return result;
    }

    /// <summary>
    /// Enables the bar's buttons for the active view and its selection.
    /// </summary>
    public void Update() =>
        Update(null);

    /// <summary>
    /// Enables the bar's buttons for the active view and <paramref name="selection"/>, which the
    /// caller has already read from its list.
    /// </summary>
    public void Update(List<FileEntry>? selection)
    {
        if (actionButtons.Count == 0 &&
            parentButtons.Count == 0)
        {
            return;
        }

        var commands = ContextCommands(selection);
        foreach (var (id, button) in actionButtons)
        {
            if (id == LodeActions.More)
            {
                button.Enabled = activeView()?.CanShowShellMenu == true;
                continue;
            }

            button.Enabled = Applies(commands, id);
        }

        // A parent's drop-down while any action in it applies.
        foreach (var (button, members) in parentButtons.Values)
        {
            button.Enabled = members.Any(_ => Applies(commands, _.Id));
        }
    }

    // Whether the action has a command now that can run, or a submenu with one in it. One whose
    // entries are found as it opens, Open with's apps say, is taken to have some, rather than
    // their being looked up on every change of selection.
    static bool Applies(Dictionary<string, QuickMenu.Command> commands, string id) =>
        commands.TryGetValue(id, out var command) &&
        command.Enabled &&
        (command.Children == null ||
         QuickMenu.NotFoundYet(command.Children) ||
         command.Children.Any(_ => _ is { Enabled: true }));

    /// <summary>
    /// The bar's buttons that fit on it and whether each is enabled, for tests.
    /// </summary>
    internal IEnumerable<string> Describe() =>
        strip.Items
            .OfType<ToolStripItem>()
            .Where(_ => _.Available)
            .Select(Describe);

    static string Describe(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        if (item.Enabled)
        {
            return item.Text!;
        }

        return $"{item.Text} (disabled)";
    }
}
