using System.Security.Cryptography;
using System.Text;

namespace MySsh.Storage;

/// <summary>Шифрование паролей через DPAPI (привязка к текущему пользователю Windows).</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MySsh.Profile.v1");

    /// <summary>Зашифровать пароль. Возвращает Base64-строку.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(cipher);
    }

    /// <summary>Расшифровать пароль. Пусто/ошибка → пустая строка.</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return string.Empty;

        try
        {
            byte[] cipher = Convert.FromBase64String(protectedBase64);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (CryptographicException)
        {
            // Профиль создан другим пользователем Windows — расшифровать нельзя.
            return string.Empty;
        }
    }
}
