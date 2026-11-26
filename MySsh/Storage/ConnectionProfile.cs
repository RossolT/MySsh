namespace MySsh.Storage;

/// <summary>Тип подключения профиля.</summary>
public enum ConnectionKind
{
    Ssh = 0,
    Sftp = 1
}

/// <summary>Сохранённый профиль подключения (пароль хранится только в защищённом виде).</summary>
public sealed class ConnectionProfile
{
    /// <summary>Стабильный идентификатор профиля.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Понятное имя подключения.</summary>
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Login { get; set; } = string.Empty;

    public ConnectionKind Kind { get; set; } = ConnectionKind.Ssh;

    /// <summary>Пароль, зашифрованный DPAPI и закодированный в Base64. Пусто — пароль не сохранён.</summary>
    public string ProtectedPassword { get; set; } = string.Empty;

    public bool HasPassword => !string.IsNullOrEmpty(ProtectedPassword);

    public ConnectionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        Login = Login,
        Kind = Kind,
        ProtectedPassword = ProtectedPassword
    };
}
