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
            })
            .Snapshot(
                """
                {
                  cToFirst: [
                    C:\C,
                    C:\A,
                    C:\B,
                    C:\D
                  ],
                  aToLast: [
                    C:\B,
                    C:\C,
                    C:\D,
                    C:\A
                  ],
                  bAfterC: [
                    C:\A,
                    C:\C,
                    C:\B,
                    C:\D
                  ],
                  pastTheEnd: [
                    C:\B,
                    C:\C,
                    C:\D,
                    C:\A
                  ]
                }
                """);

    // The folders moved to the end in turn, each unpinned and pinned again. Those already in
    // order at the start stay put.
    [Test]
    public Task ToMoveToEnd() =>
        Verify(
            new
            {
                cToFirst = MovedFor(PinnedOrder.Move(pinned, @"C:\C", 0)),
                dUpOne = MovedFor(PinnedOrder.Move(pinned, @"C:\D", 2)),
                dToSecond = MovedFor(PinnedOrder.Move(pinned, @"C:\D", 1)),
                aToLast = MovedFor(PinnedOrder.Move(pinned, @"C:\A", 3)),
                bDownOne = MovedFor(PinnedOrder.Move(pinned, @"C:\B", 2))
            })
            .Snapshot(
                """
                {
                  cToFirst: [
                    C:\A,
                    C:\B,
                    C:\D
                  ],
                  dUpOne: [
                    C:\C
                  ],
                  dToSecond: [
                    C:\B,
                    C:\C
                  ],
                  aToLast: [
                    C:\A
                  ],
                  bDownOne: [
                    C:\B,
                    C:\D
                  ]
                }
                """);

    [Test]
    public async Task NothingToMoveWhenUnchanged() =>
        await Assert.That(MovedFor(pinned)).IsEmpty();

    // Moving them to the end of the list as it is, as Quick access does, gives the order asked
    // for.
    [Test]
    public async Task MovingToEndGivesTheOrder()
    {
        for (var from = 0; from < pinned.Length; from++)
        {
            for (var to = 0; to < pinned.Length; to++)
            {
                var desired = PinnedOrder.Move(pinned, pinned[from], to);
                var list = pinned.ToList();
                foreach (var folder in PinnedOrder.ToMoveToEnd(pinned, desired))
                {
                    list.Remove(folder);
                    list.Add(folder);
                }

                await Assert.That(string.Join(", ", list)).IsEqualTo(string.Join(", ", desired));
            }
        }
    }

    static List<string> MovedFor(IReadOnlyList<string> desired) =>
        PinnedOrder.ToMoveToEnd(pinned, desired);
}
