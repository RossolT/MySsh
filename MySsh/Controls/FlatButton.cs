namespace MySsh.Controls;

/// <summary>Плоская кнопка под тёмную тему.</summary>
public sealed class FlatButton : Button
{
    private bool _hover;

    public Color NormalColor { get; set; } = Theme.InputBack;
    public Color HoverColor { get; set; } = Theme.Border;

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = NormalColor;
        ForeColor = Theme.TextPrimary;
        Height = 32;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        BackColor = HoverColor;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        BackColor = NormalColor;
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
        if (_hover) return;
        BackColor = NormalColor;
    }
}
