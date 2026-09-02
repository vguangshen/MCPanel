using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MCPanel;

public sealed class CustomWebsiteDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string PhysicalPath { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 80;
    public List<string> Domains { get; set; } = [];
    public string ManagedRuntimeVersion { get; set; } = "v4.0";
    public bool Enable32Bit { get; set; }
    public bool SslEnabled { get; set; }
    public int HttpsPort { get; set; } = 443;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public bool RedirectHttpToHttps { get; set; }
    public int MaxBandwidthKbps { get; set; }
    public Dictionary<string, string> MimeMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string ApplicationPoolName => $"MCPanel.Site.{SafeToken(Id)}";

    private static string SafeToken(string value)
    {
        var token = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray()).Trim('_');
        return token.Length == 0 ? "site" : token;
    }
}

public sealed class CustomWebsiteService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim OperationLock = new(1, 1);
    private static string StateFile => Path.Combine(AppContext.BaseDirectory, "StoreData", "RuntimeState", "custom-websites.json");
    private static string WorkDirectory => Path.Combine(AppContext.BaseDirectory, "StoreData", "Work");

    public IReadOnlyList<CustomWebsiteDefinition> LoadAll()
    {
        return ReadAll(throwOnError: false);
    }

    private static IReadOnlyList<CustomWebsiteDefinition> ReadAll(bool throwOnError)
    {
        try
        {
            if (!File.Exists(StateFile))
            {
                return [];
            }

            return (JsonSerializer.Deserialize<List<CustomWebsiteDefinition>>(File.ReadAllText(StateFile, Encoding.UTF8)) ?? [])
                .Where(site => !string.IsNullOrWhiteSpace(site.Id) && !string.IsNullOrWhiteSpace(site.Name))
                .OrderBy(site => site.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception error)
        {
            RollingLogWriter.Append(
                Path.Combine(WorkDirectory, "custom-website-state.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 读取网站状态失败：{error}{Environment.NewLine}");
            if (throwOnError)
            {
                throw new InvalidDataException(
                    $"网站状态文件无法读取，为避免覆盖现有网站配置，当前操作已取消：{StateFile}",
                    error);
            }

            return [];
        }
    }

    public async Task<string> SaveAsync(
        CustomWebsiteDefinition definition,
        string? pfxPath,
        string? pfxPassword,
        CancellationToken cancellationToken = default)
    {
        Validate(definition, pfxPath);
        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
            if (!File.Exists(appcmd))
            {
                throw new InvalidOperationException("请先进入左侧“环境”页面安装 Web Server / IIS，再创建网站。");
            }

            if (definition.SslEnabled && definition.RedirectHttpToHttps &&
                !File.Exists(Path.Combine(Path.GetDirectoryName(appcmd)!, "rewrite.dll")))
            {
                throw new InvalidOperationException("当前 IIS 缺少 URL Rewrite。请先进入“环境”页面重新安装或修复 Web Server，再启用 HTTPS 跳转。");
            }

            Directory.CreateDirectory(definition.PhysicalPath);
            Directory.CreateDirectory(WorkDirectory);
            var knownSites = ReadAll(throwOnError: true);
            var existing = knownSites.FirstOrDefault(site => site.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (knownSites.Any(site => !site.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase) &&
                                       site.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"面板中已经存在名为“{definition.Name}”的网站。");
            }
            var webConfigTransaction = UpdateManagedWebConfig(definition, existing);
            var operationId = Guid.NewGuid().ToString("N");
            var scriptFile = Path.Combine(WorkDirectory, $"configure-site-{operationId}.ps1");
            var resultFile = Path.Combine(WorkDirectory, $"configure-site-{operationId}.result");
            var siteMutationStarted = false;
            try
            {
                var script = BuildConfigureScript(definition, pfxPath, pfxPassword, resultFile, existing is not null);
                await FileCompat.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(true), cancellationToken);
                await RunElevatedScriptAsync(
                    scriptFile,
                    cancellationToken,
                    () => siteMutationStarted = true);

                if (definition.SslEnabled)
                {
                    var thumbprint = File.Exists(resultFile) ? File.ReadAllText(resultFile, Encoding.UTF8).Trim() : string.Empty;
                    if (thumbprint.Length == 0)
                    {
                        throw new InvalidOperationException("IIS 已执行配置，但未返回 SSL 证书指纹。");
                    }

                    definition.CertificateThumbprint = thumbprint;
                }
                else
                {
                    definition.CertificateThumbprint = string.Empty;
                }

                definition.UpdatedAt = DateTimeOffset.Now;
                var all = ReadAll(throwOnError: true)
                    .Where(site => !site.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                all.Add(definition);
                SaveAll(all);
                webConfigTransaction.Complete();
                return $"IIS 网站“{definition.Name}”已保存并启动，访问地址：{BuildUrl(definition)}";
            }
            catch (Exception error)
            {
                webConfigTransaction.Rollback();
                if (siteMutationStarted && existing is not null)
                {
                    try
                    {
                        // Recovery is deliberately not cancelled with the
                        // original operation: it exists to keep an existing
                        // site usable after a cancelled/failed replacement.
                        await RestoreExistingSiteAsync(existing, CancellationToken.None);
                    }
                    catch (Exception restoreError)
                    {
                        throw new InvalidOperationException(
                            $"网站修改失败，且原 IIS 网站恢复失败：{restoreError.Message}。原始错误：{error.Message}",
                            restoreError);
                    }
                }

                throw;
            }
            finally
            {
                DeleteFile(scriptFile);
                DeleteFile(resultFile);
            }
        }
        finally
        {
            OperationLock.Release();
        }
    }

    public async Task<string> DeleteAsync(CustomWebsiteDefinition definition, CancellationToken cancellationToken = default)
    {
        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(WorkDirectory);
            var scriptFile = Path.Combine(WorkDirectory, $"remove-site-{Guid.NewGuid():N}.ps1");
            try
            {
                await FileCompat.WriteAllTextAsync(scriptFile, BuildDeleteScript(definition), new UTF8Encoding(true), cancellationToken);
                await RunElevatedScriptAsync(scriptFile, cancellationToken);
            }
            finally
            {
                DeleteFile(scriptFile);
            }

            RemoveManagedWebConfig(definition);
            SaveAll(ReadAll(throwOnError: true).Where(site => !site.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)));
            return $"已从 IIS 移除网站“{definition.Name}”。网站根目录和业务文件已保留。";
        }
        finally
        {
            OperationLock.Release();
        }
    }

    internal static string BuildUrl(CustomWebsiteDefinition definition)
    {
        var scheme = definition.SslEnabled ? "https" : "http";
        var port = definition.SslEnabled ? definition.HttpsPort : definition.HttpPort;
        var host = definition.Domains.FirstOrDefault(domain => !domain.StartsWith("*.", StringComparison.Ordinal)) ?? "localhost";
        var portText = (scheme == "https" && port == 443) || (scheme == "http" && port == 80)
            ? string.Empty
            : $":{port}";
        return $"{scheme}://{host}{portText}/";
    }

    internal static void Validate(CustomWebsiteDefinition definition, string? pfxPath)
    {
        definition.Id = string.IsNullOrWhiteSpace(definition.Id) ? Guid.NewGuid().ToString("N") : definition.Id.Trim();
        definition.Name = (definition.Name ?? string.Empty).Trim();
        if (definition.Name.Length is < 1 or > 80 || definition.Name.IndexOfAny(['/', '\\', ':', '*', '?', '\"', '<', '>', '|']) >= 0 ||
            definition.Name.Equals("MCPanel", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("网站名称不能为空、不能使用 MCPanel，且不能包含 Windows 保留字符。");
        }

        if (string.IsNullOrWhiteSpace(definition.PhysicalPath) || !Path.IsPathRooted(definition.PhysicalPath))
        {
            throw new InvalidOperationException("网站根目录必须是完整的绝对路径。");
        }

        definition.PhysicalPath = Path.GetFullPath(definition.PhysicalPath.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(definition.PhysicalPath)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(definition.PhysicalPath, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能直接把磁盘根目录作为网站目录。");
        }

        if (definition.HttpPort is <= 0 or > 65535 || definition.HttpsPort is <= 0 or > 65535 ||
            (definition.SslEnabled && definition.HttpPort == definition.HttpsPort))
        {
            throw new InvalidOperationException("HTTP/HTTPS 端口必须在 1 到 65535 之间，且不能相同。");
        }

        definition.Domains = NormalizeDomains(definition.Domains);
        if (definition.ManagedRuntimeVersion is not ("" or "v2.0" or "v4.0"))
        {
            throw new InvalidOperationException("IIS 应用程序池运行时只能选择无托管代码、v2.0 或 v4.0。");
        }

        if (definition.MaxBandwidthKbps is < 0 or > 1048576)
        {
            throw new InvalidOperationException("带宽上限必须在 0 到 1048576 KB/s 之间。");
        }

        if (definition.SslEnabled && string.IsNullOrWhiteSpace(definition.CertificateThumbprint) &&
            (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath)))
        {
            throw new InvalidOperationException("首次启用 HTTPS 时必须选择有效的 PFX/P12 证书。");
        }

        definition.MimeMappings = definition.MimeMappings
            .ToDictionary(pair => NormalizeExtension(pair.Key), pair => NormalizeMimeType(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    internal static List<string> NormalizeDomains(IEnumerable<string> domains)
    {
        var tokens = domains
            .SelectMany(domain => (domain ?? string.Empty).Split([',', ';', '，', '；', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Select(domain => domain.Trim().TrimEnd('.'))
            .Where(domain => domain.Length > 0)
            .ToList();
        var result = new List<string>();
        var idn = new IdnMapping();
        foreach (var domain in tokens)
        {
            var wildcard = domain.StartsWith("*.", StringComparison.Ordinal);
            var host = wildcard ? domain.Substring(2) : domain;
            if (domain.Contains("://", StringComparison.Ordinal) || domain.Contains('/') || domain.Contains(':') ||
                host.Length == 0)
            {
                throw new InvalidOperationException($"域名格式无效：{domain}。只需填写域名，不要包含协议、端口或路径。");
            }

            try
            {
                var asciiHost = idn.GetAscii(host);
                if (Uri.CheckHostName(asciiHost) == UriHostNameType.Unknown) throw new InvalidOperationException();
                result.Add(wildcard ? $"*.{asciiHost}" : asciiHost);
            }
            catch
            {
                throw new InvalidOperationException($"域名格式无效：{domain}。只需填写域名，不要包含协议、端口或路径。");
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static Dictionary<string, string> ParseMimeMappings(string text)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"MIME 映射格式无效：{line}。请使用 .扩展名=mime/type。");
            }

            mappings[NormalizeExtension(line.Substring(0, separator))] = NormalizeMimeType(line.Substring(separator + 1));
        }

        return mappings;
    }

    internal static string FormatMimeMappings(IReadOnlyDictionary<string, string> mappings) =>
        string.Join(Environment.NewLine, mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    internal static string BuildConfigureScript(
        CustomWebsiteDefinition definition,
        string? pfxPath,
        string? pfxPassword,
        string resultFile,
        bool allowExistingSite = false)
    {
        var domains = definition.Domains.Count == 0 ? new[] { string.Empty } : definition.Domains.ToArray();
        var domainArray = string.Join(",", domains.Select(domain => $"'{EscapePowerShell(domain)}'"));
        var maxBandwidth = definition.MaxBandwidthKbps == 0
            ? "4294967295"
            : ((long)definition.MaxBandwidthKbps * 1024L).ToString(CultureInfo.InvariantCulture);
        var protectedPassword = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(pfxPassword ?? string.Empty),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser));
        return $$"""
            $ErrorActionPreference='Stop'
            Import-Module WebAdministration
            Add-Type -AssemblyName System.Security
            $siteName='{{EscapePowerShell(definition.Name)}}'
            $siteRoot='{{EscapePowerShell(definition.PhysicalPath)}}'
            $poolName='{{EscapePowerShell(definition.ApplicationPoolName)}}'
            $runtime='{{EscapePowerShell(definition.ManagedRuntimeVersion)}}'
            $domains=@({{domainArray}})
            $httpPort={{definition.HttpPort}}
            $httpsPort={{definition.HttpsPort}}
            $sslEnabled=${{definition.SslEnabled.ToString().ToLowerInvariant()}}
            $thumbprint='{{EscapePowerShell(definition.CertificateThumbprint)}}'
            $pfxPath='{{EscapePowerShell(pfxPath ?? string.Empty)}}'
            $protectedPassword='{{protectedPassword}}'
            $resultFile='{{EscapePowerShell(resultFile)}}'

            if (Test-Path "IIS:\Sites\$siteName") {
                if (-not ${{allowExistingSite.ToString().ToLowerInvariant()}}) { throw "IIS 中已存在同名网站：$siteName。请换一个名称。" }
                Remove-Website -Name $siteName
            }
            if (-not (Test-Path "IIS:\AppPools\$poolName")) { New-WebAppPool -Name $poolName | Out-Null }
            Set-ItemProperty "IIS:\AppPools\$poolName" -Name managedRuntimeVersion -Value $runtime
            Set-ItemProperty "IIS:\AppPools\$poolName" -Name enable32BitAppOnWin64 -Value ${{definition.Enable32Bit.ToString().ToLowerInvariant()}}

            $firstDomain=$domains[0]
            New-Website -Name $siteName -PhysicalPath $siteRoot -Port $httpPort -IPAddress '*' -HostHeader $firstDomain -ApplicationPool $poolName | Out-Null
            foreach ($domain in $domains | Select-Object -Skip 1) {
                New-WebBinding -Name $siteName -Protocol http -Port $httpPort -IPAddress '*' -HostHeader $domain | Out-Null
            }

            if ($sslEnabled) {
                if ($pfxPath) {
                    $passwordBytes=[Security.Cryptography.ProtectedData]::Unprotect(
                        [Convert]::FromBase64String($protectedPassword),
                        $null,
                        [Security.Cryptography.DataProtectionScope]::CurrentUser)
                    $pfxPassword=[Text.Encoding]::UTF8.GetString($passwordBytes)
                    $securePassword=ConvertTo-SecureString $pfxPassword -AsPlainText -Force
                    $certificate=Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation 'Cert:\LocalMachine\My' -Password $securePassword
                    if (-not $certificate) { throw 'PFX 证书导入失败。' }
                    $thumbprint=$certificate.Thumbprint
                }
                if (-not (Test-Path "Cert:\LocalMachine\My\$thumbprint")) { throw '未找到用于 HTTPS 的证书。' }
                foreach ($domain in $domains) {
                    $sslFlags=if ($domain) { 1 } else { 0 }
                    New-WebBinding -Name $siteName -Protocol https -Port $httpsPort -IPAddress '*' -HostHeader $domain -SslFlags $sslFlags | Out-Null
                    $binding=Get-WebBinding -Name $siteName -Protocol https | Where-Object { $_.bindingInformation -eq "*:${httpsPort}:$domain" } | Select-Object -First 1
                    if (-not $binding) { throw "未找到刚创建的 HTTPS 绑定：$domain" }
                    $binding.AddSslCertificate($thumbprint, 'my')
                }
            }

            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "/system.applicationHost/sites/site[@name='$siteName']/limits" -Name maxBandwidth -Value {{maxBandwidth}}
            Start-WebAppPool -Name $poolName
            Start-Website -Name $siteName
            [IO.File]::WriteAllText($resultFile, $thumbprint, [Text.UTF8Encoding]::new($false))
            exit 0
            """;
    }

    internal static string BuildDeleteScript(CustomWebsiteDefinition definition) => $$"""
        $ErrorActionPreference='Stop'
        Import-Module WebAdministration
        $siteName='{{EscapePowerShell(definition.Name)}}'
        $poolName='{{EscapePowerShell(definition.ApplicationPoolName)}}'
        if (Test-Path "IIS:\Sites\$siteName") { Remove-Website -Name $siteName }
        if (Test-Path "IIS:\AppPools\$poolName") { Remove-WebAppPool -Name $poolName }
        exit 0
        """;

    internal static XElement BuildHttpsRedirectRule(string httpsAuthority) => new("rule",
        new XAttribute("name", "MCPanel HTTPS Redirect"),
        new XAttribute("stopProcessing", "true"),
        new XElement("match", new XAttribute("url", "(.*)")),
        new XElement("conditions",
            new XElement("add", new XAttribute("input", "{HTTPS}"), new XAttribute("pattern", "off"), new XAttribute("ignoreCase", "true"))),
        new XElement("action", new XAttribute("type", "Redirect"),
            new XAttribute("url", $"https://{httpsAuthority}{{REQUEST_URI}}"),
            new XAttribute("redirectType", "Permanent"),
            new XAttribute("appendQueryString", "false")));

    private static WebConfigTransaction UpdateManagedWebConfig(
        CustomWebsiteDefinition definition,
        CustomWebsiteDefinition? existing)
    {
        var transaction = new WebConfigTransaction();
        try
        {
            if (existing is not null && !PathsEqual(existing.PhysicalPath, definition.PhysicalPath))
            {
                transaction.Capture(Path.Combine(existing.PhysicalPath, "web.config"));
                RemoveManagedWebConfig(existing);
            }

            var configFile = Path.Combine(definition.PhysicalPath, "web.config");
            var configExisted = File.Exists(configFile);
            transaction.Capture(configFile);
            var document = LoadOrCreateWebConfig(configFile);
            var root = document.Root!;
            var systemWebServer = root.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("system.webServer", StringComparison.OrdinalIgnoreCase));
            var rewrite = systemWebServer?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("rewrite", StringComparison.OrdinalIgnoreCase));
            var rules = rewrite?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("rules", StringComparison.OrdinalIgnoreCase));
            rules?.Elements("rule").Where(rule =>
                string.Equals(rule.Attribute("name")?.Value, "MCPanel HTTPS Redirect", StringComparison.OrdinalIgnoreCase)).Remove();
            if (definition.SslEnabled && definition.RedirectHttpToHttps)
            {
                systemWebServer ??= GetOrAdd(root, "system.webServer");
                rewrite ??= GetOrAdd(systemWebServer, "rewrite");
                rules ??= GetOrAdd(rewrite, "rules");
                var httpsAuthority = definition.HttpsPort == 443
                    ? "{HTTP_HOST}"
                    : $"{{HTTP_HOST}}:{definition.HttpsPort}";
                rules.Add(BuildHttpsRedirectRule(httpsAuthority));
            }

            var staticContent = systemWebServer?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("staticContent", StringComparison.OrdinalIgnoreCase));
            if (existing is not null && staticContent is not null)
            {
                RemoveMimeMappings(staticContent, existing.MimeMappings);
            }
            if (definition.MimeMappings.Count > 0)
            {
                systemWebServer ??= GetOrAdd(root, "system.webServer");
                staticContent ??= GetOrAdd(systemWebServer, "staticContent");
            }
            foreach (var mapping in definition.MimeMappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (staticContent is null) throw new InvalidOperationException("无法创建 IIS MIME 配置节。");
                var conflict = staticContent.Elements("mimeMap").FirstOrDefault(element =>
                    string.Equals(element.Attribute("fileExtension")?.Value, mapping.Key, StringComparison.OrdinalIgnoreCase));
                if (conflict is not null)
                {
                    throw new InvalidOperationException($"web.config 已存在扩展名 {mapping.Key} 的 MIME 映射，请先处理该冲突。");
                }

                staticContent.Add(new XElement("mimeMap", new XAttribute("fileExtension", mapping.Key), new XAttribute("mimeType", mapping.Value)));
            }

            CleanupEmptyManagedSections(systemWebServer);
            if (!configExisted && !root.HasElements)
            {
                return transaction;
            }

            SaveXml(configFile, document);
            return transaction;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void RemoveManagedWebConfig(CustomWebsiteDefinition definition)
    {
        var configFile = Path.Combine(definition.PhysicalPath, "web.config");
        if (!File.Exists(configFile)) return;
        try
        {
            var document = XDocument.Load(configFile, LoadOptions.PreserveWhitespace);
            document.Descendants("rule").Where(rule =>
                string.Equals(rule.Attribute("name")?.Value, "MCPanel HTTPS Redirect", StringComparison.OrdinalIgnoreCase)).Remove();
            var staticContent = document.Descendants("staticContent").FirstOrDefault();
            if (staticContent is not null) RemoveMimeMappings(staticContent, definition.MimeMappings);
            CleanupEmptyManagedSections(document.Descendants("system.webServer").FirstOrDefault());
            SaveXml(configFile, document);
        }
        catch
        {
            // The IIS site was removed successfully; do not delete or replace an unreadable user config.
        }
    }

    private static void RemoveMimeMappings(XElement staticContent, IReadOnlyDictionary<string, string> mappings)
    {
        staticContent.Elements("mimeMap")
            .Where(element =>
            {
                var extension = element.Attribute("fileExtension")?.Value ?? string.Empty;
                var mime = element.Attribute("mimeType")?.Value ?? string.Empty;
                return mappings.TryGetValue(extension, out var managedMime) &&
                       string.Equals(mime, managedMime, StringComparison.OrdinalIgnoreCase);
            })
            .Remove();
    }

    private static void CleanupEmptyManagedSections(XElement? systemWebServer)
    {
        if (systemWebServer is null) return;
        var rewrite = systemWebServer.Elements().FirstOrDefault(element => element.Name.LocalName == "rewrite");
        var rules = rewrite?.Elements().FirstOrDefault(element => element.Name.LocalName == "rules");
        if (rules is not null && !rules.HasElements && !rules.HasAttributes) rules.Remove();
        if (rewrite is not null && !rewrite.HasElements && !rewrite.HasAttributes) rewrite.Remove();
        var staticContent = systemWebServer.Elements().FirstOrDefault(element => element.Name.LocalName == "staticContent");
        if (staticContent is not null && !staticContent.HasElements && !staticContent.HasAttributes) staticContent.Remove();
        if (!systemWebServer.HasElements && !systemWebServer.HasAttributes) systemWebServer.Remove();
    }

    private static XDocument LoadOrCreateWebConfig(string file)
    {
        if (!File.Exists(file))
        {
            return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("configuration"));
        }

        var document = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName != "configuration")
        {
            throw new InvalidDataException($"网站 web.config 的根节点必须是 configuration：{file}");
        }

        return document;
    }

    private static XElement GetOrAdd(XElement parent, string name)
    {
        var element = parent.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (element is not null) return element;
        element = new XElement(name);
        parent.Add(element);
        return element;
    }

    private static void SaveXml(string file, XDocument document)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, NewLineChars = Environment.NewLine };
        using (var textWriter = new Utf8StringWriter(builder))
        using (var writer = XmlWriter.Create(textWriter, settings)) document.Save(writer);
        AtomicFile.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));
    }

    private static async Task RunElevatedScriptAsync(
        string scriptFile,
        CancellationToken cancellationToken,
        Action? onStarted = null)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null) throw new InvalidOperationException("无法启动 IIS 网站管理脚本。");
        onStarted?.Invoke();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }

            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"IIS 网站管理脚本执行失败，退出码：{process.ExitCode}。");
        }
    }

    private static async Task RestoreExistingSiteAsync(
        CustomWebsiteDefinition existing,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var scriptFile = Path.Combine(WorkDirectory, $"restore-site-{operationId}.ps1");
        var resultFile = Path.Combine(WorkDirectory, $"restore-site-{operationId}.result");
        try
        {
            var script = BuildConfigureScript(existing, null, null, resultFile, allowExistingSite: true);
            await FileCompat.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(true), cancellationToken);
            await RunElevatedScriptAsync(scriptFile, cancellationToken);
        }
        finally
        {
            DeleteFile(scriptFile);
            DeleteFile(resultFile);
        }
    }

    private static string NormalizeExtension(string value)
    {
        var extension = (value ?? string.Empty).Trim();
        if (!extension.StartsWith('.')) extension = "." + extension;
        if (extension.Length < 2 || extension.IndexOfAny(['/', '\\', '*', '?', ' ', '=', ';', '\r', '\n']) >= 0)
            throw new InvalidOperationException($"MIME 扩展名无效：{value}");
        return extension.ToLowerInvariant();
    }

    private static string NormalizeMimeType(string value)
    {
        var mime = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (mime.Length < 3 || !mime.Contains('/') || mime.IndexOfAny([' ', '=', ';', '\r', '\n']) >= 0)
            throw new InvalidOperationException($"MIME 类型无效：{value}");
        return mime;
    }

    private static void SaveAll(IEnumerable<CustomWebsiteDefinition> definitions)
    {
        var values = definitions.OrderBy(site => site.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        AtomicFile.WriteAllText(StateFile, JsonSerializer.Serialize(values, JsonOptions), new UTF8Encoding(false));
    }

    private static string EscapePowerShell(string value) => (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    private static bool PathsEqual(string left, string right) => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static void DeleteFile(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

    private sealed class WebConfigTransaction
    {
        private readonly Dictionary<string, string?> _snapshots = new(StringComparer.OrdinalIgnoreCase);
        private bool _completed;
        public void Capture(string file)
        {
            if (!_snapshots.ContainsKey(file)) _snapshots[file] = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        }
        public void Complete() => _completed = true;
        public void Rollback()
        {
            if (_completed) return;
            foreach (var snapshot in _snapshots)
            {
                try
                {
                    if (snapshot.Value is null) DeleteFile(snapshot.Key);
                    else AtomicFile.WriteAllText(snapshot.Key, snapshot.Value, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder builder) : base(builder) { }
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
