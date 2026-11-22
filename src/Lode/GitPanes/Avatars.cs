/// <summary>
/// The pictures History shows beside each author: GitHub's avatar for the author's email, as
/// GitHub Desktop shows, only for a repository whose origin is on GitHub, so no other site's
/// authors are sent to GitHub. Each is kept on disk for a week, and in memory while Lode is in
/// use. Until it arrives, offline, or for another site, the author's initials in a circle.
/// </summary>
static class Avatars
{
    // The size asked for, drawn smaller as each row needs.
    const int size = 64;

    /// <summary>
    /// Where avatars are kept between runs. Tests put them somewhere of their own.
    /// </summary>
    internal static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lode",
        "Avatars");

    /// <summary>
    /// How long one kept on disk is used before it is asked for again: a new picture shows within
    /// a week.
    /// </summary>
    internal static TimeSpan Expiry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Downloads a url: its bytes, empty when the site has nothing there, or null when it couldn't
    /// be asked, offline say. Tests answer for the site.
    /// </summary>
    internal static Func<string, Cancel, Task<byte[]?>> Fetch { get; set; } = Download;

    /// <summary>
    /// Raised on the UI thread as each avatar arrives, for the lists to draw it.
    /// </summary>
    public static event Action? Arrived;

    // Lode stays in the tray for days: connections are made again now and then, so a change of
    // the site's address is followed.
    static HttpClient client = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All
        })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            {
                "User-Agent", "Lode"
            }
        }
    };

    // What each email's avatar is: the picture, or null for one there is none of, or that
    // couldn't be had. Only on the UI thread.
    static Dictionary<string, Bitmap?> known = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> asking = new(StringComparer.OrdinalIgnoreCase);
    static SemaphoreSlim atOnce = new(4);

    // Plenty for the authors of a few repositories, without keeping every one ever seen.
    const int mostKept = 500;

    /// <summary>
    /// GitHub's avatar for an email: by the account's number for GitHub's own private emails,
    /// id+login@users.noreply.github.com, by the login for its older ones without the number,
    /// and otherwise by the email, which GitHub looks up among its accounts' emails.
    /// </summary>
    internal static string UrlFor(string email)
    {
        const string noReply = "@users.noreply.github.com";
        if (email.EndsWith(noReply, StringComparison.OrdinalIgnoreCase))
        {
            var local = email[..^noReply.Length];
            var plus = local.IndexOf('+');
            if (plus > 0 &&
                local[..plus].All(char.IsAsciiDigit))
            {
                return $"https://avatars.githubusercontent.com/u/{local[..plus]}?s={size}";
            }

            return $"https://github.com/{Uri.EscapeDataString(local)}.png?size={size}";
        }

        return $"https://avatars.githubusercontent.com/u/e?email={Uri.EscapeDataString(email)}&s={size}";
    }

    /// <summary>
    /// Draws the author's avatar in a circle in <paramref name="bounds"/>: the picture when it is
    /// here, and, when <paramref name="fetch"/> says the repository is on GitHub, asks for one
    /// not yet asked for, <see cref="Arrived"/> saying when it comes. Initials until then.
    /// </summary>
    public static void Draw(Graphics graphics, Rectangle bounds, string name, string email, bool fetch)
    {
        Bitmap? picture = null;
        if (fetch &&
            email.Length > 0)
        {
            picture = Get(email);
        }

        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (picture != null)
            {
                using var circle = new GraphicsPath();
                circle.AddEllipse(bounds);
                var clip = graphics.Clip;
                graphics.SetClip(circle, CombineMode.Intersect);
                var interpolation = graphics.InterpolationMode;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(picture, bounds);
                graphics.InterpolationMode = interpolation;
                graphics.Clip = clip;
                return;
            }

            DrawInitials(graphics, bounds, name, email);
        }
        finally
        {
            graphics.SmoothingMode = smoothing;
        }
    }

    // The picture if it is here, otherwise null, having asked for it.
    static Bitmap? Get(string email)
    {
        if (known.TryGetValue(email, out var picture))
        {
            return picture;
        }

        if (asking.Add(email))
        {
            Load(email);
        }

        return null;
    }

    // On the UI thread, so the picture is put in place there; the disk and the site off it.
    static async void Load(string email)
    {
        var picture = await Task.Run(() => Read(email));
        asking.Remove(email);
        if (known.Count >= mostKept)
        {
            Release();
        }

        known[email] = picture;
        Arrived?.Invoke();
    }

    static async Task<Bitmap?> Read(string email)
    {
        var file = Path.Combine(Folder, FileName(email));
        try
        {
            if (File.Exists(file) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Expiry)
            {
                return Decode(await File.ReadAllBytesAsync(file));
            }
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
        }

        await atOnce.WaitAsync();
        byte[]? bytes;
        try
        {
            bytes = await Fetch(UrlFor(email), Cancel.None);
        }
        finally
        {
            atOnce.Release();
        }

        // Couldn't be asked: offline, say. Not kept on disk, so it is asked for again next run.
        if (bytes == null)
        {
            return null;
        }

        var picture = Decode(bytes);
        try
        {
            Directory.CreateDirectory(Folder);

            // Kept even when there is none, empty, so an author with no account isn't asked
            // after on every run.
            if (picture == null)
            {
                bytes = [];
            }

            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
        }

        return picture;
    }

    // A file name for an email, which says nothing of it to whoever looks in the folder.
    internal static string FileName(string email) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))) + ".img";

    static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);

            // A copy, as one made from a stream needs the stream for as long as it is drawn.
            return new(image);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static async Task<byte[]?> Download(string url, Cancel cancel)
    {
        try
        {
            using var response = await client.GetAsync(url, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancel);
        }
        catch (Exception exception)
            when (
                exception is
                    HttpRequestException or
                    TaskCanceledException or
                    IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lets go of the pictures in memory, for a window gone to the tray. They are read from disk
    /// again when next drawn.
    /// </summary>
    public static void Release()
    {
        foreach (var picture in known.Values)
        {
            picture?.Dispose();
        }

        known.Clear();
    }

    /// <summary>
    /// The author's initials, white on a colour picked from their email, so each author keeps
    /// theirs.
    /// </summary>
    internal static void DrawInitials(Graphics graphics, Rectangle bounds, string name, string email)
    {
        using var brush = new SolidBrush(ColourFor(email.Length > 0 ? email : name));
        graphics.FillEllipse(brush, bounds);
        var initials = Initials(name);
        if (initials.Length == 0)
        {
            return;
        }

        using var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, Math.Max(1, bounds.Height * 0.4f), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(
            graphics,
            initials,
            font,
            bounds,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    /// <summary>
    /// Up to two letters for a name: the first of its first and last words, or the first of
    /// a name of one word.
    /// </summary>
    internal static string Initials(string name)
    {
        var words = name.Split((char[]) [' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "";
        }

        if (words.Length == 1)
        {
            var word = words[0];
            return word[..Math.Min(1, word.Length)].ToUpperInvariant();
        }

        return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}";
    }

    // Muted enough for white on each, light or dark.
    static Color[] colours =
    [
        Color.FromArgb(0x6E, 0x40, 0xC9),
        Color.FromArgb(0x0F, 0x6C, 0xBD),
        Color.FromArgb(0x00, 0x7A, 0x6E),
        Color.FromArgb(0x4D, 0x7C, 0x0F),
        Color.FromArgb(0xB4, 0x53, 0x09),
        Color.FromArgb(0xC2, 0x41, 0x0C),
        Color.FromArgb(0xBE, 0x18, 0x5D),
        Color.FromArgb(0x5B, 0x5F, 0x97)
    ];

    // The same for an email every run, which string's own hash code isn't.
    static Color ColourFor(string key)
    {
        var hash = 2166136261u;
        foreach (var character in key.ToLowerInvariant())
        {
            hash = (hash ^ character) * 16777619u;
        }

        return colours[hash % (uint) colours.Length];
    }
}
