/// <summary>
/// Every message box and task dialog Lode puts up, put up through here, so that tests can stop
/// one rather than wait on it. A box is modal, waiting for a click, and in a test run there is
/// nobody to click, so the run waited with it for ever. A test that expects a box answers it
/// through the stand-in Lode asks it through, such as ChangesPane.AskDiscard, which then never
/// reaches here.
/// </summary>
static class MessageBoxes
{
    public static DialogResult Show(
        IWin32Window? owner,
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Showing(owner, text, caption, buttons, icon, defaultButton);

    /// <summary>
    /// Owned by the window active on this thread, if any, as MessageBox.Show without an owner is.
    /// </summary>
    public static DialogResult Show(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Showing(null, text, caption, buttons, icon, defaultButton);

    public static TaskDialogButton ShowDialog(
        IWin32Window owner,
        TaskDialogPage page,
        TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner) =>
        ShowingDialog(owner, page, startupLocation);

    /// <summary>
    /// Owned by no window, as TaskDialog.ShowDialog without an owner is.
    /// </summary>
    public static TaskDialogButton ShowDialog(
        TaskDialogPage page,
        TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner) =>
        ShowingDialog(null, page, startupLocation);

    /// <summary>
    /// Puts up a message box, and answers which button closed it. Tests fail on it instead.
    /// </summary>
    internal static Func<IWin32Window?, string, string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult> Showing { get; set; } =
        (owner, text, caption, buttons, icon, defaultButton) =>
            MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);

    /// <summary>
    /// Puts up a task dialog, and answers which button closed it. Tests fail on it instead, but
    /// for those that click a dialog's buttons themselves, which put up <see cref="ShowDialogNow"/>.
    /// </summary>
    internal static Func<IWin32Window?, TaskDialogPage, TaskDialogStartupLocation, TaskDialogButton> ShowingDialog { get; set; } =
        ShowDialogNow;

    internal static TaskDialogButton ShowDialogNow(IWin32Window? owner, TaskDialogPage page, TaskDialogStartupLocation startupLocation)
    {
        if (owner == null)
        {
            return TaskDialog.ShowDialog(page, startupLocation);
        }

        return TaskDialog.ShowDialog(owner, page, startupLocation);
    }
}
