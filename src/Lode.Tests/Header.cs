/// <summary>
/// What a list's column header tells the list, for tests to tell it the same.
/// </summary>
static class Header
{
    const int notifyMessage = 0x004E;

    /// <summary>
    /// HDN_ENDTRACK, as the header sends it once a divider dragged is let go: NMHEADER, with the
    /// column, pointing to an HDITEM with the column's width.
    /// </summary>
    public static void LetGoOfDivider(ListView list, int column, int width)
    {
        var header = NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);

        // Room for the whole of HDITEMW, all of it but the mask and the width left empty.
        var item = Marshal.AllocHGlobal(128);
        var notification = Marshal.AllocHGlobal(4 * IntPtr.Size + 2 * sizeof(int));
        try
        {
            Marshal.Copy(new byte[128], 0, item, 128);
            Marshal.WriteInt32(item, (int)NativeMethods.HeaderItemWidth);
            Marshal.WriteInt32(item, sizeof(uint), width);

            // The header, an id, the code, then the column, the mouse button, and the HDITEM.
            Marshal.WriteIntPtr(notification, header);
            Marshal.WriteIntPtr(notification, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(notification, 2 * IntPtr.Size, NativeMethods.HeaderEndTrack);
            Marshal.WriteInt32(notification, 3 * IntPtr.Size, column);
            Marshal.WriteInt32(notification, 3 * IntPtr.Size + sizeof(int), 0);
            Marshal.WriteIntPtr(notification, 3 * IntPtr.Size + 2 * sizeof(int), item);
            NativeMethods.SendMessage(list.Handle, notifyMessage, IntPtr.Zero, notification);
        }
        finally
        {
            Marshal.FreeHGlobal(notification);
            Marshal.FreeHGlobal(item);
        }
    }
}
