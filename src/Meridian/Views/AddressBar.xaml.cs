using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Meridian.Views;

public partial class AddressBar : UserControl
{
    public AddressBar() =>
        InitializeComponent();

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var binding = PathBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            var binding = PathBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    public void FocusAddressBar() =>
        PathBox.Focus();
}
