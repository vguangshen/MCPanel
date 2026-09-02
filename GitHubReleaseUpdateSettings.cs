using System.IO;
using System.Text.Json;

namespace MCPanel;

internal sealed class GitHubReleaseUpdateSource
{
    public GitHubReleaseUpdateSource(string repository, string accessToken)
    {
        Repository = repository;
        AccessToken = accessToken;
    }

    public string Repository { get; }
    public string AccessToken { get; }
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);
}

internal sealed class GitHubUpdateCredentialRecord
{
    public string AccessToken { get; set; } = string.Empty;
}

/// <summary>
/// Stores only the GitHub read token that is needed by the current Windows user.
/// The repository itself remains a normal deployment setting in MCPanel.exe.config;
/// this file is intentionally kept under StoreData so application updates preserve it.
/// </summary>
internal sealed class GitHubUpdateCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _credentialFile;

    public GitHubUpdateCredentialStore()
        : this(Path.Combine(AppContext.BaseDirectory, "StoreData", "Updates", "github-update-credential.json"))
    {
    }

    internal GitHubUpdateCredentialStore(string credentialFile)
    {
        if (string.IsNullOrWhiteSpace(credentialFile))
        {
            throw new ArgumentException("GitHub 更新凭据文件路径不能为空。", nameof(credentialFile));
        }

        _credentialFile = Path.GetFullPath(credentialFile);
    }

    public bool HasStoredCredential() => File.Exists(_credentialFile);

    public string LoadAccessToken()
    {
        if (!File.Exists(_credentialFile))
        {
            return string.Empty;
        }

        try
        {
            var record = JsonSerializer.Deserialize<GitHubUpdateCredentialRecord>(
                File.ReadAllText(_credentialFile),
                JsonOptions);
            if (record is null || string.IsNullOrWhiteSpace(record.AccessToken))
            {
                throw new InvalidDataException("GitHub 更新凭据文件内容无效，请重新配置访问令牌。");
            }

            return NormalizeAccessToken(LocalSecretProtector.Unprotect(record.AccessToken));
        }
        catch (LocalSecretUnavailableException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("GitHub 更新凭据文件无法读取，请重新配置访问令牌。", error);
        }
    }

    public void SaveAccessToken(string accessToken)
    {
        var normalized = NormalizeAccessToken(accessToken);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("请输入 GitHub 访问令牌。");
        }

        var record = new GitHubUpdateCredentialRecord
        {
            AccessToken = LocalSecretProtector.Protect(normalized)
        };
        AtomicFile.WriteAllText(_credentialFile, JsonSerializer.Serialize(record, JsonOptions));
    }

    public void ClearAccessToken()
    {
        try
        {
            if (File.Exists(_credentialFile))
            {
                File.Delete(_credentialFile);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法删除 GitHub 更新凭据。", error);
        }
    }

    internal static string NormalizeAccessToken(string? value)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            return string.Empty;
        }

        if (token.Length < 20 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
        {
            throw new InvalidDataException("GitHub 访问令牌格式无效。请粘贴完整令牌，不要包含空格或换行。");
        }

        return token;
    }
}
