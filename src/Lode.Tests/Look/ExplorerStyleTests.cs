public class ExplorerStyleTests
{
    // Windows' own popup menus, Show more options among them, are drawn dark when Lode is, as
    // Explorer's are, and as Windows has them otherwise. Windows Forms leaves them light: they
    // go dark only for a process that asks.
    [Test]
    [NotInParallel("menuTheme")]
    public async Task WindowsMenusAreDarkWhenLodeIs()
    {
        var original = ExplorerStyle.UseDarkMenus(true);
        try
        {
            var afterDark = ExplorerStyle.UseDarkMenus(false);
            var afterLight = ExplorerStyle.UseDarkMenus(false);

            await Assert.That(afterDark).IsEqualTo(ExplorerStyle.DarkMenus);
            await Assert.That(afterLight).IsEqualTo(ExplorerStyle.DefaultMenus);
        }
        finally
        {
            NativeMethods.SetPreferredAppMode(original);
            NativeMethods.FlushMenuThemes();
        }
    }

    // With high contrast on, a button under the mouse, pressed or ticked, and a menu's entry
    // under the mouse, are filled with the system's highlight, its text in the colour for text
    // on it, as a faint shade would be lost on a black background. Without, a shade, the text as
    // it was.
    [Test]
    [STAThreadExecutor]
    [NotInParallel("windows")]
    public async Task HighlightsWithHighContrast()
    {
        var highContrast = ExplorerStyle.HighContrast;
        try
        {
            (int Fill, int Text) Drawn()
            {
                using var bar = new ToolStrip
                {
                    Renderer = new ExplorerRenderer()
                };
                var button = new ToolStripButton("Copy")
                {
                    Checked = true,
                    AutoSize = false,
                    Size = new(60, 30)
                };
                bar.Items.Add(button);
                using var bitmap = new Bitmap(60, 30);
                using var graphics = Graphics.FromImage(bitmap);
                bar.Renderer.DrawButtonBackground(new(graphics, button));
                var text = new ToolStripItemTextRenderEventArgs(graphics, button, "Copy", new(0, 0, 60, 30), SystemColors.ControlText, bar.Font, TextFormatFlags.Default);
                bar.Renderer.DrawItemText(text);
                return (bitmap.GetPixel(3, 15).ToArgb(), text.TextColor.ToArgb());
            }

            ExplorerStyle.HighContrast = () => true;
            var on = Drawn();
            var highlighted = (ExplorerStyle.Highlighted(Color.White).ToArgb(), ExplorerStyle.HighlightedText(Color.Black).ToArgb());
            ExplorerStyle.HighContrast = () => false;
            var off = Drawn();

            await Assert.That(on).IsEqualTo((SystemColors.Highlight.ToArgb(), SystemColors.HighlightText.ToArgb()));
            await Assert.That(highlighted).IsEqualTo((SystemColors.Highlight.ToArgb(), SystemColors.HighlightText.ToArgb()));
            await Assert.That(off.Text).IsEqualTo(SystemColors.ControlText.ToArgb());
            await Assert.That(off.Fill).IsEqualTo(ExplorerStyle.Pressed(ExplorerStyle.Window).ToArgb());
        }
        finally
        {
            ExplorerStyle.HighContrast = highContrast;
        }
    }
}
