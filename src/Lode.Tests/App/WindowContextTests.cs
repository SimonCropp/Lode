public class WindowContextTests
{
    [Test]
    public Task TrayClick() =>
        Verify(
            new
            {
                leftClick = WindowContext.TrayClick(MouseButtons.Left, Keys.None).ToString(),
                shiftClick = WindowContext.TrayClick(MouseButtons.Left, Keys.Shift).ToString(),
                controlClick = WindowContext.TrayClick(MouseButtons.Left, Keys.Control).ToString(),
                rightClick = WindowContext.TrayClick(MouseButtons.Right, Keys.None).ToString(),
                shiftRightClick = WindowContext.TrayClick(MouseButtons.Right, Keys.Shift).ToString()
            })
            .Snapshot(
                """
                {
                  leftClick: ShowWindows,
                  shiftClick: NewWindow,
                  controlClick: ShowWindows,
                  rightClick: None,
                  shiftRightClick: None
                }
                """);

    // A build has no item, so it is left out of the snapshot.
    [Test]
    public async Task UpdateItemText()
    {
        var answer = new TaskCompletionSource<NuGetVersion?>();
        var checking = TestUpdates.Installed(new(), _ => answer.Task);
        var check = checking.Check();
        var checkingText = WindowContext.UpdateItemText(checking);
        answer.SetResult(null);
        await check;

        await Verify(
                new
                {
                    available = WindowContext.UpdateItemText(
                        TestUpdates.Installed(
                            new()
                            {
                                AvailableUpdate = "0.4.0-beta.2"
                            })),
                    newest = WindowContext.UpdateItemText(TestUpdates.Installed(new())),
                    checking = checkingText,
                    build = WindowContext.UpdateItemText(
                        TestUpdates.Build(
                            new()
                            {
                                AvailableUpdate = "0.4.0"
                            }))
                })
            .Snapshot(
                """
                {
                  available: Update to Lode 0.4.0-beta.2,
                  newest: Check for updates,
                  checking: Checking for updates…
                }
                """);
    }

    [Test]
    public async Task CheckNotice()
    {
        var available = TestUpdates.Installed(new(), "0.4.0");
        await available.Check();
        var newest = TestUpdates.Installed(new(), "0.3.0");
        await newest.Check();
        var failed = TestUpdates.Installed(new(), _ => throw new HttpRequestException("No such host is known."));
        await failed.Check();

        await Verify(
                new
                {
                    available = WindowContext.CheckNotice(available),
                    newest = WindowContext.CheckNotice(newest),
                    failed = WindowContext.CheckNotice(failed)
                })
            .Snapshot(
                """
                {
                  available: {
                    Title: Lode 0.4.0 is available,
                    Message: Update to Lode 0.4.0 is in this icon's menu.,
                    Icon: Info
                  },
                  newest: {
                    Title: Lode is up to date,
                    Message: Lode 0.3.0 is the newest version.,
                    Icon: Info
                  },
                  failed: {
                    Title: Couldn't check for updates,
                    Message: No such host is known.,
                    Icon: Warning
                  }
                }
                """);
    }
}
