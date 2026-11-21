/// <summary>
/// What each device notification means for a drive: going, staying, or neither.
/// </summary>
public class DriveRemovalTests
{
    [Test]
    public Task Meaning() =>
        Verify(
            new
            {
                queryRemove = DriveRemoval.Meaning(2, null),
                queryRemoveFailed = DriveRemoval.Meaning(3, null),
                removePending = DriveRemoval.Meaning(4, null),
                removeComplete = DriveRemoval.Meaning(5, null),
                volumeLock = DriveRemoval.Meaning(6, new("50708874-c9af-11d1-8fef-00a0c9a06d32")),
                volumeLockFailed = DriveRemoval.Meaning(6, new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32")),
                volumeUnlock = DriveRemoval.Meaning(6, new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32")),
                volumeDismount = DriveRemoval.Meaning(6, new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32")),
                volumeMount = DriveRemoval.Meaning(6, new("b5804878-1a96-11d2-8ffd-00a0c9a06d32")),
                otherCustomEvent = DriveRemoval.Meaning(6, Guid.Empty)?.ToString() ?? "neither",
                interfaceArrival = DriveRemoval.Meaning(0, null)?.ToString() ?? "neither"
            });
}
