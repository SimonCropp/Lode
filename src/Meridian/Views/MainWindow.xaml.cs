using System.Windows;
using System.Windows.Input;
using Meridian.Services;
using Meridian.ViewModels;

namespace Meridian.Views;

public partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        DataContext = new MainViewModel(startPath);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AddressBar.FocusAddressBar();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is MainViewModel { Tabs.Count: 0 } vm && ShellRegistration.IsRegisteredAsShell())
        {
            e.Cancel = true;
            vm.NewTab();
        }
    }
}
