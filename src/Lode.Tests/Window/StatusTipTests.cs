/// <summary>
/// The status bar's tooltips go above it. The bar's own went below the pointer, and at the
/// bottom of the screen Windows moved them up over it, so the bar took the pointer to have left,
/// hid the tooltip, and showed it again, over and over.
/// </summary>
public class StatusTipTests
{
    [Test]
    public async Task AboveTheBarClearOfThePointer()
    {
        var item = new Rectangle(900, 2, 120, 26);

        var location = StatusBar.TipLocation(item, height: 30, gap: 4);

        await Assert.That(location).IsEqualTo(new(900, -34));
        // Its bottom, the location's top plus its height, is still above the bar's top.
        await Assert.That(location.Y + 30).IsLessThan(0);
    }
}
