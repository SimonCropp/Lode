using System.Net;

/// <summary>
/// What nuget.org says the newest version of a package is, asked over HTTP as its API
/// documents. Not through NuGet's client libraries: Lode asks once a day and then sits in the
/// tray for days, and those, once loaded, stay loaded.
/// </summary>
static class NuGetFeed
{
    const string serviceIndex = "https://api.nuget.org/v3/index.json";

    // The registrations that hold every version, SemVer 2 ones included, as a prerelease with
    // a dotted label is.
    const string registrationsType = "RegistrationsBaseUrl/3.6.0";

    /// <summary>
    /// The newest listed version of the package, prereleases included, or null when it has none
    /// or there is no such package.
    /// </summary>
    public static async Task<NuGetVersion?> LatestVersion(string packageId, Cancel cancel)
    {
        // Those registrations are always sent compressed.
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler);
        return await LatestVersion(packageId, _ => Fetch(client, _, cancel));
    }

    static async Task<JsonDocument?> Fetch(HttpClient client, string url, Cancel cancel)
    {
        using var response = await client.GetAsync(url, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
    }

    /// <param name="fetch">The JSON at a URL, or null where there is nothing. Given in tests.</param>
    internal static async Task<NuGetVersion?> LatestVersion(string packageId, Func<string, Task<JsonDocument?>> fetch)
    {
        using var services = await fetch(serviceIndex);
        var registrations = RegistrationsUrl(services) ??
                            throw new InvalidOperationException("nuget.org offered no package metadata.");

        using var package = await fetch($"{registrations.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json");
        if (package == null)
        {
            return null;
        }

        // Pages of versions, oldest first. The newest listed version is in the last page that
        // has one listed. A package with few versions has its pages in the index itself; one
        // with many has each fetched.
        var pages = package.RootElement.GetProperty("items");
        for (var index = pages.GetArrayLength() - 1; index >= 0; index--)
        {
            var page = pages[index];
            NuGetVersion? newest;
            if (page.TryGetProperty("items", out var versions))
            {
                newest = Newest(versions);
            }
            else
            {
                using var fetched = await fetch(page.GetProperty("@id").GetString()!);
                if (fetched == null)
                {
                    continue;
                }

                newest = Newest(fetched.RootElement.GetProperty("items"));
            }

            if (newest != null)
            {
                return newest;
            }
        }

        return null;
    }

    static string? RegistrationsUrl(JsonDocument? services)
    {
        if (services == null)
        {
            return null;
        }

        foreach (var resource in services.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (resource.GetProperty("@type").ValueKind == JsonValueKind.String &&
                resource.GetProperty("@type").GetString() == registrationsType)
            {
                return resource.GetProperty("@id").GetString();
            }
        }

        return null;
    }

    // The newest of a page's versions that is listed: one unlisted is still there, but not
    // offered, and dotnet tool update would not install it.
    static NuGetVersion? Newest(JsonElement versions)
    {
        NuGetVersion? newest = null;
        foreach (var item in versions.EnumerateArray())
        {
            var entry = item.GetProperty("catalogEntry");
            if (entry.TryGetProperty("listed", out var listed) &&
                listed.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            if (NuGetVersion.TryParse(entry.GetProperty("version").GetString(), out var version) &&
                (newest == null || version > newest))
            {
                newest = version;
            }
        }

        return newest;
    }
}
