using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MCPanel;

public sealed record MySqlDefaultCredentials(string Host, int Port, string UserName, string Password);

public static class MySqlCredentialStore
{
    public const int DefaultPort = 3380;

    // The original Store uses this password for a fresh custom MySQL package install.
    private const string OriginalDefaultPassword = "mike";

    public static MySqlDefaultCredentials Load()
    {
        foreach (var file in CandidateCredentialFiles())
        {
            try
            {
                if (File.Exists(file))
                {
                    var credentials = JsonSerializer.Deserialize<MySqlDefaultCredentials>(File.ReadAllText(file));
                    if (credentials is not null && !string.IsNullOrWhiteSpace(credentials.Password))
                    {
                        var wasProtected = LocalSecretProtector.IsProtected(credentials.Password);
                        var normalized = credentials with { Password = LocalSecretProtector.Unprotect(credentials.Password) };
                        if (string.IsNullOrWhiteSpace(normalized.Password))
                        {
                            continue;
                        }

                        if (!PathsEqual(file, CredentialFile))
                        {
                            Save(normalized);
                        }
                        else if (!wasProtected)
                        {
                            Save(normalized);
                        }

                        return WithConfiguredPort(normalized);
                    }
                }
            }
            catch (LocalSecretUnavailableException)
            {
                throw;
            }
            catch
            {
                // Keep compatibility with older installations that used the fixed password.
            }
        }

        return WithConfiguredPort(new MySqlDefaultCredentials("127.0.0.1", DefaultPort, "root", OriginalDefaultPassword));
    }

    public static MySqlDefaultCredentials CreateNew()
    {
        return new MySqlDefaultCredentials("127.0.0.1", DefaultPort, "root", OriginalDefaultPassword);
    }

    public static void Save(MySqlDefaultCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialFile)!);
        var stored = credentials with { Password = LocalSecretProtector.Protect(credentials.Password) };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(CredentialFile, json);
    }

    public static string FormatForDisplay()
    {
        var credentials = Load();
        return $"默认端口：{credentials.Port}{Environment.NewLine}" +
               $"MySQL 账号：{credentials.UserName}{Environment.NewLine}" +
               $"MySQL 密码：{credentials.Password}{Environment.NewLine}{Environment.NewLine}" +
               "以上信息已复制到剪贴板。";
    }

    public static string FormatForClipboard()
    {
        var credentials = Load();
        return $"Host={credentials.Host}{Environment.NewLine}" +
               $"Port={credentials.Port}{Environment.NewLine}" +
               $"User={credentials.UserName}{Environment.NewLine}" +
               $"Password={credentials.Password}";
    }

    public static string FormatForTooltip()
    {
        var credentials = Load();
        return $"MySQL 默认信息{Environment.NewLine}" +
               $"地址：{credentials.Host}{Environment.NewLine}" +
               $"端口：{credentials.Port}{Environment.NewLine}" +
               $"账号：{credentials.UserName}{Environment.NewLine}" +
               $"密码：{credentials.Password}";
    }

    private static string CredentialFile => Path.Combine(ComponentPaths.RuntimeStateRoot, "mysql-default.json");

    private static IEnumerable<string> CandidateCredentialFiles()
    {
        yield return CredentialFile;

        var baseDirectory = ComponentPaths.ApplicationRoot.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Directory.GetParent(baseDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            yield return Path.Combine(parent, "StoreData", "RuntimeState", "mysql-default.json");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static MySqlDefaultCredentials WithConfiguredPort(MySqlDefaultCredentials credentials)
    {
        foreach (var file in CandidateMySqlConfigFiles())
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var section = string.Empty;
                foreach (var line in File.ReadLines(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                        trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = trimmed;
                        continue;
                    }

                    if (!section.Equals("[mysqld]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var separator = trimmed.IndexOf('=');
                    if (separator <= 0 ||
                        !trimmed.Substring(0, separator).Trim().Equals("port", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = trimmed.Substring(separator + 1).Trim();
                    var comment = value.IndexOfAny(['#', ';']);
                    if (comment >= 0)
                    {
                        value = value.Substring(0, comment).Trim();
                    }

                    if (int.TryParse(value, out var port) &&
                        port is > 0 and <= 65535)
                    {
                        return credentials with { Port = port };
                    }
                }
            }
            catch
            {
                // A damaged or temporarily locked my.ini must not hide the
                // persisted connection information from the panel.
            }
        }

        return credentials;
    }

    private static IEnumerable<string> CandidateMySqlConfigFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceExecutable = ReadServiceExecutablePath();
        if (!string.IsNullOrWhiteSpace(serviceExecutable))
        {
            var serviceRoot = Directory.GetParent(Path.GetDirectoryName(serviceExecutable!)!)?.FullName;
            if (!string.IsNullOrWhiteSpace(serviceRoot) && seen.Add(Path.Combine(serviceRoot, "my.ini")))
            {
                yield return Path.Combine(serviceRoot, "my.ini");
            }
        }

        foreach (var root in ComponentPaths.MySqlSearchRoots)
        {
            var file = Path.Combine(root, "my.ini");
            if (seen.Add(file))
            {
                yield return file;
            }
        }
    }

    private static string? ReadServiceExecutablePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MySQL80");
            var imagePath = key?.GetValue("ImagePath")?.ToString();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(imagePath!.Trim());
            if (expanded.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = expanded.IndexOf('"', 1);
                return end > 1 ? expanded.Substring(1, end - 1) : null;
            }

            var exeEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd >= 0 ? expanded.Substring(0, exeEnd + 4).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
