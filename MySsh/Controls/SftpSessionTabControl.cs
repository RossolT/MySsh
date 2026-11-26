using MySsh.Models;
using MySsh.Services;

namespace MySsh.Controls;

/// <summary>Вкладка двухпанельного SFTP: слева локальная ФС, справа удалённая.</summary>
public sealed class SftpSessionTabControl : UserControl, IDisposable
{
    private const long MaxEditableFileBytes = 2 * 1024 * 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly string _login;
    private readonly string _password;

    private readonly SftpService _sftp = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly Panel _topBar = new();
    private readonly Label _lblStatus = new();
    private readonly ProgressBar _progress = new();
    private readonly FlatButton _btnDownload = new() { Text = "← Скачать", Width = 105, Height = 28 };
    private readonly FlatButton _btnUpload = new() { Text = "Загрузить →", Width = 110, Height = 28 };
    private readonly FlatButton _btnMkdir = new() { Text = "Новая папка", Width = 110, Height = 28 };
    private readonly FlatButton _btnDelete = new() { Text = "Удалить", Width = 90, Height = 28 };
    private readonly FlatButton _btnReconnect = new() { Text = "Повторить", Width = 95, Height = 28 };

    private readonly SplitContainer _split = new();
    private readonly FilePanelControl _localPanel;
    private readonly FilePanelControl _remotePanel;

    private readonly Action<TabPage>? _openTabAction;
    private readonly Action<TabPage>? _closeTabAction;

    private bool _busy;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public SftpSessionTabControl(
        string host,
        int port,
        string login,
        string password,
        Action<TabPage>? openTabAction = null,
        Action<TabPage>? closeTabAction = null)
    {
        _host = host;
        _port = port;
        _login = login;
        _password = password;
        _openTabAction = openTabAction;
        _closeTabAction = closeTabAction;

        Dock = DockStyle.Fill;
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        _localPanel = new FilePanelControl("Локальный компьютер");
        _remotePanel = new FilePanelControl($"Сервер ({_login}@{_host}:{_port})");

        BuildLayout();
        WireEvents();

        Load += async (_, _) => await InitializeAsync();
    }

    private void BuildLayout()
    {
        _topBar.Dock = DockStyle.Top;
        _topBar.Height = 40;
        _topBar.BackColor = Theme.PanelBack;
        _topBar.Padding = new Padding(10, 6, 10, 6);

        _progress.Dock = DockStyle.Right;
        _progress.Width = 130;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;

        _btnReconnect.Dock = DockStyle.Right;
        _btnReconnect.Visible = false;
        _btnReconnect.NormalColor = Theme.Accent;
        _btnReconnect.HoverColor = Theme.AccentHover;
        _btnReconnect.BackColor = Theme.Accent;

        _btnDelete.Dock = DockStyle.Right;
        _btnDelete.ForeColor = Theme.Danger;

        _btnMkdir.Dock = DockStyle.Right;
        _btnUpload.Dock = DockStyle.Right;
        _btnDownload.Dock = DockStyle.Right;
        _btnDownload.NormalColor = Theme.Accent;
        _btnDownload.HoverColor = Theme.AccentHover;
        _btnDownload.BackColor = Theme.Accent;

        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.ForeColor = Theme.TextMuted;
        _lblStatus.Text = $"Подключение SFTP к {_login}@{_host}:{_port}…";

        // В Dock.Right элементы добавляются справа налево по порядку пристыковки.
        _topBar.Controls.Add(_lblStatus);
        _topBar.Controls.Add(_progress);
        _topBar.Controls.Add(_btnReconnect);
        _topBar.Controls.Add(_btnDelete);
        _topBar.Controls.Add(_btnMkdir);
        _topBar.Controls.Add(_btnUpload);
        _topBar.Controls.Add(_btnDownload);

        _split.Dock = DockStyle.Fill;
        _split.BackColor = Theme.Border;
        _split.SplitterWidth = 4;
        _split.Panel1.BackColor = Theme.PanelBack;
        _split.Panel2.BackColor = Theme.PanelBack;
        _split.Panel1.Controls.Add(_localPanel);
        _split.Panel2.Controls.Add(_remotePanel);

        // В WinForms Dock.Fill добавляется в Controls первым, Dock.Top — после него.
        Controls.Add(_split);
        Controls.Add(_topBar);
    }

    private void WireEvents()
    {
        _split.SizeChanged += (_, _) =>
        {
            if (_split.Width > 200)
                _split.SplitterDistance = _split.Width / 2;
        };

        _localPanel.NavigateRequested += LoadLocalDirectoryAsync;
        _localPanel.RefreshRequested += async (_, _) => await LoadLocalDirectoryAsync(_localPanel.CurrentPath);
        _localPanel.FileActivated += async (_, entry) => await UploadSingleAsync(entry);

        _remotePanel.NavigateRequested += LoadRemoteDirectoryAsync;
        _remotePanel.RefreshRequested += async (_, _) => await LoadRemoteDirectoryAsync(_remotePanel.CurrentPath);
        _remotePanel.FileActivated += async (_, entry) => await OpenOrEditRemoteEntryAsync(entry);

        _btnDownload.Click += async (_, _) => await DownloadSelectedAsync();
        _btnUpload.Click += async (_, _) => await UploadSelectedAsync();
        _btnMkdir.Click += async (_, _) => await CreateRemoteDirectoryAsync();
        _btnDelete.Click += async (_, _) => await DeleteSelectedRemoteAsync();
        _btnReconnect.Click += async (_, _) => await InitializeAsync();
    }

    private async Task OpenOrEditRemoteEntryAsync(FileEntryItem remoteEntry)
    {
        if (!_sftp.IsConnected || _busy) return;

        if (remoteEntry.IsDirectory || remoteEntry.IsParentLink)
        {
            await LoadRemoteDirectoryAsync(remoteEntry.FullPath);
            return;
        }

        if (remoteEntry.Size > MaxEditableFileBytes)
        {
            var choice = MessageBox.Show(this,
                $"Файл «{remoteEntry.Name}» слишком большой для встроенного редактора (> 2 МБ).\n" +
                "Скачать его на локальный компьютер?",
                "MySsh", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (choice == DialogResult.Yes)
                await DownloadSingleAsync(remoteEntry);
            return;
        }

        _busy = true;
        SetControlsEnabled(false);
        SetStatus($"Открытие «{remoteEntry.Name}» в редакторе…", Theme.TextMuted);

        var readRes = await _sftp.ReadTextFileAsync(remoteEntry.FullPath, remoteEntry.Size, _cts.Token);

        _busy = false;
        SetControlsEnabled(true);

        if (_disposed) return;

        if (!readRes.Ok)
        {
            SetStatus("Ошибка чтения файла: " + (readRes.Error ?? "неизвестная ошибка"), Theme.Danger);
            MessageBox.Show(this, "Не удалось открыть файл:\n" + readRes.Error,
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var tab = new TabPage($"[Редактор] {remoteEntry.Name}") { BackColor = Theme.WindowBack };

        var editorTab = new TextFileEditorTabControl(
            remoteEntry.FullPath,
            readRes.Content,
            async (text, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, token);
                return await _sftp.WriteTextFileAsync(remoteEntry.FullPath, text, linked.Token);
            },
            closeTabAction: _ => _closeTabAction?.Invoke(tab));

        editorTab.Dock = DockStyle.Fill;
        editorTab.StatusChanged += (_, status) => tab.ToolTipText = status;
        editorTab.FileSaved += async (_, _) =>
        {
            if (!_disposed && _sftp.IsConnected)
                await LoadRemoteDirectoryAsync(_remotePanel.CurrentPath);
        };

        tab.Controls.Add(editorTab);

        if (_openTabAction is not null)
        {
            _openTabAction(tab);
            SetStatus($"Открыт в редакторе: «{remoteEntry.Name}»", Theme.TextPrimary);
        }
        else
        {
            using var fallbackForm = new Form
            {
                Text = $"Редактор — {remoteEntry.Name}",
                Size = new Size(920, 620),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Theme.WindowBack
            };
            var standalone = new TextFileEditorTabControl(
                remoteEntry.FullPath,
                readRes.Content,
                async (text, token) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, token);
                    return await _sftp.WriteTextFileAsync(remoteEntry.FullPath, text, linked.Token);
                },
                closeTabAction: _ => fallbackForm.Close())
            {
                Dock = DockStyle.Fill
            };
            fallbackForm.Controls.Add(standalone);
            fallbackForm.ShowDialog(this);
        }
    }

    private async Task InitializeAsync()
    {
        if (_disposed || _busy) return;
        _busy = true;
        SetControlsEnabled(false);
        _btnReconnect.Visible = false;
        SetStatus($"Подключение SFTP к {_login}@{_host}:{_port}…", Theme.TextMuted);

        string startLocal = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(startLocal) || !Directory.Exists(startLocal))
            startLocal = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        await LoadLocalDirectoryAsync(startLocal);

        var connectResult = await _sftp.ConnectAsync(_host, _port, _login, _password, _cts.Token);
        if (_disposed) return;

        if (!connectResult.Success)
        {
            _busy = false;
            SetControlsEnabled(true);
            _btnReconnect.Visible = true;
            SetStatus("Ошибка SFTP: " + (connectResult.ErrorMessage ?? "Не удалось подключиться."), Theme.Danger);
            return;
        }

        await LoadRemoteDirectoryAsync(".");
        _busy = false;
        SetControlsEnabled(true);
        _btnReconnect.Visible = false;
        SetStatus($"SFTP подключено: {_login}@{_host}:{_port}", Theme.TextPrimary);
    }

    private Task LoadLocalDirectoryAsync(string path)
    {
        try
        {
            string target = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path;

            if (!Directory.Exists(target))
            {
                SetStatus($"Локальная папка не найдена: {target}", Theme.Danger);
                return Task.CompletedTask;
            }

            var items = new List<FileEntryItem>();
            var parent = Directory.GetParent(target);
            if (parent is not null)
            {
                items.Add(new FileEntryItem
                {
                    Name = "..",
                    FullPath = parent.FullName,
                    IsDirectory = true,
                    IsParentLink = true
                });
            }

            foreach (string dir in Directory.GetDirectories(target))
            {
                var di = new DirectoryInfo(dir);
                items.Add(new FileEntryItem
                {
                    Name = di.Name,
                    FullPath = di.FullName,
                    IsDirectory = true,
                    IsParentLink = false,
                    LastWriteTime = di.LastWriteTimeUtc
                });
            }

            foreach (string file in Directory.GetFiles(target))
            {
                var fi = new FileInfo(file);
                items.Add(new FileEntryItem
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    IsDirectory = false,
                    IsParentLink = false,
                    Size = fi.Length,
                    LastWriteTime = fi.LastWriteTimeUtc
                });
            }

            var sorted = items
                .OrderBy(x => x.IsParentLink ? 0 : (x.IsDirectory ? 1 : 2))
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _localPanel.SetEntries(target, sorted);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка локальной ФС: " + ex.Message, Theme.Danger);
        }

        return Task.CompletedTask;
    }

    private async Task LoadRemoteDirectoryAsync(string path)
    {
        if (!_sftp.IsConnected) return;

        var res = await _sftp.ListDirectoryAsync(path, _cts.Token);
        if (_disposed) return;

        if (!res.Ok)
        {
            SetStatus("Ошибка каталога сервера: " + (res.Error ?? "неизвестная ошибка"), Theme.Danger);
            return;
        }

        _remotePanel.SetEntries(res.Path, res.Items);
    }

    private async Task DownloadSelectedAsync()
    {
        var selected = _remotePanel.SelectedEntries.Where(x => !x.IsDirectory).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Выберите файл на удалённой панели справа для скачивания.",
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var entry in selected)
            await DownloadSingleAsync(entry);
    }

    private async Task DownloadSingleAsync(FileEntryItem remoteEntry)
    {
        if (remoteEntry.IsDirectory || !_sftp.IsConnected || _busy) return;
        if (string.IsNullOrWhiteSpace(_localPanel.CurrentPath)) return;

        string localTarget = Path.Combine(_localPanel.CurrentPath, remoteEntry.Name);
        await RunTransferAsync($"Скачивание «{remoteEntry.Name}»…", async prog =>
            await _sftp.DownloadFileAsync(remoteEntry.FullPath, localTarget, remoteEntry.Size, prog, _cts.Token));

        await LoadLocalDirectoryAsync(_localPanel.CurrentPath);
    }

    private async Task UploadSelectedAsync()
    {
        var selected = _localPanel.SelectedEntries.Where(x => !x.IsDirectory).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Выберите файл на локальной панели слева для загрузки.",
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var entry in selected)
            await UploadSingleAsync(entry);
    }

    private async Task UploadSingleAsync(FileEntryItem localEntry)
    {
        if (localEntry.IsDirectory || !_sftp.IsConnected || _busy) return;
        if (string.IsNullOrWhiteSpace(_remotePanel.CurrentPath)) return;

        string remoteTarget = SftpService.CombineRemotePath(_remotePanel.CurrentPath, localEntry.Name);
        await RunTransferAsync($"Загрузка «{localEntry.Name}»…", async prog =>
            await _sftp.UploadFileAsync(localEntry.FullPath, remoteTarget, prog, _cts.Token));

        await LoadRemoteDirectoryAsync(_remotePanel.CurrentPath);
    }

    private async Task RunTransferAsync(string label, Func<IProgress<double>, Task<SftpOperationResult>> action)
    {
        _busy = true;
        SetControlsEnabled(false);
        _progress.Value = 0;
        _progress.Visible = true;
        SetStatus(label, Theme.TextMuted);

        var progress = new Progress<double>(pct =>
        {
            if (!_disposed && !IsDisposed)
                _progress.Value = Math.Clamp((int)Math.Round(pct), 0, 100);
        });

        var result = await action(progress);

        _progress.Visible = false;
        _busy = false;
        SetControlsEnabled(true);

        if (result.Success)
            SetStatus("Передача завершена.", Theme.TextPrimary);
        else
            SetStatus("Ошибка передачи: " + (result.ErrorMessage ?? "неизвестная ошибка"), Theme.Danger);
    }

    private async Task CreateRemoteDirectoryAsync()
    {
        if (!_sftp.IsConnected || _busy) return;

        string? name = PromptText("Новая папка на сервере", "Имя папки:", "new_folder");
        if (string.IsNullOrWhiteSpace(name)) return;

        string targetPath = SftpService.CombineRemotePath(_remotePanel.CurrentPath, name.Trim());
        var res = await _sftp.CreateDirectoryAsync(targetPath);
        if (res.Success)
        {
            await LoadRemoteDirectoryAsync(_remotePanel.CurrentPath);
            SetStatus($"Создана папка «{name.Trim()}».", Theme.TextPrimary);
        }
        else
        {
            SetStatus("Не удалось создать папку: " + res.ErrorMessage, Theme.Danger);
        }
    }

    private async Task DeleteSelectedRemoteAsync()
    {
        if (!_sftp.IsConnected || _busy) return;
        var selected = _remotePanel.SelectedEntries;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Выберите элемент на удалённой панели для удаления.",
                "MySsh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string desc = selected.Count == 1
            ? $"Удалить «{selected[0].Name}» на сервере?"
            : $"Удалить выбранные элементы ({selected.Count}) на сервере?";

        if (MessageBox.Show(this, desc, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        foreach (var item in selected)
        {
            var res = item.IsDirectory
                ? await _sftp.DeleteEmptyDirectoryAsync(item.FullPath)
                : await _sftp.DeleteFileAsync(item.FullPath);

            if (!res.Success)
            {
                SetStatus($"Ошибка удаления «{item.Name}»: {res.ErrorMessage}", Theme.Danger);
                break;
            }
        }

        await LoadRemoteDirectoryAsync(_remotePanel.CurrentPath);
    }

    private static string? PromptText(string title, string prompt, string defaultValue)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(360, 145),
            BackColor = Theme.WindowBack,
            ForeColor = Theme.TextPrimary,
            Font = new Font("Segoe UI", 10f)
        };

        var lbl = new Label { Text = prompt, Location = new Point(14, 14), AutoSize = true, ForeColor = Theme.TextMuted };
        var tb = new TextBox
        {
            Text = defaultValue,
            Location = new Point(14, 42),
            Width = 332,
            BackColor = Theme.InputBack,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new FlatButton
        {
            Text = "ОК",
            NormalColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            BackColor = Theme.Accent,
            DialogResult = DialogResult.OK,
            Location = new Point(166, 90),
            Width = 86,
            Height = 32
        };
        var cancel = new FlatButton
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(260, 90),
            Width = 86,
            Height = 32
        };

        dlg.Controls.AddRange([lbl, tb, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _btnDownload.Enabled = enabled;
        _btnUpload.Enabled = enabled;
        _btnMkdir.Enabled = enabled;
        _btnDelete.Enabled = enabled;
        _localPanel.Enabled = enabled;
        _remotePanel.Enabled = enabled;
    }

    private void SetStatus(string text, Color color)
    {
        if (_disposed || IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, color));
            return;
        }

        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
        StatusChanged?.Invoke(this, text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _cts.Cancel(); } catch { /* ignore */ }
            _cts.Dispose();
            _sftp.Dispose();
        }
        base.Dispose(disposing);
    }
}