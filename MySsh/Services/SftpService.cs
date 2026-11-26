using System.Net.Sockets;
using System.Text;
using MySsh.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MySsh.Services;

/// <summary>Результат операции SFTP.</summary>
public readonly record struct SftpOperationResult(bool Success, string? ErrorMessage)
{
    public static SftpOperationResult Ok() => new(true, null);
    public static SftpOperationResult Fail(string message) => new(false, message);
}

/// <summary>Клиент SFTP на базе SSH.NET (логин + пароль).</summary>
public sealed class SftpService : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private SftpClient? _client;
    private bool _disposed;

    public bool IsConnected => !_disposed && _client?.IsConnected == true;

    public async Task<SftpOperationResult> ConnectAsync(
        string host,
        int port,
        string login,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return SftpOperationResult.Fail("SFTP-сессия закрыта.");

        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SftpOperationResult.Fail(
                $"Сервер {host}:{port} не отвечает (таймаут {(int)ConnectTimeout.TotalSeconds} сек).");
        }
        catch (SocketException ex)
        {
            return SftpOperationResult.Fail($"Сетевая ошибка ({ex.SocketErrorCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            return SftpOperationResult.Fail(ex.Message);
        }

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

                var client = new SftpClient(connectionInfo);
                client.Connect();

                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    try { client.Disconnect(); } catch { /* ignore */ }
                    client.Dispose();
                    return;
                }

                _client = client;
            }, cancellationToken).ConfigureAwait(false);

            return IsConnected
                ? SftpOperationResult.Ok()
                : SftpOperationResult.Fail("Не удалось установить SFTP-соединение.");
        }
        catch (SshAuthenticationException)
        {
            return SftpOperationResult.Fail("Ошибка авторизации: неверный логин или пароль.");
        }
        catch (Exception ex)
        {
            return SftpOperationResult.Fail(ex.Message);
        }
    }

    public Task<(bool Ok, string Path, List<FileEntryItem> Items, string? Error)> ListDirectoryAsync(
         string? remotePath,
         CancellationToken cancellationToken = default)
    {
        return Task.Run<(bool, string, List<FileEntryItem>, string?)>(() =>
        {
            if (!IsConnected || _client is null)
                return (false, remotePath ?? "/", [], "SFTP-соединение не установлено.");

            try
            {
                string targetPath;
                if (string.IsNullOrWhiteSpace(remotePath) || remotePath == ".")
                {
                    targetPath = string.IsNullOrWhiteSpace(_client.WorkingDirectory)
                        ? "/"
                        : _client.WorkingDirectory;
                }
                else
                {
                    targetPath = remotePath.Trim();
                    if (!targetPath.StartsWith('/'))
                    {
                        string baseDir = string.IsNullOrWhiteSpace(_client.WorkingDirectory)
                            ? "/"
                            : _client.WorkingDirectory;
                        targetPath = CombineRemotePath(baseDir, targetPath);
                    }
                }

                targetPath = NormalizeRemotePath(targetPath);

                var rawFiles = _client.ListDirectory(targetPath).ToList();
                string resolved = targetPath;

                var entries = new List<FileEntryItem>();
                if (resolved != "/")
                {
                    string parent = GetRemoteParent(resolved);
                    entries.Add(new FileEntryItem
                    {
                        Name = "..",
                        FullPath = parent,
                        IsDirectory = true,
                        IsParentLink = true
                    });
                }

                foreach (ISftpFile f in rawFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (f.Name == "." || f.Name == "..")
                        continue;

                    entries.Add(new FileEntryItem
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        IsDirectory = f.IsDirectory,
                        IsParentLink = false,
                        Size = f.IsDirectory ? 0 : f.Length,
                        LastWriteTime = f.LastWriteTimeUtc
                    });
                }

                var sorted = entries
                    .OrderBy(x => x.IsParentLink ? 0 : (x.IsDirectory ? 1 : 2))
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (true, resolved, sorted, null);
            }
            catch (OperationCanceledException)
            {
                return (false, remotePath ?? "/", [], "Операция отменена.");
            }
            catch (Exception ex)
            {
                return (false, remotePath ?? "/", [], ex.Message);
            }
        }, cancellationToken);
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        string p = path.Replace('\\', '/');
        if (!p.StartsWith('/'))
            p = "/" + p;

        var parts = new List<string>();
        foreach (string seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }

        return parts.Count == 0 ? "/" : "/" + string.Join("/", parts);
    }

    public Task<SftpOperationResult> CreateDirectoryAsync(string remoteDirPath)
    {
        return Task.Run(() =>
        {
            if (!IsConnected || _client is null)
                return SftpOperationResult.Fail("SFTP-соединение не установлено.");

            try
            {
                _client.CreateDirectory(remoteDirPath);
                return SftpOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return SftpOperationResult.Fail(ex.Message);
            }
        });
    }

    public Task<SftpOperationResult> DeleteFileAsync(string remoteFilePath)
    {
        return Task.Run(() =>
        {
            if (!IsConnected || _client is null)
                return SftpOperationResult.Fail("SFTP-соединение не установлено.");

            try
            {
                _client.DeleteFile(remoteFilePath);
                return SftpOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return SftpOperationResult.Fail(ex.Message);
            }
        });
    }

    public Task<SftpOperationResult> DeleteEmptyDirectoryAsync(string remoteDirPath)
    {
        return Task.Run(() =>
        {
            if (!IsConnected || _client is null)
                return SftpOperationResult.Fail("SFTP-соединение не установлено.");

            try
            {
                _client.DeleteDirectory(remoteDirPath);
                return SftpOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return SftpOperationResult.Fail(ex.Message);
            }
        });
    }

    public Task<SftpOperationResult> UploadFileAsync(
        string localFilePath,
        string remoteFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!IsConnected || _client is null)
                return SftpOperationResult.Fail("SFTP-соединение не установлено.");

            try
            {
                var info = new FileInfo(localFilePath);
                ulong total = info.Exists && info.Length > 0 ? (ulong)info.Length : 1UL;

                using var fs = File.OpenRead(localFilePath);
                _client.UploadFile(fs, remoteFilePath, canOverride: true, uploaded =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    progress?.Report(Math.Clamp(uploaded / (double)total * 100.0, 0, 100));
                });

                progress?.Report(100);
                return SftpOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return SftpOperationResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    public Task<SftpOperationResult> DownloadFileAsync(
        string remoteFilePath,
        string localFilePath,
        long knownSize,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!IsConnected || _client is null)
                return SftpOperationResult.Fail("SFTP-соединение не установлено.");

            try
            {
                ulong total = knownSize > 0 ? (ulong)knownSize : 1UL;
                using var fs = File.Create(localFilePath);
                _client.DownloadFile(remoteFilePath, fs, downloaded =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    progress?.Report(Math.Clamp(downloaded / (double)total * 100.0, 0, 100));
                });

                progress?.Report(100);
                return SftpOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return SftpOperationResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    public async Task<(bool Ok, string Content, string? Error)> ReadTextFileAsync(
        string remoteFilePath,
        long knownSize = 0,
        CancellationToken cancellationToken = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"myssh_edit_{Guid.NewGuid():N}.tmp");
        try
        {
            var dl = await DownloadFileAsync(remoteFilePath, tempPath, knownSize, null, cancellationToken)
                .ConfigureAwait(false);
            if (!dl.Success)
                return (false, string.Empty, dl.ErrorMessage ?? "Не удалось скачать файл.");

            string text = await File.ReadAllTextAsync(tempPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return (true, text, null);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    public async Task<SftpOperationResult> WriteTextFileAsync(
        string remoteFilePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"myssh_save_{Guid.NewGuid():N}.tmp");
        try
        {
            string normalized = content.Replace("\r\n", "\n");
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(tempPath, normalized, utf8NoBom, cancellationToken)
                .ConfigureAwait(false);

            return await UploadFileAsync(tempPath, remoteFilePath, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SftpOperationResult.Fail(ex.Message);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }
    }

    public static string CombineRemotePath(string basePath, string name)
    {
        if (string.IsNullOrEmpty(basePath) || basePath == "/")
            return "/" + name.TrimStart('/');
        return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public static string GetRemoteParent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";
        string trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? "/" : trimmed[..idx];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_client?.IsConnected == true)
                _client.Disconnect();
        }
        catch { /* ignore */ }

        try { _client?.Dispose(); } catch { /* ignore */ }
        _client = null;
    }
}