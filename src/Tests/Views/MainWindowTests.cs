using System.Windows;
using Meridian.Views;

namespace Tests.Views;

[TestFixture]
[Apartment(ApartmentState.STA)]
class MainWindowTests
{
    [Test]
    public void WindowCreates()
    {
        var window = new MainWindow();
        Assert.That(window, Is.Not.Null);
        Assert.That(window.Title, Is.EqualTo("Meridian"));
        Assert.That(window.Width, Is.EqualTo(1024));
        Assert.That(window.Height, Is.EqualTo(768));
    }
}
