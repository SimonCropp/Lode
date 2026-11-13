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
    }

    /// <summary>
    /// The strip for the commands, with "Show more options" last unless
    /// <paramref name="showMore"/> is null. Clicking an item hands its action to
    /// <paramref name="choose"/> rather than running it.
    /// </summary>
    internal static ContextMenuStrip Build(IReadOnlyList<Command?> commands, Action? showMore, Action<Action> choose)
    {
        var strip = new ContextMenuStrip();
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
                Image = Icons.Bitmap(Icons.More, iconSize, SystemColors.MenuText)
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

            var item = new ToolStripMenuItem(command.Text)
            {
                Enabled = command.Enabled,
                Image = Image(command, iconSize, images)
            };
            if (command.Children == null)
            {
                item.Click += (_, _) => choose(command.Action);
            }
            else
            {
                // Clicking a submenu's own entry only opens it.
                AddItems(item.DropDownItems, command.Children, choose, iconSize, images);
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
