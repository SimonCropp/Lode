/// <summary>
/// A later launch handing its folders to the running copy.
/// </summary>
public class SingleInstanceTests
{
    // A launch while the running copy is still starting up, two folders opened together, say,
    // hands over at once, rather than giving up after waiting and running beside it. What it
    // handed over is kept until the copy can open it. The mutex is the thread's that took it,
    // so everything up to letting it go happens on the one thread.
    [Test]
    public async Task HandsOverWhileTheRunningCopyStartsUp()
    {
        var name = $"Lode-Tests-{Guid.NewGuid()}";
        var opened = new List<string[]>();
        bool isFirst;
        bool secondIsFirst;
        bool handedOver;
        TimeSpan took;
        using (var running = new SingleInstance(name))
        {
            isFirst = running.IsFirst;
            running.Listen();

            using (var later = new SingleInstance(name))
            {
                secondIsFirst = later.IsFirst;
                var stopwatch = Stopwatch.StartNew();
                handedOver = later.TryHandOff([@"C:\Code\Lode", @"D:\Two Words"]);
                took = stopwatch.Elapsed;
            }

            // The window is up: what was kept is handed to it.
            var deadline = Stopwatch.StartNew();
            running.HandTo(opened.Add);
            while (opened.Count == 0 &&
                   deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }
        }

        await Assert.That(isFirst).IsTrue();
        await Assert.That(secondIsFirst).IsFalse();
        await Assert.That(handedOver).IsTrue();
        await Assert.That(took).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(opened.SelectMany(_ => _)).IsEquivalentTo(
            [@"C:\Code\Lode", @"D:\Two Words"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
