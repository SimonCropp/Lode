public class PinnedOrderTests
{
    static readonly string[] pinned = [@"C:\A", @"C:\B", @"C:\C", @"C:\D"];

    [Test]
    public Task Move() =>
        Verify(
            new
            {
                cToFirst = PinnedOrder.Move(pinned, @"C:\C", 0),
                aToLast = PinnedOrder.Move(pinned, @"C:\A", 3),
                bAfterC = PinnedOrder.Move(pinned, @"C:\B", 2),
                pastTheEnd = PinnedOrder.Move(pinned, @"C:\A", 10)
            });

    // Everything from the first folder out of place is unpinned, then pinned again in order.
    [Test]
    public Task Steps() =>
        Verify(
            new
            {
                cToFirst = Describe(PinnedOrder.Steps(pinned, PinnedOrder.Move(pinned, @"C:\C", 0))),
                dUpOne = Describe(PinnedOrder.Steps(pinned, PinnedOrder.Move(pinned, @"C:\D", 2))),
                unchanged = Describe(PinnedOrder.Steps(pinned, pinned))
            });

    static IEnumerable<string> Describe(List<(string Path, bool Pin)> steps) =>
        steps.Select(_ => $"{(_.Pin ? "pin" : "unpin")} {_.Path}");
}
