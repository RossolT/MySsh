using MySsh.Controls;

namespace MySsh.Forms;

/// <summary>Запрос пароля, если он не сохранён в профиле.</summary>
public sealed class PasswordPromptDialog : Form
{
    private readonly TextBox _password = new();
    public string Password => _password.Text;

    public PasswordPromptDialog(string connectionName)
    {
        Text = "Пароль — " + connectionName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(380, 150);
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);

        Controls.Add(new Label
        {
            Text = "Пароль не сохранён. Введите пароль:",
            ForeColor = Theme.TextMuted,
            Location = new Point(16, 16),
            AutoSize = true
        });

        _password.Location = new Point(16, 44);
        _password.Width = ClientSize.Width - 32;
        _password.UseSystemPasswordChar = true;
        _password.BackColor = Theme.InputBack;
        _password.ForeColor = Theme.TextPrimary;
        _password.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_password);

        var ok = new FlatButton
        {
            Text = "Подключиться",
            NormalColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 220, 90),
            Width = 130
        };

        var cancel = new FlatButton
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 82, 90),
            Width = 66
        };

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
