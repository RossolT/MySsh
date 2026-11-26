using Windows.Security.Credentials.UI;

namespace MySsh.Services;

/// <summary>Проверка доступности и запрос подтверждения через Windows Hello (PIN / биометрия).</summary>
public static class WindowsHelloPrompt
{
    public static async Task<bool> IsAvailableAsync()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> VerifyIfEnabledAsync(IWin32Window owner, string message)
    {
        if (!await IsAvailableAsync())
            return true;

        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }
}