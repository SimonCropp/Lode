public class IconsTests
{
    // Windows 11 has Segoe Fluent Icons; Windows 10 only Segoe MDL2 Assets.
    [Test]
    public Task FontName() =>
        Verify(
            new
            {
                windows11 = Icons.FontName(["Segoe UI", "Segoe Fluent Icons", "Segoe MDL2 Assets"]),
                windows10 = Icons.FontName(["Segoe UI", "Segoe MDL2 Assets"])
            });
}
