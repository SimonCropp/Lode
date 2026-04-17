[TestFixture]
[Apartment(ApartmentState.STA)]
class MainWindowTests
{
    [Test]
    public async Task InitialLayout()
    {
        var window = new MainWindow();
        await Verify(window);
    }
}
