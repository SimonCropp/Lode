/// <summary>
/// Sizes a window as Windows itself does, by its maximum size. WinForms keeps a window no wider
/// than the screen whatever its maximum size, so on CI's 1024 wide screen one asked to be 1200
/// wide by its Size would be 1044, where the panes have no room beside the list. Windows goes by
/// the maximum size, so a window that has one big enough is as wide as asked.
/// </summary>
static class WindowSize
{
    /// <summary>
    /// <paramref name="width"/> and <paramref name="height"/> are in the window's own pixels.
    /// Its handle is made if it hasn't one yet, so it can be sized before it shows.
    /// </summary>
    public static void Set(Form form, int width, int height)
    {
        const uint noMove = 0x2;
        const uint noZOrder = 0x4;
        const uint noActivate = 0x10;
        SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, width, height, noMove | noZOrder | noActivate);
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
