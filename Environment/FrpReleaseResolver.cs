using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCPanel;

internal sealed record FrpRelease(string Version, string PackageUrl, string OfficialUrl, string? Sha256 = null)
{
    public string PackageFileName => $"frp_{Version}_windows_amd64.zip";
    public string ArchiveRoot => $"frp_{Version}_windows_amd64";
    public IEnumerable<string> DownloadUrls => new[] { PackageUrl, OfficialUrl }.Distinct(StringComparer.Ordinal);
}

/// <summary>Resolve once per installation; never substitute a pinned or cached older release.</summary>
internal static class FrpReleaseResolver
{
    internal const string LatestReleaseApi = "https://api.github.com/repos/fatedier/frp/releases/latest";
    private static readonly Regex PackageNamePattern = new(
        @"^frp_(?<version>[0-9]+\.[0-9]+\.[0-9]+)_windows_amd64\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LinkPattern = new(
        "href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<FrpRelease> ResolveAsync(
        HttpClient client, string configuredUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new Uri(configuredUrl, UriKind.Absolute);
        Exception upstreamError;
        try
        {
            // GitHub defines the latest stable release even while a mirror is still syncing.
            var json = await ReadMetadataAsync(client, new Uri(LatestReleaseApi), cancellationToken);
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            {
                throw new InvalidDataException("FRP 最新发布不是稳定版本。");
            }

            var version = release.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (version is null || !PackageNamePattern.IsMatch($"frp_{version}_windows_amd64.zip"))
            {
                throw new InvalidDataException("无法识别 FRP 最新版本号。");
            }

            var fileName = $"frp_{version}_windows_amd64.zip";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != fileName) continue;
                var digest = asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var sha256 = digest is not null && Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$")
                    ? digest.Substring(7) : null;
                return CreateRelease(source, version, sha256);
            }

            throw new NotSupportedException("FRP 最新稳定版中没有 Windows amd64 安装包，已取消安装。");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsMetadataFailure(ex))
        {
            upstreamError = ex;
        }

        try
        {
            // The rolling NJU directory also works when the GitHub API is unreachable or rate limited.
            var listing = await ReadMetadataAsync(client, new Uri(source, "."), cancellationToken);
            var version = ParseLatestMirrorVersion(listing);
            return CreateRelease(source, version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsMetadataFailure(ex))
        {
            throw new InvalidOperationException(
                "无法确认 FRP 最新稳定版本。请检查网络或下载源后重试；不会使用旧版本缓存继续安装。",
                new AggregateException(upstreamError, ex));
        }
    }

    internal static string ParseLatestMirrorVersion(string listing)
    {
        var versions = new List<Version>();
        foreach (Match link in LinkPattern.Matches(listing))
        {
            var href = WebUtility.HtmlDecode(link.Groups["url"].Value);
            var fileName = href.Substring(href.LastIndexOf('/') + 1);
            var match = PackageNamePattern.Match(fileName);
            if (match.Success && Version.TryParse(match.Groups["version"].Value, out var version))
            {
                versions.Add(version);
            }
        }

        return versions.OrderByDescending(version => version).FirstOrDefault()?.ToString()
            ?? throw new InvalidDataException("FRP 镜像目录中没有稳定版 Windows amd64 ZIP 安装包。");
    }

    private static FrpRelease CreateRelease(Uri source, string version, string? sha256 = null)
    {
        var fileName = $"frp_{version}_windows_amd64.zip";
        var oldName = source.AbsolutePath.Substring(source.AbsolutePath.LastIndexOf('/') + 1);
        // Migrate versioned URLs retained by previous MCPanel configuration merges.
        var packageUrl = source.AbsolutePath.EndsWith("/", StringComparison.Ordinal) || PackageNamePattern.IsMatch(oldName)
            ? new Uri(source, fileName).AbsoluteUri
            : source.AbsoluteUri;
        return new(version, packageUrl,
            $"https://github.com/fatedier/frp/releases/download/v{version}/{fileName}", sha256);
    }

    private static async Task<string> ReadMetadataAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static bool IsMetadataFailure(Exception ex) =>
        ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidDataException ||
        ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException;
}
