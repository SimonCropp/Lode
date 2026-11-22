[NotInParallel("avatars")]
public class AvatarsTests
{
    // By the account's number for GitHub's private emails, by the login for its older ones, and
    // otherwise by the email itself.
    [Test]
    public async Task UrlFor()
    {
        await Assert.That(Avatars.UrlFor("12345+octo@users.noreply.github.com")).IsEqualTo("https://avatars.githubusercontent.com/u/12345?s=64");
        await Assert.That(Avatars.UrlFor("octo@users.noreply.github.com")).IsEqualTo("https://github.com/octo.png?size=64");
        await Assert.That(Avatars.UrlFor("ada+lode@example.com")).IsEqualTo("https://avatars.githubusercontent.com/u/e?email=ada%2Blode%40example.com&s=64");
    }

    [Test]
    public async Task Initials()
    {
        await Assert.That(Avatars.Initials("Ada Lovelace")).IsEqualTo("AL");
        await Assert.That(Avatars.Initials("Grace Brewster Murray Hopper")).IsEqualTo("GH");
        await Assert.That(Avatars.Initials("octocat")).IsEqualTo("O");
        await Assert.That(Avatars.Initials("dependabot[bot]")).IsEqualTo("D");
        await Assert.That(Avatars.Initials("")).IsEqualTo("");
    }

    // The file kept for an email says nothing of it, and is the same however it is written.
    [Test]
    public async Task FileName()
    {
        await Assert.That(Avatars.FileName("Ada@Example.com")).IsEqualTo(Avatars.FileName("ada@example.com "));
        await Assert.That(Avatars.FileName("ada@example.com")).DoesNotContain("ada");
    }

    // Asked for once, an avatar is drawn from memory; asked for in another run, from the disk,
    // without asking the site again. One the site has none of is kept as none, so isn't asked
    // after again either.
    [Test]
    [STAThreadExecutor]
    public async Task KeptOnDisk()
    {
        using var fixture = Fixture.Create();
        var folder = Avatars.Folder;
        var fetch = Avatars.Fetch;
        var asked = new List<string>();
        Avatars.Folder = fixture.At("Avatars");
        Avatars.Fetch = (url, _) =>
        {
            asked.Add(url);
            if (url.Contains("nobody", StringComparison.Ordinal))
            {
                return Task.FromResult<byte[]?>([]);
            }

            using var picture = new Bitmap(8, 8);
            using var stream = new MemoryStream();
            picture.Save(stream, ImageFormat.Png);
            return Task.FromResult<byte[]?>(stream.ToArray());
        };
        var arrived = 0;
        void OnArrived() => arrived++;
        Avatars.Arrived += OnArrived;
        try
        {
            using var canvas = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(canvas);
            Avatars.Draw(graphics, new(0, 0, 16, 16), "Ada Lovelace", "ada@example.com", fetch: true);
            Avatars.Draw(graphics, new(0, 0, 16, 16), "No Body", "nobody@example.com", fetch: true);
            await Pump.Until(() => arrived == 2);

            Avatars.Release();
            Avatars.Draw(graphics, new(0, 0, 16, 16), "Ada Lovelace", "ada@example.com", fetch: true);
            Avatars.Draw(graphics, new(0, 0, 16, 16), "No Body", "nobody@example.com", fetch: true);
            await Pump.Until(() => arrived == 4);

            await Assert.That(asked.Count).IsEqualTo(2);
            await Assert.That(Directory.GetFiles(Avatars.Folder).Length).IsEqualTo(2);
        }
        finally
        {
            Avatars.Arrived -= OnArrived;
            Avatars.Release();
            Avatars.Folder = folder;
            Avatars.Fetch = fetch;
        }
    }

    // Not on GitHub, nothing is asked for: initials alone.
    [Test]
    public async Task NotFetchedOffGitHub()
    {
        var fetch = Avatars.Fetch;
        var asked = 0;
        Avatars.Fetch = (_, _) =>
        {
            asked++;
            return Task.FromResult<byte[]?>(null);
        };
        try
        {
            using var canvas = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(canvas);
            Avatars.Draw(graphics, new(0, 0, 16, 16), "Ada Lovelace", "elsewhere@example.com", fetch: false);

            await Assert.That(asked).IsEqualTo(0);
        }
        finally
        {
            Avatars.Fetch = fetch;
        }
    }
}
