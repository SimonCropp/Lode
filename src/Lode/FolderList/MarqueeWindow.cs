/// <summary>
/// The selection rectangle: a borderless, translucent window in the highlight colour that
/// takes neither the focus nor the mouse.
/// </summary>
sealed class MarqueeWindow : Form
{
    const int layered = 0x80000;
    const int transparent = 0x20;
    const int toolWindow = 0x80;
    const int noActivate = 0x8000000;

    public MarqueeWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = SystemColors.Highlight;
        Opacity = 0.3;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= layered | transparent | toolWindow | noActivate;
            return parameters;
        }
    }
}