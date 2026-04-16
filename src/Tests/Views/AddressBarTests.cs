using Meridian.Views;

namespace Tests.Views;

[TestFixture]
[Apartment(ApartmentState.STA)]
class AddressBarTests
{
    [Test]
    public async Task DefaultState()
    {
        var bar = new AddressBar();
        await Verify(bar);
    }
}
