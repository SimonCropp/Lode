using System.Windows;
using System.Windows.Controls;
using Meridian.Views;

namespace Tests.Views;

[TestFixture]
[Apartment(ApartmentState.STA)]
class FileBrowserTabTests
{
    [Test]
    public void DefaultState()
    {
        var tab = new FileBrowserTab();
        Assert.That(tab, Is.Not.Null);

        var grid = (Grid)tab.Content;
        Assert.That(grid.ColumnDefinitions, Has.Count.EqualTo(3));

        var treeView = (TreeView)grid.Children[0];
        Assert.That(treeView, Is.Not.Null);

        var listView = (ListView)grid.Children[2];
        Assert.That(listView, Is.Not.Null);
        Assert.That(listView.ContextMenu?.Items, Has.Count.EqualTo(8));
    }
}
