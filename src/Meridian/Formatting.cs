static class Formatting
{
    static readonly string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Kilobytes(long bytes) =>
        $"{(bytes + 1023) / 1024:N0} KB";

    public static string Size(long bytes)
    {
        if (bytes == 1)
        {
            return "1 byte";
        }

        if (bytes < 1024)
        {
            return $"{bytes:N0} bytes";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "N0" : value >= 10 ? "N1" : "N2";
        return $"{value.ToString(format)} {units[unit]}";
    }
}
