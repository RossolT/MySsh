using System.Text;
using System.Text.Json;
using MySsh.Forms;
using MySsh.Services;
using MySsh.Storage;

namespace MySsh.Controls;

/// <summary>Содержимое вкладки №0: список сохранённых подключений.</summary>
public sealed class ConnectionsManagerControl : UserControl
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly MainForm _mainForm;
    private readonly ProfileStore _store = new();
    private List<ConnectionProfile> _profiles = new();

    private readonly ListView _list = new();
    private readonly Label _status = new();

    public ConnectionsManagerControl(MainForm mainForm)
    {
        _mainForm = mainForm;
        Dock = DockStyle.Fill;
        BackColor = Theme.WindowBack;
        Padding = new Padding(12);

        // --- Заголовок ---
        var title = new Label
        {
            Text = "Сохранённые подключения",
            ForeColor = Theme.TextPrimary,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // --- Кнопки ---
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 6)
        };

        var add = MakeButton("Добавить", Theme.Accent);
        var edit = MakeButton("Изменить");
        var remove = MakeButton("Удалить");
        var open = MakeButton("Подключиться");
        var openSftp = MakeButton("Открыть SFTP");
        var exportBtn = MakeButton("Экспорт");
        var importBtn = MakeButton("Импорт");
        remove.NormalColor = Theme.InputBack;
        remove.BackColor = Theme.InputBack;
        remove.ForeColor = Theme.Danger;

        add.Click += (_, _) => AddProfile();
        edit.Click += (_, _) => EditProfile();
        remove.Click += (_, _) => RemoveProfile();
        open.Click += (_, _) => OpenSelected();
        openSftp.Click += (_, _) => OpenSelectedAsSftp();
        exportBtn.Click += async (_, _) => await ExportProfilesAsync();
        importBtn.Click += (_, _) => ImportProfiles();

        buttons.Controls.AddRange(new Control[] { add, edit, remove, open, openSftp, exportBtn, importBtn });

        // --- Список ---
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BackColor = Theme.PanelBack;
        _list.ForeColor = Theme.TextPrimary;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Columns.Add("Название", 240);
        _list.Columns.Add("Хост", 220);
        _list.Columns.Add("Логин", 160);
        _list.Columns.Add("Тип", 90);
        _list.DoubleClick += (_, _) => OpenSelected();

        // --- Статус ---
        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;
        _status.ForeColor = Theme.TextMuted;

        // Порядок добавления важен для Dock: Fill добавляется последним по z-порядку.
        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(title);
        Controls.Add(_status);

        ReloadProfiles();
    }

    private static FlatButton MakeButton(string text, Color? accent = null)
    {
        var b = new FlatButton
        {
            Text = text,
            Width = 120,
            Margin = new Padding(0, 0, 8, 0)
        };
        if (accent is { } c)
        {
            b.NormalColor = c;
            b.HoverColor = Theme.AccentHover;
            b.BackColor = c;
        }
        return b;
    }

    private void ReloadProfiles()
    {
        _profiles = _store.Load();
        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _profiles)
        {
            var item = new ListViewItem(p.Name)
            {
                Tag = p
            };
            item.SubItems.Add(p.Host);
            item.SubItems.Add(p.Login);
            item.SubItems.Add(p.Kind == ConnectionKind.Sftp ? "SFTP" : "SSH");
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _status.Text = _profiles.Count == 0
            ? "Нет сохранённых подключений. Нажмите «Добавить»."
            : $"Всего подключений: {_profiles.Count}";
    }

    private ConnectionProfile? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ConnectionProfile : null;

    private async Task ExportProfilesAsync()
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show(this, "Нет подключений для экспорта.", "MySsh",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool verified = await WindowsHelloPrompt.VerifyIfEnabledAsync(
            this,
            "Подтвердите личность для экспорта подключений MySsh");

        if (!verified)
        {
            MessageBox.Show(this,
                "Подтверждение не пройдено. Экспорт отменён.",
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ExportProfiles();
    }

    private void ExportProfiles()
    {
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Экспорт подключений",
                Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = "json",
                FileName = $"myssh-connections-{DateTime.Now:yyyy-MM-dd}.json",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            string json = JsonSerializer.Serialize(_profiles, ExportJsonOptions);
            File.WriteAllText(dlg.FileName, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _status.Text = $"Экспортировано подключений: {_profiles.Count} -> {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось экспортировать подключения:\n" + ex.Message,
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportProfiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Импорт подключений",
            Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            string json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            var imported = JsonSerializer.Deserialize<List<ConnectionProfile>>(json);
            if (imported is null || imported.Count == 0)
            {
                MessageBox.Show(this, "В файле не найдено подключений для импорта.", "MySsh",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var existingIds = _profiles.Select(p => p.Id).ToHashSet();
            int addedCount = 0;

            foreach (var raw in imported)
            {
                if (string.IsNullOrWhiteSpace(raw.Name) || string.IsNullOrWhiteSpace(raw.Host))
                    continue;

                var profile = raw;
                if (profile.Id == Guid.Empty || existingIds.Contains(profile.Id))
                    profile.Id = Guid.NewGuid();

                existingIds.Add(profile.Id);
                _profiles.Add(profile);
                addedCount++;
            }

            if (addedCount == 0)
            {
                MessageBox.Show(this, "Не удалось найти корректные записи подключений (нужны название и хост).",
                    "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Persist();
            _status.Text = $"Импортировано подключений: {addedCount}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось импортировать подключения:\n" + ex.Message,
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddProfile()
    {
        using var dlg = new ConnectionEditDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        if (string.IsNullOrWhiteSpace(dlg.Profile.Name) || string.IsNullOrWhiteSpace(dlg.Profile.Host))
        {
            MessageBox.Show(this, "Укажите название и хост.", "MySsh",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _profiles.Add(dlg.Profile);
        Persist();
    }

    private void EditProfile()
    {
        var selected = Selected;
        if (selected is null)
        {
            NotifySelect();
            return;
        }

        using var dlg = new ConnectionEditDialog(selected);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        int index = _profiles.FindIndex(p => p.Id == selected.Id);
        if (index >= 0)
            _profiles[index] = dlg.Profile;

        Persist();
    }

    private void RemoveProfile()
    {
        var selected = Selected;
        if (selected is null)
        {
            NotifySelect();
            return;
        }

        var answer = MessageBox.Show(this,
            $"Удалить подключение «{selected.Name}»?", "MySsh",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        _profiles.RemoveAll(p => p.Id == selected.Id);
        Persist();
    }

    private string? ResolvePassword(ConnectionProfile profile)
    {
        string password = SecretProtector.Unprotect(profile.ProtectedPassword);

        if (string.IsNullOrEmpty(password))
        {
            using var prompt = new PasswordPromptDialog(profile.Name);
            if (prompt.ShowDialog(this) != DialogResult.OK)
                return null;
            password = prompt.Password;
        }

        return password;
    }

    private void OpenSelected()
    {
        var selected = Selected;
        if (selected is null)
        {
            NotifySelect();
            return;
        }

        string? password = ResolvePassword(selected);
        if (password is null)
            return;

        if (selected.Kind == ConnectionKind.Ssh)
        {
            OpenSshTab(selected, password);
        }
        else
        {
            OpenSftpTab(selected, password);
        }
    }

    private void OpenSelectedAsSftp()
    {
        var selected = Selected;
        if (selected is null)
        {
            NotifySelect();
            return;
        }

        string? password = ResolvePassword(selected);
        if (password is null)
            return;

        OpenSftpTab(selected, password);
    }

    private void OpenSshTab(ConnectionProfile profile, string password)
    {
        var tab = new TabPage(profile.Name) { BackColor = Theme.WindowBack };

        var session = new SshSessionTabControl(profile.Host, profile.Port, profile.Login, password);
        session.Dock = DockStyle.Fill;

        session.StatusChanged += (_, status) =>
        {
            tab.ToolTipText = status;
        };

        tab.Controls.Add(session);
        _mainForm.AddSessionTab(tab);
    }

    private void OpenSftpTab(ConnectionProfile profile, string password)
    {
        var tab = new TabPage($"[SFTP] {profile.Name}") { BackColor = Theme.WindowBack };

        var session = new SftpSessionTabControl(
            profile.Host,
            profile.Port,
            profile.Login,
            password,
            openTabAction: _mainForm.AddSessionTab,
            closeTabAction: _mainForm.CloseTabPage);
        session.Dock = DockStyle.Fill;

        session.StatusChanged += (_, status) =>
        {
            tab.ToolTipText = status;
        };

        tab.Controls.Add(session);
        _mainForm.AddSessionTab(tab);
    }

    private void Persist()
    {
        try
        {
            _store.Save(_profiles);
            RefreshList();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Не удалось сохранить подключения:\n" + ex.Message,
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void NotifySelect() =>
        MessageBox.Show(this, "Сначала выберите подключение в списке.", "MySsh",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
}