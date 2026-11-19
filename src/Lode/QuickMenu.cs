/// <summary>
/// A fixed right-click menu of Lode's own common commands. Building it touches no shell
/// extensions, so it opens at once; "Show more options" opens the full shell menu, with every
/// installed extension, as Windows 11 Explorer does. Shift+right-click goes straight there.
/// </summary>
static class QuickMenu
{
    public record Command(string Text, Action Action, bool Enabled = true)
    {
        /// <summary>
        /// Entries shown in a submenu of this one, in which case <see cref="Action"/> is unused.
        /// A null entry is a separator.
        /// </summary>
        public IReadOnlyList<Command?>? Children { get; init; }

        /// <summary>
        /// The action in <see cref="LodeActions"/> this is, so Options can leave it off the
        /// menu and the actions bar's button for it can find it. Null for entries that are only
        /// part of a submenu.
        /// </summary>
        public string? Id { get; init; }

        /// <summary>
        /// The icon beside it, a Segoe Fluent Icons glyph.
        /// </summary>
        public char? Glyph { get; init; }

        /// <summary>
        /// The icon beside it, from the system image list: a file type's own icon.
        /// </summary>
        public int? ShellIcon { get; init; }

        /// <summary>
        /// The keys that do the same, shown at the right of the entry.
        /// </summary>
        public string? Shortcut { get; init; }

        /// <summary>
        /// Shown ticked, for a setting the entry turns on and off.
        /// </summary>
        public bool Checked { get; init; }

        /// <summary>
        /// What it is called in its parent's submenu or drop-down, where the parent's name says
        /// the rest: Pull, under Git. Null where it is called <see cref="Text"/> there too.
        /// </summary>
        public string? NestedText { get; init; }
    }

    /// <summary>
    /// An entry that opens a submenu of <paramref name="children"/>.
    /// </summary>
    public static Command Submenu(string text, IReadOnlyList<Command?> children) =>
        new(text, () => { })
        {
            Children = children
        };

    /// <summary>
    /// A submenu's entries, found the first time something reads them: when the submenu opens,
    /// rather than as the menu it is in is built, or each time the actions bar builds the
    /// commands for a new selection.
    /// </summary>
    public static IReadOnlyList<Command?> Deferred(Func<IReadOnlyList<Command?>> find) =>
        new DeferredCommands(find);

    /// <summary>
    /// Whether a submenu's entries are found only when something reads them, and have not been
    /// yet: taken to have some until then.
    /// </summary>
    public static bool NotFoundYet(IReadOnlyList<Command?> commands) =>
        commands is DeferredCommands { Found: false };

    sealed class DeferredCommands(Func<IReadOnlyList<Command?>> find) : IReadOnlyList<Command?>
    {
        Lazy<IReadOnlyList<Command?>> commands = new(find, LazyThreadSafetyMode.None);

        public bool Found => commands.IsValueCreated;

        public int Count => commands.Value.Count;

        public Command? this[int index] => commands.Value[index];

        public IEnumerator<Command?> GetEnumerator() =>
            commands.Value.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    /// <summary>
    /// Shows the menu at <paramref name="location"/>, in screen coordinates, owned by
    /// <paramref name="owner"/> so it closes when that window loses focus or hides. Unowned, it
    /// could be left on screen. A null entry is a separator. Shift+right-click runs
    /// <paramref name="showMore"/> straight away, even when <paramref name="offerMore"/> keeps
    /// it off the menu.
    /// </summary>
    public static void Show(Control owner, Point location, IReadOnlyList<Command?> commands, Action showMore, bool offerMore = true)
    {
        if (Control.ModifierKeys.HasFlag(Keys.Shift))
        {
            showMore();
            return;
        }

        Action? chosen = null;
        var strip = Build(commands, offerMore ? showMore : null, _ => chosen = _);

        // Run the choice once the strip has closed, so a shell menu or dialog it opens is not
        // fighting the strip for focus.
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                chosen?.Invoke();
            });
        strip.Show(owner, owner.PointToClient(location));

        // After it shows, which is at once: the shell's menu is readied behind it.
        ShellContextMenu.MenuOpened();
    }

    /// <summary>
    /// The strip for the commands, with "Show more options" last unless
    /// <paramref name="showMore"/> is null. Clicking an item hands its action to
    /// <paramref name="choose"/> rather than running it.
    /// </summary>
    internal static ContextMenuStrip Build(IReadOnlyList<Command?> commands, Action? showMore, Action<Action> choose)
    {
        var strip = new ExplorerMenu();
        var iconSize = strip.LogicalToDeviceUnits(16);
        strip.ImageScalingSize = new(iconSize, iconSize);
        var images = new List<Image>();
        AddItems(strip.Items, commands, choose, iconSize, images);

        if (showMore != null)
        {
            if (strip.Items.Count > 0)
            {
                strip.Items.Add(new ToolStripSeparator());
            }

            var more = new ToolStripMenuItem("Show more options")
            {
                Image = Icons.Bitmap(Icons.MoreOptions, iconSize, SystemColors.MenuText)
            };
            more.Click += (_, _) => choose(showMore);
            strip.Items.Add(more);
        }

        // File type icons are drawn for each menu; glyphs are cached and shared.
        strip.Disposed += (_, _) =>
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        };
        return strip;
    }

    /// <summary>
    /// Menu items for the commands, with their icons, into <paramref name="items"/>.
    /// <paramref name="images"/> collects the icons drawn for these items alone, for the caller
    /// to dispose once the items are gone.
    /// </summary>
    internal static void AddItems(ToolStripItemCollection items, IReadOnlyList<Command?> commands, Action<Action> choose, int iconSize, List<Image> images)
    {
        foreach (var command in commands)
        {
            if (command == null)
            {
                items.Add(new ToolStripSeparator());
                continue;
            }

            // An ampersand would mark the next letter as the item's access key and not show:
            // "Snip & Sketch", or a folder named "R&D".
            var item = new ToolStripMenuItem(command.Text.Replace("&", "&&"))
            {
                Enabled = command.Enabled,
                Image = Image(command, iconSize, images),
                ShortcutKeyDisplayString = command.Shortcut,
                Checked = command.Checked
            };
            if (command.Children == null)
            {
                item.Click += (_, _) => choose(command.Action);
            }
            else if (NotFoundYet(command.Children))
            {
                // Found as it opens, Open with's apps say, rather than on every right-click.
                // Until then it holds a stand-in, without which it would not open.
                var children = command.Children;
                var filled = false;
                item.DropDownItems.Add(new ToolStripMenuItem("Finding…") { Enabled = false });
                item.DropDownOpening += (_, _) =>
                {
                    if (filled)
                    {
                        return;
                    }

                    filled = true;
                    item.DropDownItems.Clear();
                    AddItems(item.DropDownItems, children, choose, iconSize, images);
                    if (item.DropDownItems.Count == 0)
                    {
                        item.DropDownItems.Add(new ToolStripMenuItem("None") { Enabled = false });
                    }
                };
            }
            else
            {
                // Clicking a submenu's own entry only opens it. One with nothing in it has
                // nothing to open.
                AddItems(item.DropDownItems, command.Children, choose, iconSize, images);
                item.Enabled = command.Enabled && command.Children.Count > 0;
            }

            items.Add(item);
        }
    }

    static Image? Image(Command command, int size, List<Image> images)
    {
        if (command.Glyph is { } glyph)
        {
            return Icons.Bitmap(glyph, size, SystemColors.MenuText);
        }

        if (command.ShellIcon is { } shellIcon)
        {
            var image = ShellIcons.Render(shellIcon, new(size, size), Point.Empty);
            images.Add(image);
            return image;
        }

        return null;
    }
}
