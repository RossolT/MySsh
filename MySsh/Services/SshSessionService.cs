using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySsh.Services;

/// <summary>Событие поступления данных из SSH-сессии.</summary>
public sealed class DataReceivedEventArgs : EventArgs
{
    public required byte[] Data { get; init; }
}

/// <summary>Результат попытки подключения к SSH-серверу.</summary>
public readonly record struct SshConnectResult(bool Success, string? ErrorMessage)
{
    public static SshConnectResult Ok() => new(true, null);
    public static SshConnectResult Fail(string message) => new(false, message);
}

/// <summary>Активная SSH-сессия: ShellStream с PTY, чтение/запись, resize.</summary>
public sealed class SshSessionService : IDisposable
{
    private const string TerminalName = "xterm-256color";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private bool _disposed;

    private uint _cols = 80;
    private uint _rows = 24;

    /// <summary>Данные из сокета (сырые байты, включая ANSI).</summary>
    public event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>Соединение закрылось или разорвалось.</summary>
    public event EventHandler<string>? Disconnected;

    public bool IsConnected => _client?.IsConnected == true && _shell is not null;

    /// <summary>Асинхронно установить соединение и открыть интерактивный shell без падения по таймауту сокета.</summary>
    public async Task<SshConnectResult> ConnectAsync(
        string host,
        int port,
        string login,
        string password,
        CancellationToken externalToken = default)
    {
        if (_disposed)
            return SshConnectResult.Fail("Сессия уже закрыта.");

        // 1. Быстрая проверка доступности хоста и порта по TCP до запуска SSH.NET,
        // чтобы не ловить долгий SshOperationTimeoutException внутри Renci.SshNet.
        var tcpCheck = await CheckTcpReachableAsync(host, port, ConnectTimeout, externalToken)
            .ConfigureAwait(false);
        if (!tcpCheck.Success)
            return tcpCheck;

        if (_disposed || externalToken.IsCancellationRequested)
            return SshConnectResult.Fail("Подключение отменено.");

        // 2. SSH-рукопожатие и аутентификация.
        try
        {
            await Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(
                    host,
                    port,
                    login,
                    new PasswordAuthenticationMethod(login, password))
                {
                    Encoding = Encoding.UTF8,
                    Timeout = ConnectTimeout
                };

                var client = new SshClient(connectionInfo);
                client.Connect();

                if (_disposed || externalToken.IsCancellationRequested)
                {
                    try { client.Disconnect(); } catch { /* ignore */ }
                    client.Dispose();
                    return;
                }

                _client = client;
                _shell = _client.CreateShellStream(
                    TerminalName,
                    _cols,
                    _rows,
                    width: 0,
                    height: 0,
                    bufferSize: 4096);
            }, externalToken).ConfigureAwait(false);

            if (_disposed || externalToken.IsCancellationRequested)
                return SshConnectResult.Fail("Подключение отменено.");

            _cts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
            return SshConnectResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return SshConnectResult.Fail("Подключение отменено.");
        }
        catch (Exception ex)
        {
            return SshConnectResult.Fail(FormatFriendlyError(ex));
        }
    }

    private static async Task<SshConnectResult> CheckTcpReachableAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken externalToken)
    {
        using var tcp = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return SshConnectResult.Ok();
        }
        catch (OperationCanceledException) when (!externalToken.IsCancellationRequested)
        {
            return SshConnectResult.Fail(
                $"Сервер {host}:{port} не отвечает (таймаут {(int)timeout.TotalSeconds} сек). Проверьте адрес, порт и фаервол.");
        }
        catch (OperationCanceledException)
        {
            return SshConnectResult.Fail("Подключение отменено.");
        }
        catch (SocketException sockEx)
        {
            return SshConnectResult.Fail(FormatSocketError(sockEx, host, port));
        }
        catch (Exception ex)
        {
            return SshConnectResult.Fail(FormatFriendlyError(ex));
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];

        try
        {
            while (!token.IsCancellationRequested && _shell is not null)
            {
                int read = await _shell.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                                        .ConfigureAwait(false);
                if (read <= 0)
                    break;

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                DataReceived?.Invoke(this, new DataReceivedEventArgs { Data = chunk });
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested)
                Disconnected?.Invoke(this, FormatFriendlyError(ex));
            return;
        }

        if (!_disposed && !token.IsCancellationRequested)
            Disconnected?.Invoke(this, "Соединение закрыто сервером.");
    }

    /// <summary>Отправить данные (набор с клавиатуры) в сессию.</summary>
    public void Send(byte[] data)
    {
        if (_disposed || _shell is null) return;

        try
        {
            _shell.Write(data, 0, data.Length);
            _shell.Flush();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException ex)
        {
            if (!_disposed)
                Disconnected?.Invoke(this, FormatFriendlyError(ex));
        }
    }

    /// <summary>Сообщить серверу новый размер терминала (ncurses: mc/vim).</summary>
    public void Resize(uint cols, uint rows)
    {
        if (_disposed || cols == 0 || rows == 0) return;
        _cols = cols;
        _rows = rows;

        try
        {
            _shell?.ChangeWindowSize(cols, rows, 0, 0);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static string FormatFriendlyError(Exception ex)
    {
        Exception root = ex;
        while (root.InnerException is not null)
            root = root.InnerException;

        if (root is SshAuthenticationException)
            return "Ошибка авторизации: неверный логин или пароль.";

        if (root is SshOperationTimeoutException)
            return "Превышено время ожидания ответа от SSH-сервера.";

        if (root is SocketException sockEx)
            return FormatSocketError(sockEx, null, 0);

        if (root is SshConnectionException)
            return $"Ошибка SSH-соединения: {root.Message}";

        return root.Message;
    }

    private static string FormatSocketError(SocketException sockEx, string? host, int port)
    {
        string target = string.IsNullOrWhiteSpace(host) ? "сервер" : $"{host}:{port}";
        return sockEx.SocketErrorCode switch
        {
            SocketError.TimedOut =>
                $"Сервер {target} не отвечает (таймаут). Проверьте адрес, порт, сеть и фаервол.",
            SocketError.ConnectionRefused =>
                $"Сервер {target} отклонил подключение (порт закрыт или SSH-служба не запущена).",
            SocketError.HostNotFound or SocketError.NoData =>
                $"Не удалось определить адрес хоста{(string.IsNullOrWhiteSpace(host) ? "" : $" «{host}»")}. Проверьте имя сервера и DNS.",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                $"Сеть или узел {target} недостижимы. Проверьте интернет или VPN.",
            _ => $"Сетевая ошибка ({sockEx.SocketErrorCode}): {sockEx.Message}"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _shell?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_client?.IsConnected == true)
                _client.Disconnect();
        }
        catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }

        _shell = null;
        _client = null;

        try { _readLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}