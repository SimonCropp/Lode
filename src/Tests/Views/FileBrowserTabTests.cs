[TestFixture]
[Apartment(ApartmentState.STA)]
class FileBrowserTabTests
{
    [Test]
    public async Task DefaultState()
    {
        var tab = new FileBrowserTab();
        await Verify(tab);
    }
}
