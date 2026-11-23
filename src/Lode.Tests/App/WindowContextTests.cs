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

    // Only a newer version has an item: newest and build are none, so leave the snapshot.
    [Test]
    public Task UpdateItemText() =>
        Verify(
            new
            {
                available = WindowContext.UpdateItemText(
                    TestUpdates.Installed(
                        new()
                        {
                            AvailableUpdate = "0.4.0-beta.2"
                        })),
                newest = WindowContext.UpdateItemText(TestUpdates.Installed(new())),
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
                  available: Update to Lode 0.4.0-beta.2
                }
                """);
}
