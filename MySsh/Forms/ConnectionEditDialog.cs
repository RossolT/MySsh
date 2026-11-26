using MySsh.Controls;
using MySsh.Storage;

namespace MySsh.Forms;

/// <summary>Диалог создания/редактирования профиля.</summary>
public sealed class ConnectionEditDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new();
    private readonly TextBox _login = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _showPassword = new();
    private readonly RadioButton _kindSsh = new() { Text = "SSH (терминал)", AutoSize = true };
    private readonly RadioButton _kindSftp = new() { Text = "SFTP (файлы)", AutoSize = true };

    public ConnectionProfile Profile { get; }

    public ConnectionEditDialog(ConnectionProfile? existing = null)
    {
        Profile = existing?.Clone() ?? new ConnectionProfile();

        Text = existing is null ? "Новое подключение" : "Изменение подключения";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 415);
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);

        int y = 14;

        AddLabel("Название подключения", ref y);
        StyleInput(_name);
        _name.Text = Profile.Name;
        Place(_name, ref y);

        AddLabel("Хост", ref y);
        StyleInput(_host);
        _host.Text = Profile.Host;
        Place(_host, ref y);

        AddLabel("Порт", ref y);
        _port.Minimum = 1;
        _port.Maximum = 65535;
        _port.Value = Profile.Port <= 0 ? 22 : Profile.Port;
        _port.BackColor = Theme.InputBack;
        _port.ForeColor = Theme.TextPrimary;
        _port.BorderStyle = BorderStyle.FixedSingle;
        Place(_port, ref y);

        AddLabel("Логин", ref y);
        StyleInput(_login);
        _login.Text = Profile.Login;
        Place(_login, ref y);

        AddLabel("Пароль", ref y);
        StyleInput(_password);
        _password.UseSystemPasswordChar = true;
        _password.Text = SecretProtector.Unprotect(Profile.ProtectedPassword);
        Place(_password, ref y);

        _showPassword.Text = "Показать пароль";
        _showPassword.ForeColor = Theme.TextMuted;
        _showPassword.AutoSize = true;
        _showPassword.Location = new Point(16, y);
        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;
        Controls.Add(_showPassword);
        y += 28;

        var kindGroup = new GroupBox
        {
            Text = "Тип подключения",
            ForeColor = Theme.TextMuted,
            Location = new Point(16, y),
            Size = new Size(ClientSize.Width - 32, 56)
        };
        _kindSsh.Location = new Point(14, 24);
        _kindSsh.ForeColor = Theme.TextPrimary;
        _kindSftp.Location = new Point(185, 24);
        _kindSftp.ForeColor = Theme.TextPrimary;

        _kindSsh.Checked = Profile.Kind != ConnectionKind.Sftp;
        _kindSftp.Checked = Profile.Kind == ConnectionKind.Sftp;

        kindGroup.Controls.Add(_kindSsh);
        kindGroup.Controls.Add(_kindSftp);
        Controls.Add(kindGroup);
        y += 68;

        var ok = new FlatButton
        {
            Text = "Сохранить",
            NormalColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 224, y),
            Width = 104,
            Height = 32
        };
        ok.Click += OnSaveClick;

        var cancel = new FlatButton
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 112, y),
            Width = 96,
            Height = 32
        };

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        string name = _name.Text.Trim();
        string host = _host.Text.Trim();
        string login = _login.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Укажите название подключения.", "MySsh",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _name.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Укажите адрес хоста.", "MySsh",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _host.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(login))
        {
            MessageBox.Show(this, "Укажите логин.", "MySsh",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _login.Focus();
            return;
        }

        Profile.Name = name;
        Profile.Host = host;
        Profile.Port = (int)_port.Value;
        Profile.Login = login;
        Profile.Kind = _kindSftp.Checked ? ConnectionKind.Sftp : ConnectionKind.Ssh;
        Profile.ProtectedPassword = SecretProtector.Protect(_password.Text);
    }

    private void AddLabel(string text, ref int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            ForeColor = Theme.TextMuted,
            Location = new Point(16, y),
            AutoSize = true
        });
        y += 20;
    }

    private void Place(Control c, ref int y)
    {
        c.Location = new Point(16, y);
        c.Width = ClientSize.Width - 32;
        Controls.Add(c);
        y += 34;
    }

    private static void StyleInput(TextBox t)
    {
        t.BackColor = Theme.InputBack;
        t.ForeColor = Theme.TextPrimary;
        t.BorderStyle = BorderStyle.FixedSingle;
    }
}