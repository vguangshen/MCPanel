using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Microsoft.Win32;

namespace MCPanel;

public sealed record SqlServerDefaultCredentials(string Host, int Port, string UserName, string Password, string InstanceName);

public sealed record SqlServerInstalledConnection(string Host, string InstanceName, int? Port)
{
    public string ServerTarget => Port.HasValue && Port.Value > 0
        ? $"{Host},{Port.Value}"
        : string.Equals(InstanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
            ? Host
            : $"{Host}\\{InstanceName}";
}

public static class SqlServerCredentialStore
{
    // Matches the original Store format: "it" + 8 uppercase letters/digits + "8".
    private const string OriginalPasswordChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    // Do not use a fixed fallback password here.  A new installation must use the
    // same generated format as the original Store; an already persisted password
    // is still loaded unchanged so existing SQL Server logins are not rotated
    // without an explicit user action.
    private static readonly SqlServerDefaultCredentials Defaults = CreateGeneratedDefaults();

    public static SqlServerDefaultCredentials Load()
    {
        var credentials = LoadPersistedCredentials();
        var installed = TryReadInstalledConnection();
        if (installed is null)
        {
            return credentials;
        }

        // Keep the saved SQL login/password, but always prefer the instance and
        // port that SQL Server currently advertises in the machine registry.
        return credentials with
        {
            Host = installed.Host,
            Port = installed.Port ?? credentials.Port,
            InstanceName = installed.InstanceName
        };
    }

    public static SqlServerInstalledConnection? TryReadInstalledConnection()
    {
        try
        {
            SqlServerInstalledConnection? fallback = null;
            foreach (var view in RegistryViews())
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var instances = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (instances is null)
                {
                    continue;
                }

                var instanceNames = instances.GetValueNames()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => string.Equals(name, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
                foreach (var instanceName in instanceNames)
                {
                    var instanceId = Convert.ToString(instances.GetValue(instanceName), CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        continue;
                    }

                    using var tcp = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Microsoft SQL Server\{instanceId}\MSSQLServer\SuperSocketNetLib\Tcp\IPAll");
                    var candidate = new SqlServerInstalledConnection("localhost", instanceName, ReadPort(tcp));
                    if (string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }

                    fallback ??= candidate;
                }
            }

            return fallback;
        }
        catch
        {
            // Registry access is only an enhancement. Keep the persisted connection
            // information usable when a legacy machine denies registry access.
        }

        return null;
    }

    public static string GetServerTarget(SqlServerDefaultCredentials credentials)
    {
        return TryReadInstalledConnection()?.ServerTarget ??
               $"{NormalizeLocalHost(credentials.Host)},{credentials.Port}";
    }

    public static void SaveDefault() => Save(Defaults);

    public static SqlServerDefaultCredentials CreateNew() => CreateGeneratedDefaults();

    public static void Save(SqlServerDefaultCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialFile)!);
        var normalized = credentials with
        {
            Host = NormalizeLocalHost(credentials.Host),
            Password = LocalSecretProtector.Protect(credentials.Password)
        };
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(CredentialFile, json);
    }

    public static string FormatForDisplay()
    {
        var credentials = Load();
        return $"默认端口：{credentials.Port}{Environment.NewLine}" +
               $"SQL Server 实例：{credentials.InstanceName}{Environment.NewLine}" +
               $"SQL Server 账号：{credentials.UserName}{Environment.NewLine}" +
               $"SQL Server 密码：{credentials.Password}{Environment.NewLine}{Environment.NewLine}" +
               "以上信息已复制到剪贴板。";
    }

    public static string FormatForClipboard()
    {
        var credentials = Load();
        return $"Server={GetServerTarget(credentials)}{Environment.NewLine}" +
               $"Instance={credentials.InstanceName}{Environment.NewLine}" +
               $"User={credentials.UserName}{Environment.NewLine}" +
               $"Password={credentials.Password}";
    }

    public static string FormatForTooltip()
    {
        var credentials = Load();
        return $"SQL Server 默认信息{Environment.NewLine}" +
               $"地址：{GetServerTarget(credentials)}{Environment.NewLine}" +
               $"实例：{credentials.InstanceName}{Environment.NewLine}" +
               $"账号：{credentials.UserName}{Environment.NewLine}" +
               $"密码：{credentials.Password}";
    }

    private static SqlServerDefaultCredentials LoadPersistedCredentials()
    {
        try
        {
            var file = CredentialFile;
            if (File.Exists(file))
            {
                var credentials = JsonSerializer.Deserialize<SqlServerDefaultCredentials>(File.ReadAllText(file));
                if (credentials is not null && !string.IsNullOrWhiteSpace(credentials.Password))
                {
                    var wasProtected = LocalSecretProtector.IsProtected(credentials.Password);
                    var normalized = credentials with
                    {
                        Host = NormalizeLocalHost(credentials.Host),
                        Password = LocalSecretProtector.Unprotect(credentials.Password)
                    };
                    if (!string.IsNullOrWhiteSpace(normalized.Password))
                    {
                        if (!wasProtected)
                        {
                            Save(normalized);
                        }

                        return normalized;
                    }
                }
            }
        }
        catch (LocalSecretUnavailableException)
        {
            throw;
        }
        catch
        {
            // Keep using built-in defaults when the local state file is missing or damaged.
        }

        return Defaults;
    }

    private static IEnumerable<RegistryView> RegistryViews()
    {
        var seen = new HashSet<RegistryView>();
        if (Environment.Is64BitOperatingSystem && seen.Add(RegistryView.Registry64))
        {
            yield return RegistryView.Registry64;
        }

        if (seen.Add(RegistryView.Registry32))
        {
            yield return RegistryView.Registry32;
        }

        if (seen.Add(RegistryView.Default))
        {
            yield return RegistryView.Default;
        }
    }

    private static int? ReadPort(RegistryKey? tcp)
    {
        if (tcp is null)
        {
            return null;
        }

        foreach (var valueName in new[] { "TcpPort", "TcpDynamicPorts" })
        {
            var value = Convert.ToString(tcp.GetValue(valueName), CultureInfo.InvariantCulture);
            foreach (var token in (value ?? string.Empty).Split(';'))
            {
                if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
                    port > 0 && port <= 65535)
                {
                    return port;
                }
            }
        }

        return null;
    }

    private static string NormalizeLocalHost(string host) =>
        host is "127.0.0.1" or "::1" ? "localhost" : host;

    private static SqlServerDefaultCredentials CreateGeneratedDefaults() =>
        new("localhost", 1433, "sa", GenerateOriginalPassword(), "MSSQLSERVER");

    private static string GenerateOriginalPassword()
    {
        var buffer = new char[8];
        var bytes = new byte[4];
        using var random = RandomNumberGenerator.Create();
        var limit = uint.MaxValue - (uint.MaxValue % (uint)OriginalPasswordChars.Length);

        for (var i = 0; i < buffer.Length; i++)
        {
            uint value;
            do
            {
                random.GetBytes(bytes);
                value = BitConverter.ToUInt32(bytes, 0);
            }
            while (value >= limit);

            buffer[i] = OriginalPasswordChars[(int)(value % (uint)OriginalPasswordChars.Length)];
        }

        return "it" + new string(buffer) + "8";
    }

    private static string CredentialFile => Path.Combine(AppContext.BaseDirectory, "StoreData", "RuntimeState", "sqlserver-default.json");
}
