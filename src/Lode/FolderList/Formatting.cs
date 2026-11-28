static class Formatting
{
    static string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Kilobytes(long bytes) =>
        $"{(bytes + 1023) / 1024:N0} KB";

    /// <summary>
    /// The culture whose short date the Date modified column follows: the user's, as Windows'
    /// region settings have it. Tests pin it.
    /// </summary>
    internal static Func<CultureInfo> DateCulture { get; set; } = () => CultureInfo.CurrentCulture;

    /// <summary>
    /// When a file or folder was last changed, as the Date modified column shows it: the date in
    /// the order the region writes it, its year in full, then the time on a twelve-hour clock:
    /// "01/11/2026 01:11pm" in Britain, "11/1/2026 01:11pm" in the United States. The time is the
    /// same whatever the culture, which would otherwise have a space before a capital PM.
    /// </summary>
    public static string Modified(DateTime when) =>
        Modified(when, DateCulture());

    /// <summary>
    /// As <see cref="Modified(DateTime)"/>, with the date as <paramref name="culture"/> writes it.
    /// </summary>
    internal static string Modified(DateTime when, CultureInfo culture)
    {
        var date = when.ToString(DateFormat(culture), culture);
        var time = when.ToString("hh:mm", CultureInfo.InvariantCulture);
        if (when.Hour < 12)
        {
            return $"{date} {time}am";
        }

        return $"{date} {time}pm";
    }

    // A culture's short date, with the year in four digits where it has two, kept for the culture
    // asked for last, as the column asks for every row's.
    sealed record DateFormatOf(CultureInfo Culture, string Format);

    static DateFormatOf? lastDateFormat;

    static string DateFormat(CultureInfo culture)
    {
        if (lastDateFormat is { } last &&
            ReferenceEquals(last.Culture, culture))
        {
            return last.Format;
        }

        var format = FullYear(culture.DateTimeFormat.ShortDatePattern);
        lastDateFormat = new(culture, format);
        return format;
    }

    // A date pattern with each run of y, the year in however many digits, as yyyy, leaving what
    // is quoted, or escaped, as it is.
    internal static string FullYear(string pattern)
    {
        var full = new StringBuilder(pattern.Length + 2);
        var quote = '\0';
        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];
            if (quote != '\0')
            {
                full.Append(character);
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (character)
            {
                case '\'' or '"':
                    quote = character;
                    full.Append(character);
                    break;
                case '\\' when i + 1 < pattern.Length:
                    full.Append(character).Append(pattern[++i]);
                    break;
                case 'y':
                    while (i + 1 < pattern.Length &&
                           pattern[i + 1] == 'y')
                    {
                        i++;
                    }

                    full.Append("yyyy");
                    break;
                default:
                    full.Append(character);
                    break;
            }
        }

        return full.ToString();
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
