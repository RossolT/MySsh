using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MySsh.Services;

namespace MySsh.Controls;

/// <summary>Вкладка SSH-сессии: панель статуса + xterm.js в WebView2 + SSH-соединение.</summary>
public sealed class SshSessionTabControl : UserControl, IDisposable
{
    private readonly WebView2 _webView = new();
    private readonly TerminalBridge _bridge;
    private readonly Panel _topBar = new();
    private readonly Label _lblStatus = new();
    private readonly FlatButton _btnReconnect = new() { Text = "Повторить", Width = 105, Height = 26 };

    private readonly string _host;
    private readonly int _port;
    private readonly string _login;
    private readonly string _password;

    private SshSessionService? _session;
    private CancellationTokenSource? _connectCts;
    private bool _webInitialized;
    private bool _connecting;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public SshSessionTabControl(string host, int port, string login, string password)
    {
        _host = host;
        _port = port;
        _login = login;
        _password = password;

        _bridge = new TerminalBridge(this);
        _bridge.Disconnected += OnSessionDisconnected;

        Dock = DockStyle.Fill;
        BackColor = Theme.WindowBack;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        ConfigureTopBar();

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Theme.WindowBack;

        // В WinForms элемент с Dock.Fill добавляется в Controls ПЕРВЫМ,
        // а элементы с Dock.Top/Bottom — ПОСЛЕ него, иначе они перекрываются.
        Controls.Add(_webView);
        Controls.Add(_topBar);

        Load += async (_, _) => await StartOrReconnectAsync();
    }

    private void ConfigureTopBar()
    {
        _topBar.Dock = DockStyle.Top;
        _topBar.Height = 36;
        _topBar.BackColor = Theme.PanelBack;
        _topBar.Padding = new Padding(12, 4, 12, 4);

        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.ForeColor = Theme.TextMuted;
        _lblStatus.Text = $"Подключение к {_login}@{_host}:{_port}…";

        _btnReconnect.Dock = DockStyle.Right;
        _btnReconnect.Visible = false;
        _btnReconnect.NormalColor = Theme.Accent;
        _btnReconnect.HoverColor = Theme.AccentHover;
        _btnReconnect.Click += async (_, _) => await StartOrReconnectAsync();

        _topBar.Controls.Add(_lblStatus);
        _topBar.Controls.Add(_btnReconnect);
    }

    private async Task StartOrReconnectAsync()
    {
        if (_disposed || _connecting) return;
        _connecting = true;

        SetStatus($"Подключение к {_login}@{_host}:{_port}…", Theme.TextMuted, showReconnect: false);

        if (!_webInitialized)
        {
            _webInitialized = true;
            if (!await EnsureWebViewReadyAsync())
            {
                _connecting = false;
                return;
            }
        }

        ReplaceSession();
        var session = new SshSessionService();
        _session = session;
        _bridge.BindSession(session);

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        _bridge.ShowInfo($"Подключение к {_login}@{_host}:{_port}…");

        var result = await session.ConnectAsync(_host, _port, _login, _password, _connectCts.Token);

        _connecting = false;

        if (_disposed || _session != session)
            return;

        if (result.Success)
        {
            SetStatus($"Подключено: {_login}@{_host}:{_port}", Theme.TextPrimary, showReconnect: false);
            _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                "window.__mySshForceResize && window.__mySshForceResize();");
            _webView.Focus();
        }
        else
        {
            string errorText = result.ErrorMessage ?? "Не удалось установить соединение.";
            _bridge.ShowInfo("Ошибка подключения: " + errorText);
            SetStatus("Ошибка: " + errorText, Theme.Danger, showReconnect: true);
        }
    }

    private async Task<bool> EnsureWebViewReadyAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFatal("Не установлен компонент WebView2 Runtime.\n" +
                      "Установите его: https://developer.microsoft.com/microsoft-edge/webview2/");
            return false;
        }

        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-background-networking " +
                    "--disable-component-update " +
                    "--disable-features=msSmartScreenProtection,Translate"
            };

            string userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MySsh",
                "WebView2Data");

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataDir,
                options: options);

            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowFatal("Не удалось запустить WebView2:\n" + ex.Message);
            return false;
        }

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        _bridge.Attach(core);

        string webDir = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
        string htmlPath = Path.Combine(webDir, "terminal.html");

        if (!File.Exists(htmlPath))
        {
            ShowFatal("Не найдены файлы терминала:\n" + htmlPath);
            return false;
        }

        core.SetVirtualHostNameToFolderMapping(
            "myssh.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

        core.Navigate("https://myssh.local/terminal.html");
        return true;
    }

    private void OnSessionDisconnected(object? sender, string reason)
    {
        if (_disposed) return;
        SetStatus("Отключено: " + reason, Theme.Danger, showReconnect: true);
    }

    private void SetStatus(string text, Color color, bool showReconnect)
    {
        if (_disposed || IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, color, showReconnect));
            return;
        }

        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
        _btnReconnect.Visible = showReconnect;
        StatusChanged?.Invoke(this, text);
    }

    private void ReplaceSession()
    {
        _bridge.UnbindSession();
        _session?.Dispose();
        _session = null;
    }

    private void ShowFatal(string message)
    {
        _webView.Visible = false;
        SetStatus("Ошибка терминала", Theme.Danger, showReconnect: false);

        var label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Danger,
            BackColor = Theme.WindowBack,
            Padding = new Padding(20),
            Text = message
        };
        Controls.Add(label);
        label.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _connectCts?.Cancel(); } catch { /* ignore */ }
            _connectCts?.Dispose();
            _bridge.Disconnected -= OnSessionDisconnected;
            _bridge.Dispose();
            _session?.Dispose();
            _session = null;
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}