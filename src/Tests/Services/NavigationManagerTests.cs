using Meridian.Services;

namespace Tests.Services;

[TestFixture]
class NavigationManagerTests
{
    [Test]
    public void InitialState()
    {
        var nav = new NavigationManager();

        Assert.That(nav.CurrentPath, Is.Null);
        Assert.That(nav.CanGoBack, Is.False);
        Assert.That(nav.CanGoForward, Is.False);
        Assert.That(nav.CanGoUp, Is.False);
    }

    [Test]
    public void NavigateTo_SetsCurrentPath()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");

        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\Users"));
    }

    [Test]
    public void NavigateTo_EnablesBack()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");
        nav.NavigateTo(@"C:\Windows");

        Assert.That(nav.CanGoBack, Is.True);
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\Windows"));
    }

    [Test]
    public void GoBack_ReturnsPreviousPath()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");
        nav.NavigateTo(@"C:\Windows");

        var result = nav.GoBack();

        Assert.That(result, Is.EqualTo(@"C:\Users"));
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\Users"));
        Assert.That(nav.CanGoForward, Is.True);
    }

    [Test]
    public void GoBack_WhenEmpty_ReturnsNull()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");

        var result = nav.GoBack();

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GoForward_ReturnsNextPath()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");
        nav.NavigateTo(@"C:\Windows");
        nav.GoBack();

        var result = nav.GoForward();

        Assert.That(result, Is.EqualTo(@"C:\Windows"));
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\Windows"));
    }

    [Test]
    public void GoForward_WhenEmpty_ReturnsNull()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");

        var result = nav.GoForward();

        Assert.That(result, Is.Null);
    }

    [Test]
    public void NavigateTo_ClearsForwardStack()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users");
        nav.NavigateTo(@"C:\Windows");
        nav.GoBack();
        nav.NavigateTo(@"C:\Temp");

        Assert.That(nav.CanGoForward, Is.False);
    }

    [Test]
    public void GoUp_NavigatesToParent()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users\Simon");

        var result = nav.GoUp();

        Assert.That(result, Is.EqualTo(@"C:\Users"));
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\Users"));
        Assert.That(nav.CanGoBack, Is.True);
    }

    [Test]
    public void GoUp_AtRoot_ReturnsNull()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\");

        var result = nav.GoUp();

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GoUp_ClearsForwardStack()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\Users\Simon");
        nav.NavigateTo(@"C:\Windows");
        nav.GoBack();
        nav.GoUp();

        Assert.That(nav.CanGoForward, Is.False);
    }

    [Test]
    public void MultipleBackForward()
    {
        var nav = new NavigationManager();

        nav.NavigateTo(@"C:\A");
        nav.NavigateTo(@"C:\B");
        nav.NavigateTo(@"C:\C");

        nav.GoBack();
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\B"));

        nav.GoBack();
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\A"));

        nav.GoForward();
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\B"));

        nav.GoForward();
        Assert.That(nav.CurrentPath, Is.EqualTo(@"C:\C"));
    }
}
