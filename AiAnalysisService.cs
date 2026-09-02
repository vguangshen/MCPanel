using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MCPanel;

public sealed record AiProviderSettings
{
    public const string ProviderConfigKey = "Ai.Provider";
    public const string EndpointConfigKey = "Ai.Endpoint";
    public const string ModelConfigKey = "Ai.Model";
    public const string ApiKeyConfigKey = "Ai.ApiKey";

    public const string DefaultProvider = "GLM";
    public const string DefaultEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public const string DefaultModel = "glm-5.2";

    public static IReadOnlyList<string> ConfigKeys { get; } =
    [
        ProviderConfigKey,
        EndpointConfigKey,
        ModelConfigKey,
        ApiKeyConfigKey
    ];

    public string Provider { get; init; } = DefaultProvider;
    public string Endpoint { get; init; } = DefaultEndpoint;
    public string Model { get; init; } = DefaultModel;
    public string ApiKey { get; init; } = string.Empty;

    public static AiProviderSettings Default { get; } = new();

    public static AiProviderSettings FromAppSettings(NameValueCollection appSettings)
    {
        return new AiProviderSettings
        {
            Provider = Read(appSettings, ProviderConfigKey, DefaultProvider),
            Endpoint = Read(appSettings, EndpointConfigKey, DefaultEndpoint),
            Model = Read(appSettings, ModelConfigKey, DefaultModel),
            ApiKey = Read(appSettings, ApiKeyConfigKey, string.Empty)
        };
    }

    private static string Read(NameValueCollection appSettings, string key, string fallback)
    {
        try
        {
            var value = appSettings[key]?.Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value!;
        }
        catch
        {
            return fallback;
        }
    }
}

public sealed class AiAnalysisService
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public AiProviderSettings LoadSettings()
    {
        try
        {
            return AiProviderSettings.FromAppSettings(ConfigurationManager.AppSettings);
        }
        catch
        {
            return AiProviderSettings.Default;
        }
    }

    public async Task<string> AnalyzeAsync(
        string componentName,
        string logText,
        string userDescription,
        CancellationToken cancellationToken = default)
    {
        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("尚未配置模型 API Key，请在 MCPanel.exe.config 的 Ai.ApiKey 中填写。\n配置修改后重新开始分析即可生效。");
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException("MCPanel.exe.config 中的 Ai.Endpoint 不能为空。");
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new InvalidOperationException("MCPanel.exe.config 中的 Ai.Endpoint 必须使用 HTTPS；仅 localhost/127.0.0.1 允许使用 HTTP。");
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("MCPanel.exe.config 中的 Ai.Model 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(logText))
        {
            throw new InvalidOperationException("没有采集到可分析的日志。");
        }

        var prompt = $$"""
            你正在诊断服务器上的 MCPanel 组件“{{componentName}}”。
            请严格根据日志给出中文分析，不要虚构日志中不存在的事实。

            用户补充现象：
            {{(string.IsNullOrWhiteSpace(userDescription) ? "无" : userDescription.Trim())}}

            日志：
            {{logText}}

            请按以下结构回答：
            1. 结论摘要
            2. 最可能的根因（按可能性排序，并引用关键日志）
            3. 可直接执行的修复步骤
            4. 验证修复是否成功的方法
            5. 风险与回滚建议

            如果日志不足，请明确列出还需要哪些日志或系统信息。
            """;

        var payload = new
        {
            model = settings.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是一名资深 Windows、IIS、Tomcat、Nginx、MySQL、SQL Server 与 FRP 运维工程师。回答应准确、可执行，并优先保护现有数据。"
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            stream = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseText = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AI 服务请求失败：{(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{TrimForMessage(responseText)}");
        }

        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("模型服务没有返回可识别的分析结果。请检查接口是否兼容 Chat Completions 格式。");
        }

        var content = choices[0].GetProperty("message").GetProperty("content");
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                Environment.NewLine,
                content.EnumerateArray()
                    .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : item.ToString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => content.ToString()
        };
    }

    private static string TrimForMessage(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 800 ? trimmed : $"{trimmed.Substring(0, 800)}...";
    }

    private static async Task<string> ReadResponseTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > MaxResponseBytes)
        {
            throw new InvalidDataException("AI 服务返回内容过大，已停止读取以避免占用过多内存。");
        }

        using var stream = await content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("AI 服务返回内容过大，已停止读取以避免占用过多内存。");
            }

            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Most compatible Chat Completions services return UTF-8; fall back
                // gracefully when a provider sends an unknown charset label.
            }
        }

        return encoding.GetString(buffer.ToArray());
    }
}
