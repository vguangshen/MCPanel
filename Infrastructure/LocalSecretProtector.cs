using System.Security.Cryptography;
using System.Text;

namespace MCPanel;

internal sealed class LocalSecretUnavailableException : InvalidOperationException
{
    public LocalSecretUnavailableException(Exception innerException)
        : base("本机加密凭据无法解密。该配置可能来自另一台服务器或另一个 Windows 用户，请重新填写并保存连接凭据。", innerException)
    {
    }
}

internal static class LocalSecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MCPanel.LocalSecrets.v1");
    // Keep decrypting credentials written by pre-MCPanel builds without exposing the old product name.
    private static readonly byte[] LegacyEntropy = Convert.FromBase64String("SVRNQ01vZGVybi5Mb2NhbFNlY3JldHMudjE=");

    public static bool IsProtected(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value!.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value))
        {
            return value;
        }

        var clearBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value!.Substring(Prefix.Length));
            byte[] clearBytes;
            try
            {
                clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                clearBytes = ProtectedData.Unprotect(protectedBytes, LegacyEntropy, DataProtectionScope.CurrentUser);
            }
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (CryptographicException ex)
        {
            throw new LocalSecretUnavailableException(ex);
        }
        catch (FormatException ex)
        {
            throw new LocalSecretUnavailableException(ex);
        }
    }
}
