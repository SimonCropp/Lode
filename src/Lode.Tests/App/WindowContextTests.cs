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
}
