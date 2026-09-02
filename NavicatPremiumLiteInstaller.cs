using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCPanel;

internal sealed class NavicatPremiumLiteInstaller
{
    internal const string LandingPageUrl = "https://www.navicat.com.cn/download/navicat-premium-lite";
    internal const string ResolverUrl = "https://www.navicat.com.cn/includes/Navicat/direct_download.php";
    internal const string DefaultProductFileName = "navicat17_premium_lite_cs_x64.exe";

    private const long MinimumInstallerBytes = 10L * 1024 * 1024;
    private static readonly Regex ProductFileNamePattern = new(
        @"\bnavicat\d+_premium_lite_cs_x64\.exe\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task InstallAsync(
        string toolsRoot,
        string workDirectory,
        Action<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

        var installerDirectory = Path.Combine(toolsRoot, "Installers");
        Directory.CreateDirectory(installerDirectory);
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(ComponentPaths.NavicatLiteRoot);

        using var client = CreateHttpClient();
        progress?.Invoke(new InstallProgress(0, "正在读取 Navicat Premium Lite 官方下载页..."));
        var productFileName = await ResolveProductFileNameAsync(client, cancellationToken);
        var installerPath = Path.Combine(installerDirectory, productFileName);
        var partialPath = installerPath + ".download";

        if (!IsTrustedInstaller(installerPath))
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            if (IsTrustedInstaller(partialPath))
            {
                FileCompat.Move(partialPath, installerPath, overwrite: true);
            }
            else
            {
                var downloadUri = await ResolveDownloadUriAsync(client, productFileName, cancellationToken);
                await DownloadInstallerAsync(
                    client,
                    downloadUri,
                    productFileName,
                    partialPath,
                    progress,
                    cancellationToken);

                try
                {
                    ValidateInstaller(partialPath);
                }
                catch
                {
                    if (File.Exists(partialPath))
                    {
                        File.Delete(partialPath);
                    }

                    throw;
                }

                FileCompat.Move(partialPath, installerPath, overwrite: true);
            }
        }
        else
        {
            progress?.Invoke(new InstallProgress(70, "已找到经过签名校验的 Navicat Premium Lite 缓存安装包。"));
        }

        ValidateInstaller(installerPath);
        var installLogPath = Path.Combine(workDirectory, "navicat-premium-lite-install.log");
        progress?.Invoke(new InstallProgress(
            72,
            $"正在安装 Navicat Premium Lite 到 {ComponentPaths.NavicatLiteRoot}..."));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = BuildInstallerArguments(ComponentPaths.NavicatLiteRoot, installLogPath),
            WorkingDirectory = installerDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 Navicat Premium Lite 安装器。");
        }

        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException(
                $"Navicat Premium Lite 安装失败，退出码：{process.ExitCode}。请查看日志：{installLogPath}");
        }

        var executablePath = FindInstalledExecutable(ComponentPaths.NavicatLiteRoot);
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"安装程序已结束，但未在目标文件夹检测到 navicat.exe。请查看日志：{installLogPath}");
        }

        progress?.Invoke(new InstallProgress(100, $"Navicat Premium Lite 已安装到 {ComponentPaths.NavicatLiteRoot}。"));
    }

    internal static string ExtractProductFileName(string html)
    {
        if (!string.IsNullOrWhiteSpace(html))
        {
            var match = ProductFileNamePattern.Match(html);
            if (match.Success && IsExpectedProductFileName(match.Value))
            {
                return match.Value;
            }
        }

        return DefaultProductFileName;
    }

    internal static Uri ParseDownloadUri(string json, string expectedProductFileName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("download_link", out var linkElement) ||
            linkElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Navicat 官网没有返回有效的下载地址。");
        }

        var value = linkElement.GetString()?.Trim() ?? string.Empty;
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }
        else if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsAllowedDownloadUri(uri, expectedProductFileName))
        {
            throw new InvalidDataException("Navicat 官网返回了不受信任的下载地址，已停止下载。");
        }

        return uri;
    }

    internal static bool IsAllowedDownloadUri(Uri uri, string expectedProductFileName)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsExpectedProductFileName(expectedProductFileName))
        {
            return false;
        }

        var allowedHost = string.Equals(uri.Host, "dn.navicat.com.cn", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(
                              uri.Host,
                              "navicat-installers.oss-cn-shanghai.aliyuncs.com",
                              StringComparison.OrdinalIgnoreCase);
        if (!allowedHost)
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)),
            expectedProductFileName,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildInstallerArguments(string installRoot, string logPath)
    {
        if (!Path.IsPathRooted(installRoot) || !Path.IsPathRooted(logPath))
        {
            throw new ArgumentException("Navicat 安装目录和日志路径必须是绝对路径。");
        }

        return string.Join(
            " ",
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/SP-",
            $"/DIR={Compat.QuoteCommandLineArgument(Path.GetFullPath(installRoot))}",
            $"/LOG={Compat.QuoteCommandLineArgument(Path.GetFullPath(logPath))}");
    }

    internal static string? FindInstalledExecutable(string root)
    {
        var direct = Path.Combine(root, "navicat.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, "navicat.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPanel/1.1 NavicatPremiumLiteInstaller");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return client;
    }

    private static async Task<string> ResolveProductFileNameAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            LandingPageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractProductFileName(html);
    }

    private static async Task<Uri> ResolveDownloadUriAsync(
        HttpClient client,
        string productFileName,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["product"] = productFileName,
            ["location"] = "1",
            ["support"] = string.Empty,
            ["linux_dist"] = string.Empty
        });
        using var response = await client.PostAsync(ResolverUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseDownloadUri(json, productFileName);
    }

    private static async Task DownloadInstallerAsync(
        HttpClient client,
        Uri downloadUri,
        string productFileName,
        string partialPath,
        Action<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                progress?.Invoke(new InstallProgress(
                    0,
                    $"发现 {PanelSettingsService.FormatStorageSize(existingBytes)} 未完成下载，正在从断点继续..."));
            }
            else
            {
                progress?.Invoke(new InstallProgress(0, "正在连接 Navicat 官方下载服务器..."));
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
            {
                File.Delete(partialPath);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? downloadUri;
            if (!IsAllowedDownloadUri(finalUri, productFileName))
            {
                throw new InvalidDataException("Navicat 下载被重定向到不受信任的服务器，已停止下载。");
            }

            var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
            {
                existingBytes = 0;
            }

            var contentLength = response.Content.Headers.ContentLength;
            var totalBytes = response.Content.Headers.ContentRange?.Length ??
                             (contentLength.HasValue ? existingBytes + contentLength.Value : (long?)null);
            var receivedBytes = existingBytes;
            var receivedThisSession = 0L;
            var startedAt = DateTime.UtcNow;
            var lastProgressAt = DateTime.MinValue;

            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var output = new FileStream(
                       partialPath,
                       append ? FileMode.Append : FileMode.Create,
                       FileAccess.Write,
                       FileShare.Read,
                       1024 * 128,
                       useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer, 0, read, cancellationToken);
                    receivedBytes += read;
                    receivedThisSession += read;
                    var now = DateTime.UtcNow;
                    if (now - lastProgressAt >= TimeSpan.FromMilliseconds(250) ||
                        (totalBytes is > 0 && receivedBytes == totalBytes.Value))
                    {
                        var elapsedSeconds = Math.Max((now - startedAt).TotalSeconds, 0.001);
                        var speed = receivedThisSession / elapsedSeconds;
                        var percent = totalBytes is > 0 ? receivedBytes * 70d / totalBytes.Value : 0d;
                        var downloadedText = totalBytes is > 0
                            ? $"{PanelSettingsService.FormatStorageSize(receivedBytes)} / {PanelSettingsService.FormatStorageSize(totalBytes.Value)}"
                            : PanelSettingsService.FormatStorageSize(receivedBytes);
                        progress?.Invoke(new InstallProgress(
                            Compat.Clamp(percent, 0, 70),
                            $"正在下载 Navicat Premium Lite：{downloadedText}，速度 {FormatTransferRate(speed)}"));
                        lastProgressAt = now;
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            if (receivedBytes <= 0)
            {
                throw new InvalidDataException("Navicat Premium Lite 下载文件为空。");
            }

            if (totalBytes is > 0 && receivedBytes != totalBytes.Value)
            {
                throw new InvalidDataException(
                    $"Navicat Premium Lite 下载不完整：应为 {totalBytes.Value} 字节，实际 {receivedBytes} 字节。下次会从断点继续。");
            }

            progress?.Invoke(new InstallProgress(70, "Navicat Premium Lite 下载完成，正在校验官方数字签名..."));
            return;
        }

        throw new InvalidDataException("Navicat Premium Lite 断点文件与服务器状态不一致，请重试。");
    }

    private static string FormatTransferRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
    }

    private static bool IsExpectedProductFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        ProductFileNamePattern.Match(value) is { Success: true, Index: 0 } match &&
        match.Length == value.Length &&
        string.Equals(Path.GetFileName(value), value, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedInstaller(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MinimumInstallerBytes)
        {
            return false;
        }

        try
        {
            ValidateInstaller(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateInstaller(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MinimumInstallerBytes)
        {
            throw new InvalidDataException("Navicat Premium Lite 安装包不存在或文件大小异常。");
        }

        var trustResult = AuthenticodeTrust.Verify(path);
        if (trustResult != 0)
        {
            throw new InvalidDataException($"Navicat Premium Lite 安装包数字签名无效（0x{trustResult:X8}）。");
        }

        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (certificate.Subject.IndexOf("PremiumSoft CyberTech", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException("Navicat Premium Lite 安装包签名者不是 PremiumSoft CyberTech。");
        }

        var version = FileVersionInfo.GetVersionInfo(path);
        if (string.IsNullOrWhiteSpace(version.ProductName) ||
            version.ProductName.IndexOf("Navicat Premium Lite", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException("下载文件不是 Navicat Premium Lite 官方安装包。");
        }
    }

    private static class AuthenticodeTrust
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static uint Verify(string fileName)
        {
            var fileInfo = new WinTrustFileInfo(fileName);
            var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(fileInfo));
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData(fileInfoPointer);
            try
            {
                return WinVerifyTrust(new IntPtr(-1), WinTrustActionGenericVerifyV2, ref trustData);
            }
            finally
            {
                trustData.StateAction = WtdStateActionClose;
                WinVerifyTrust(new IntPtr(-1), WinTrustActionGenericVerifyV2, ref trustData);
                Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            ref WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            internal WinTrustFileInfo(string fileName)
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
                FilePath = fileName;
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }

            internal uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] internal string FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal WinTrustData(IntPtr fileInfoPointer)
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>();
                PolicyCallbackData = IntPtr.Zero;
                SipClientData = IntPtr.Zero;
                UiChoice = WtdUiNone;
                RevocationChecks = WtdRevokeNone;
                UnionChoice = WtdChoiceFile;
                FileInfoPointer = fileInfoPointer;
                StateAction = WtdStateActionVerify;
                StateData = IntPtr.Zero;
                UrlReference = IntPtr.Zero;
                ProviderFlags = 0;
                UiContext = 0;
            }

            internal uint Size;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr FileInfoPointer;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
        }
    }
}
