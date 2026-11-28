/// <summary>
/// Test runs at the same time, from two checkouts say, taking turns at the folders their
/// fixtures share. A turn is a mutex, so turns taken on two threads here contend as two runs'
/// do.
/// </summary>
public class FolderTurnTests
{
    // A second turn at a folder waits for the first to be let go, then has it. Let go from a
    // thread other than the one that took it, as a test's Dispose may be.
    [Test]
    public async Task ASecondWaitsForTheFirst()
    {
        var folder = Folder();
        var first = new FolderTurn(folder);
        var second = Task.Run(() => new FolderTurn(folder));
        await Task.Delay(300);
        var waited = !second.IsCompleted;

        await Task.Run(first.Dispose);
        using var secondTurn = await second.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(waited).IsTrue();
    }

    // Turns at different folders don't wait on each other.
    [Test]
    public async Task OthersDontWait()
    {
        using var first = new FolderTurn(Folder());
        using var second = await Task.Run(() => new FolderTurn(Folder())).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A run that ends with its turn, killed say, never lets go of the mutex: Windows lets the
    // next in, rather than it waiting for ever.
    [Test]
    public async Task ARunThatEndsWithItsTurnLetsTheNextIn()
    {
        var folder = Folder();
        var ended = new Thread(() => new Mutex(false, FolderTurn.Name(folder)).WaitOne());
        ended.Start();
        ended.Join();

        using var turn = await Task.Run(() => new FolderTurn(folder)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A fixture another run is building at the same path isn't deleted and built again under
    // it: the second waits, then builds its own.
    [Test]
    public async Task AFixtureWaitsForAnotherAtTheSamePath()
    {
        var member = $"Same.{Guid.NewGuid():N}";
        var first = Fixture.Create(nameof(FolderTurnTests), member);
        var marker = first.At("Docs", "marker.aleph");
        await File.WriteAllTextAsync(marker, "first run's");
        var second = Task.Run(() => Fixture.Create(nameof(FolderTurnTests), member));
        await Task.Delay(300);
        var waited = !second.IsCompleted;
        var kept = File.Exists(marker);

        first.Dispose();
        using var secondFixture = await second.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(waited).IsTrue();
        await Assert.That(kept).IsTrue();
        await Assert.That(secondFixture.Root).IsEqualTo(first.Root);
        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(File.Exists(secondFixture.At("Docs", "guide.aleph"))).IsTrue();
    }

    static string Folder() =>
        Path.Combine(Path.GetTempPath(), "LodeTests", $"Turn.{Guid.NewGuid():N}");
}
