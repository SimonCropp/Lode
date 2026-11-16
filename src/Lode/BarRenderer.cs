/// <summary>
/// The command bar and status bar drawn as Explorer's are: flat white, the command bar with a
/// line along its foot between it and the panes. Their buttons, and the menus that drop from
/// them, are drawn as Windows draws a toolbar's, as before.
/// </summary>
sealed class BarRenderer : ToolStripSystemRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
    {
        if (eventArgs.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBackground(eventArgs);
            return;
        }

        eventArgs.Graphics.FillRectangle(SystemBrushes.Window, eventArgs.AffectedBounds);
    }

    // In place of the raised edge Windows draws round a toolbar.
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
    {
        if (eventArgs.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(eventArgs);
            return;
        }

        if (eventArgs.ToolStrip is StatusStrip)
        {
            return;
        }

        var strip = eventArgs.ToolStrip;
        using var pen = new Pen(ExplorerStyle.Divider);
        eventArgs.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
    }
}
