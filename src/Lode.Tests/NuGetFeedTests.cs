using System.Text.Json;
using NuGet.Versioning;

/// <summary>
/// The newest version nuget.org has of a package, from its answers as its API documents them,
/// which are given here rather than asked for.
/// </summary>
public class NuGetFeedTests
{
    const string services =
        """
        {
          "version": "3.0.0",
          "resources": [
            { "@id": "https://feed.test/registration-semver1/", "@type": "RegistrationsBaseUrl" },
            { "@id": "https://feed.test/registration/", "@type": "RegistrationsBaseUrl/3.6.0" },
            { "@id": "https://feed.test/query", "@type": "SearchQueryService" }
          ]
        }
        """;

    // A package with few versions has them in its index. The newest listed one is the answer,
    // a prerelease included; one unlisted is passed over, newer or not.
    [Test]
    public async Task TheNewestListedVersionPrereleasesIncluded()
    {
        var latest = await Latest(
            "Lode",
            new()
            {
                ["https://feed.test/registration/lode/index.json"] =
                    $$"""
                      {
                        "items": [
                          {
                            "@id": "https://feed.test/registration/lode/index.json#page/0.1.0/0.4.0",
                            "items": [
                              {{Version("0.1.0")}},
                              {{Version("0.3.0")}},
                              {{Version("0.4.0-beta.2")}},
                              {{Version("0.4.0", listed: false)}}
                            ]
                          }
                        ]
                      }
                      """
            });

        await Assert.That(latest).IsEqualTo(NuGetVersion.Parse("0.4.0-beta.2"));
    }

    // A package with many versions has them in pages of their own, oldest first. They are
    // fetched from the last back, as far as the first with a version that is listed.
    [Test]
    public async Task PagesAreFetchedFromTheLastBack()
    {
        var asked = new List<string>();
        var latest = await Latest(
            "Lode",
            new()
            {
                ["https://feed.test/registration/lode/index.json"] =
                    """
                    {
                      "items": [
                        { "@id": "https://feed.test/registration/lode/page/first.json" },
                        { "@id": "https://feed.test/registration/lode/page/second.json" },
                        { "@id": "https://feed.test/registration/lode/page/third.json" }
                      ]
                    }
                    """,
                ["https://feed.test/registration/lode/page/first.json"] = Page(Version("0.1.0")),
                ["https://feed.test/registration/lode/page/second.json"] = Page(Version("1.0.0"), Version("1.1.0"), Version("1.2.0", listed: false)),
                ["https://feed.test/registration/lode/page/third.json"] = Page(Version("2.0.0", listed: false))
            },
            asked);

        await Assert.That(latest).IsEqualTo(NuGetVersion.Parse("1.1.0"));
        await Assert.That(asked).IsEquivalentTo(
        [
            "https://api.nuget.org/v3/index.json",
            "https://feed.test/registration/lode/index.json",
            "https://feed.test/registration/lode/page/third.json",
            "https://feed.test/registration/lode/page/second.json"
        ]);
    }

    [Test]
    public async Task NoSuchPackage()
    {
        var latest = await Latest("Lode", []);

        await Assert.That(latest).IsNull();
    }

    [Test]
    public async Task NoListedVersion()
    {
        var latest = await Latest(
            "Lode",
            new()
            {
                ["https://feed.test/registration/lode/index.json"] =
                    $$"""{ "items": [ { "@id": "page", "items": [ {{Version("0.1.0", listed: false)}} ] } ] }"""
            });

        await Assert.That(latest).IsNull();
    }

    // Without the registrations that hold every version there is nothing to go by, and that is
    // a failed check, not a package with nothing newer.
    [Test]
    public async Task AFeedWithoutRegistrationsFails()
    {
        Task<JsonDocument?> Fetch(string url) =>
            Task.FromResult<JsonDocument?>(JsonDocument.Parse("""{ "resources": [ { "@id": "https://feed.test/query", "@type": "SearchQueryService" } ] }"""));

        await Assert.That(() => NuGetFeed.LatestVersion("Lode", Fetch)).Throws<InvalidOperationException>();
    }

    // The newest version of a package, from these answers by URL, with nuget.org's own index
    // of its services added. Any other URL has nothing at it.
    static Task<NuGetVersion?> Latest(string packageId, Dictionary<string, string> answers, List<string>? asked = null)
    {
        answers["https://api.nuget.org/v3/index.json"] = services;
        return NuGetFeed.LatestVersion(
            packageId,
            url =>
            {
                asked?.Add(url);
                if (answers.TryGetValue(url, out var json))
                {
                    return Task.FromResult<JsonDocument?>(JsonDocument.Parse(json));
                }

                return Task.FromResult<JsonDocument?>(null);
            });
    }

    static string Page(params string[] versions) =>
        $$"""{ "items": [ {{string.Join(", ", versions)}} ] }""";

    static string Version(string version, bool listed = true) =>
        $$"""{ "catalogEntry": { "id": "Lode", "version": "{{version}}", "listed": {{(listed ? "true" : "false")}} } }""";
}
