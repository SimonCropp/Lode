using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Meridian.Interop;

static class ShellApi
{
    internal static ImageSource? GetIcon(string path, bool isDirectory, bool small)
    {
        var hIcon = isDirectory
            ? NativeMethods.GetFolderIcon(small)
            : NativeMethods.GetFileIcon(path, small);

        if (hIcon == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.ReleaseIcon(hIcon);
        }
    }
}
