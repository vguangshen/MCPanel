using System.IO;
using System.Text.Json;

namespace MCPanel;

/// <summary>
/// A JSON credential record whose password is stored encrypted with
/// <see cref="LocalSecretProtector"/> (used by the MySQL and SQL Server stores).
/// </summary>
internal static class ProtectedCredentialFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Writes <paramref name="credentials"/> with its password encrypted, atomically.</summary>
    public static void Save<T>(string path, T credentials, Func<T, string> password, Func<T, string, T> withPassword)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = withPassword(credentials, LocalSecretProtector.Protect(password(credentials)));
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(stored, Indented));
    }

    /// <summary>
    /// Reads a record and decrypts its password. <c>WasProtected</c> is false
    /// for an older plain-text file, which the caller should save again.
    /// Throws <see cref="InvalidDataException"/> for an unusable record and
    /// lets <see cref="LocalSecretUnavailableException"/> through.
    /// </summary>
    public static (T Credentials, bool WasProtected) Read<T>(
        string path,
        Func<T, string> password,
        Func<T, string, T> withPassword,
        string invalidMessage,
        string emptyPasswordMessage)
        where T : class
    {
        var credentials = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        if (credentials is null || string.IsNullOrWhiteSpace(password(credentials)))
        {
            throw new InvalidDataException(invalidMessage);
        }

        var stored = password(credentials);
        var plain = withPassword(credentials, LocalSecretProtector.Unprotect(stored));
        if (string.IsNullOrWhiteSpace(password(plain)))
        {
            throw new InvalidDataException(emptyPasswordMessage);
        }

        return (plain, LocalSecretProtector.IsProtected(stored));
    }
}
