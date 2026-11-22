static class Formatting
{
    static string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Kilobytes(long bytes) =>
        $"{(bytes + 1023) / 1024:N0} KB";

    /// <summary>
    /// When a file or folder was last changed, as the Date modified column shows it: the day,
    /// month and year in full, then the time on a twelve-hour clock, "01/11/2026 01:11pm". The
    /// same whatever the culture, which would otherwise have the year in two digits and a space
    /// before a capital PM.
    /// </summary>
    public static string Modified(DateTime when)
    {
        var date = when.ToString("dd/MM/yyyy hh:mm", CultureInfo.InvariantCulture);
        if (when.Hour < 12)
        {
            return $"{date}am";
        }

        return $"{date}pm";
    }

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
