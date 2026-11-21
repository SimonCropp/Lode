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
}
