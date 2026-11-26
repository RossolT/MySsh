using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MySsh.Services;

/// <summary>Связка SshSessionService ↔ WebView2 (xterm.js) через postMessage.</summary>
public sealed class TerminalBridge : IDisposable
{
    private readonly Control _uiOwner;
    private SshSessionService? _session;
    private CoreWebView2? _webView;
    private bool _webReady;

    private readonly object _sync = new();
    private readonly List<byte> _pendingBytes = new();
    private readonly List<string> _queuedInfo = new();
    private System.Windows.Forms.Timer? _flushTimer;
    private bool _disposed;

    public TerminalBridge(Control uiOwner)
    {
        _uiOwner = uiOwner;
    }

    public event EventHandler<string>? Disconnected;

    /// <summary>Привязать или перепривязать активную SSH-сессию.</summary>
    public void BindSession(SshSessionService session)
    {
        UnbindSession();
        _session = session;
        _session.DataReceived += OnDataReceived;
        _session.Disconnected += OnDisconnected;
    }

    public void UnbindSession()
    {
        if (_session is null) return;
        _session.DataReceived -= OnDataReceived;
        _session.Disconnected -= OnDisconnected;
        _session = null;
    }

    /// <summary>Подключить мост к инициализированному WebView2 (вызывать из UI-потока).</summary>
    public void Attach(CoreWebView2 webView)
    {
        if (_webView is not null)
            _webView.WebMessageReceived -= OnWebMessageReceived;

        _webView = webView;
        _webView.WebMessageReceived += OnWebMessageReceived;

        _flushTimer ??= new System.Windows.Forms.Timer { Interval = 16 };
        _flushTimer.Tick -= OnFlushTimerTick;
        _flushTimer.Tick += OnFlushTimerTick;
        _flushTimer.Start();
    }

    private void OnFlushTimerTick(object? sender, EventArgs e) => Flush();

    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (_disposed) return;

        bool needImmediateFlush;
        lock (_sync)
        {
            _pendingBytes.AddRange(e.Data);
            needImmediateFlush = _pendingBytes.Count > 512 * 1024;
        }

        if (needImmediateFlush)
            MarshalToUi(Flush);
    }

    private void Flush()
    {
        if (_disposed || _webView is null || !_webReady)
            return;

        List<string> infoToSend;
        byte[]? dataChunk = null;

        lock (_sync)
        {
            infoToSend = _queuedInfo.Count > 0 ? new List<string>(_queuedInfo) : [];
            _queuedInfo.Clear();

            if (_pendingBytes.Count > 0)
            {
                dataChunk = _pendingBytes.ToArray();
                _pendingBytes.Clear();
            }
        }

        foreach (string info in infoToSend)
        {
            string escaped = EscapeJsSingleQuoted(info);
            _ = _webView.ExecuteScriptAsync(
                $"window.__mySshWriteText && window.__mySshWriteText('{escaped}');");
        }

        if (dataChunk is not null && dataChunk.Length > 0)
        {
            string b64 = Convert.ToBase64String(dataChunk);
            _ = _webView.ExecuteScriptAsync(
                $"window.__mySshWrite && window.__mySshWrite('{b64}');");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string kind = root.GetProperty("kind").GetString() ?? "";

            switch (kind)
            {
                case "input":
                    {
                        string payload = root.GetProperty("payload").GetString() ?? "";
                        _session?.Send(Encoding.UTF8.GetBytes(payload));
                        break;
                    }
                case "resize":
                    {
                        var p = root.GetProperty("payload");
                        uint cols = (uint)p.GetProperty("cols").GetInt32();
                        uint rows = (uint)p.GetProperty("rows").GetInt32();
                        _session?.Resize(cols, rows);
                        break;
                    }
                case "js_error":
                    {
                        string err = root.TryGetProperty("payload", out var pl)
                            ? pl.GetString() ?? "Ошибка скрипта"
                            : "Ошибка скрипта";
                        ShowInfo("JS-ошибка: " + err);
                        break;
                    }
                case "copy":
                    {
                        string selected = root.TryGetProperty("payload", out var pl)
                            ? pl.GetString() ?? ""
                            : "";
                        if (!string.IsNullOrEmpty(selected))
                        {
                            MarshalToUi(() =>
                            {
                                try { Clipboard.SetText(selected); }
                                catch { /* ignore */ }
                            });
                        }
                        break;
                    }
                case "paste_request":
                    {
                        MarshalToUi(() =>
                        {
                            try
                            {
                                if (!Clipboard.ContainsText()) return;
                                string text = Clipboard.GetText();
                                if (string.IsNullOrEmpty(text) || _webView is null) return;

                                string jsonText = JsonSerializer.Serialize(text);
                                _ = _webView.ExecuteScriptAsync(
                                    $"window.__mySshPasteText && window.__mySshPasteText({jsonText});");
                            }
                            catch { /* ignore */ }
                        });
                        break;
                    }
                case "ready":
                    _webReady = true;
                    Flush();
                    break;
            }
        }
        catch (JsonException)
        {
        }
        catch (KeyNotFoundException)
        {
        }
    }

    /// <summary>Служебное сообщение в терминал (серым).</summary>
    public void ShowInfo(string text)
    {
        if (_disposed) return;

        lock (_sync)
        {
            _queuedInfo.Add(text);
        }

        MarshalToUi(Flush);
    }

    private void OnDisconnected(object? sender, string reason)
    {
        ShowInfo("Сессия завершена: " + reason);
        MarshalToUi(() => Disconnected?.Invoke(this, reason));
    }

    private void MarshalToUi(Action action)
    {
        if (_disposed || _uiOwner.IsDisposed)
            return;

        if (_uiOwner.InvokeRequired)
        {
            try
            {
                _uiOwner.BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        else
        {
            action();
        }
    }

    private static string EscapeJsSingleQuoted(string text) =>
        text
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "")
            .Replace("\n", "\\r\\n");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnbindSession();

        if (_flushTimer is not null)
        {
            _flushTimer.Stop();
            _flushTimer.Tick -= OnFlushTimerTick;
            _flushTimer.Dispose();
            _flushTimer = null;
        }

        if (_webView is not null)
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
            _webView = null;
        }
    }
}