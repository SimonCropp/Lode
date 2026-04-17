namespace Meridian.Views;

public partial class AddressBar
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
