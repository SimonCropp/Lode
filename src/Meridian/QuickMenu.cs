/// <summary>
/// A fixed right-click menu of Meridian's own common commands. Building it touches no shell
/// extensions, so it opens at once; "Show more options" opens the full shell menu, with every
/// installed extension, as Windows 11 Explorer does. Shift+right-click goes straight there.
/// </summary>
static class QuickMenu
{
    public record Command(string Text, Action Action, bool Enabled = true);

    // A null entry is a separator.
    public static void Show(Point location, IReadOnlyList<Command?> commands, Action showMore)
    {
        if (Control.ModifierKeys.HasFlag(Keys.Shift))
        {
            showMore();
            return;
        }

        Action? chosen = null;
        var strip = Build(commands, showMore, _ => chosen = _);

        // Run the choice once the strip has closed, so a shell menu or dialog it opens is not
        // fighting the strip for focus.
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                chosen?.Invoke();
            });
        strip.Show(location);
    }

    /// <summary>
    /// The strip for the commands, with "Show more options" last. Clicking an item hands its
    /// action to <paramref name="choose"/> rather than running it.
    /// </summary>
    internal static ContextMenuStrip Build(IReadOnlyList<Command?> commands, Action showMore, Action<Action> choose)
    {
        var strip = new ContextMenuStrip();
        foreach (var command in commands)
        {
            if (command == null)
            {
                strip.Items.Add(new ToolStripSeparator());
                continue;
            }

            var item = new ToolStripMenuItem(command.Text)
            {
                Enabled = command.Enabled
            };
            item.Click += (_, _) => choose(command.Action);
            strip.Items.Add(item);
        }

        strip.Items.Add(new ToolStripSeparator());
        var more = new ToolStripMenuItem("Show more options");
        more.Click += (_, _) => choose(showMore);
        strip.Items.Add(more);
        return strip;
    }
}
