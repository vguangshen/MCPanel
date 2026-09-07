using System.Diagnostics;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace MCPanel;

public sealed class OnlineUpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
}

public sealed class PreparedApplicationUpdate
{
    public string Version { get; set; } = string.Empty;
    public string PayloadDirectory { get; set; } = string.Empty;
    public string PackageFile { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public bool DeletePackageFileAfterApply { get; set; }
}

internal sealed class ApplicationUpdateAuthorization
{
    public string HelperRoot { get; init; } = string.Empty;
    public string PlanFile { get; init; } = string.Empty;
    public string CancelFile { get; init; } = string.Empty;
    public string InstallDirectory { get; init; } = string.Empty;
    public int ParentProcessId { get; init; }
    public Process? HelperProcess { get; set; }
    public bool PlanSubmitted { get; set; }
}

internal sealed class GitHubReleaseUpdate
{
    public OnlineUpdateManifest Manifest { get; init; } = new();
    public GitHubReleaseUpdateSource Source { get; init; } = new(string.Empty, string.Empty);
    public long PackageAssetId { get; init; }
}

internal sealed class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

internal sealed class GitHubReleaseAsset
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
}

internal sealed class GitHubReleaseManifest
{
    public string Format { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
}

internal sealed class ApplicationUpdatePlan
{
    public string InstallDirectory { get; set; } = string.Empty;
    public string PayloadDirectory { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string MainExecutableName { get; set; } = "MCPanel.exe";
    public int ParentProcessId { get; set; }
    public string StagingRoot { get; set; } = string.Empty;
    public string PackageFile { get; set; } = string.Empty;
    public bool DeletePackageFileAfterApply { get; set; }
}

public sealed class ApplicationUpdateService
{
    private const int MaxManifestBytes = 1024 * 1024;
    internal const string GitHubReleaseManifestFormat = "mcpanel-github-release-v1";
    internal const string GitHubUpdateRepositoryConfigKey = "GitHubUpdateRepository";
    private const string GitHubApiVersion = "2022-11-28";

    private const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 200000;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly HttpClient GitHubClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly GitHubUpdateCredentialStore _gitHubUpdateCredentials = new();
    private int _updaterCleanupStarted;
    private static readonly IReadOnlyCollection<string> PreservedTopLevelNames =
        DeploymentLayoutManifest.PreservedTopLevelNames;
    public string UpdatesRoot => ComponentPaths.UpdatesRoot;
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    public string LoadManifestUrl()
    {
        try
        {
            return (ConfigurationManager.AppSettings["UpdateManifestUrl"] ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public string LoadGitHubUpdateRepository()
    {
        try
        {
            return (ConfigurationManager.AppSettings[GitHubUpdateRepositoryConfigKey] ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool HasStoredGitHubUpdateToken() => _gitHubUpdateCredentials.HasStoredCredential();

    public void SaveGitHubUpdateToken(string accessToken) => _gitHubUpdateCredentials.SaveAccessToken(accessToken);

    public void ClearGitHubUpdateToken() => _gitHubUpdateCredentials.ClearAccessToken();

    internal GitHubReleaseUpdateSource LoadGitHubReleaseUpdateSource()
    {
        var repository = NormalizeGitHubRepository(LoadGitHubUpdateRepository());
        if (repository.Length == 0)
        {
            throw new InvalidOperationException("尚未配置 GitHub 更新仓库。");
        }

        var accessToken = _gitHubUpdateCredentials.LoadAccessToken();
        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException("尚未配置 GitHub 私有更新访问令牌。");
        }

        return new GitHubReleaseUpdateSource(repository, accessToken);
    }

    public string ConsumeLastUpdateResult()
    {
        var resultFile = Path.Combine(UpdatesRoot, "last-update.json");
        if (!File.Exists(resultFile)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
            var root = document.RootElement;
            var success = root.TryGetProperty("Success", out var successValue) && successValue.GetBoolean();
            var message = root.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() : string.Empty;
            return success ? message ?? "软件更新已完成。" : $"上次软件更新失败：{message}";
        }
        catch
        {
            return "上次软件更新结果无法读取，请检查 StoreData\\Updates。";
        }
        finally
        {
            try { File.Delete(resultFile); } catch { }
        }
    }

    public void CleanupStaleUpdaterFiles()
    {
        if (Interlocked.Exchange(ref _updaterCleanupStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // The helper starts the new application only after replacing and
            // cleaning its staging files. Give the just-exited helper a short
            // grace period, then retry once for slow antivirus/file release.
            foreach (var delay in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) })
            {
                await Task.Delay(delay);
                CleanupUpdaterDirectories();
            }
        });
    }

    public async Task<OnlineUpdateManifest> CheckOnlineAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        var uri = ValidateHttpsUrl(manifestUrl, "更新清单地址");
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await ReadResponseTextAsync(response.Content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = JsonSerializer.Deserialize<OnlineUpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("在线更新清单内容无效。");
        ValidateManifest(manifest, uri);
        return manifest;
    }

    internal async Task<GitHubReleaseUpdate> CheckGitHubReleaseAsync(
        GitHubReleaseUpdateSource source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var repository = NormalizeGitHubRepository(source.Repository);
        var accessToken = GitHubUpdateCredentialStore.NormalizeAccessToken(source.AccessToken);
        if (repository.Length == 0 || accessToken.Length == 0)
        {
            throw new InvalidOperationException("GitHub 私有更新源未完成配置。");
        }

        GitHubReleaseResponse? release;
        using (var response = await SendGitHubApiRequestAsync(
                   CreateGitHubLatestReleaseUri(repository),
                   accessToken,
                   acceptBinary: false,
                   cancellationToken))
        {
            EnsureGitHubResponse(response, "最新正式发行版");
            var json = await ReadResponseTextAsync(response.Content, cancellationToken);
            release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json, JsonOptions);
        }

        if (release is null || release.Draft || release.Prerelease)
        {
            throw new InvalidDataException("GitHub 更新仓库未返回可用的正式发行版。");
        }

        var releaseAssets = release.Assets ?? [];
        var manifestAsset = releaseAssets.FirstOrDefault(asset =>
            asset.Id > 0 && string.Equals(asset.Name, "update-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestAsset is null)
        {
            throw new InvalidDataException("GitHub 正式发行版缺少 update-manifest.json。");
        }

        GitHubReleaseManifest? releaseManifest;
        using (var response = await DownloadGitHubAssetAsync(repository, accessToken, manifestAsset.Id, cancellationToken))
        {
            EnsureGitHubResponse(response, "更新清单");
            var json = await ReadResponseTextAsync(response.Content, cancellationToken);
            releaseManifest = JsonSerializer.Deserialize<GitHubReleaseManifest>(json, JsonOptions);
        }

        var manifest = ValidateGitHubReleaseManifest(releaseManifest);
        if (!string.IsNullOrWhiteSpace(release.TagName) &&
            !string.Equals(NormalizeVersionText(release.TagName), manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub Release 标签与更新清单版本号不一致。");
        }

        var packageAsset = releaseAssets.FirstOrDefault(asset =>
            asset.Id > 0 && string.Equals(asset.Name, manifest.PackageName, StringComparison.OrdinalIgnoreCase));
        if (packageAsset is null)
        {
            throw new InvalidDataException($"GitHub 正式发行版缺少更新包 {manifest.PackageName}。");
        }

        var assetDigest = NormalizeGitHubAssetDigest(packageAsset.Digest);
        if (assetDigest.Length > 0 &&
            (assetDigest.Length != 64 || !assetDigest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("GitHub Release 记录的更新包摘要与清单不一致。");
        }

        var onlineManifest = new OnlineUpdateManifest
        {
            Version = manifest.Version,
            PackageUrl = CreateGitHubReleaseAssetUri(repository, packageAsset.Id).AbsoluteUri,
            Sha256 = manifest.Sha256,
            ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? release.Body ?? string.Empty : manifest.ReleaseNotes,
            Mandatory = manifest.Mandatory
        };
        ValidateManifest(onlineManifest, null);

        return new GitHubReleaseUpdate
        {
            Manifest = onlineManifest,
            Source = new GitHubReleaseUpdateSource(repository, accessToken),
            PackageAssetId = packageAsset.Id
        };
    }

    public async Task<PreparedApplicationUpdate> PrepareOnlineAsync(
        OnlineUpdateManifest manifest,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest, null);
        var packageUri = ValidateHttpsUrl(manifest.PackageUrl, "更新包地址");
        return await PrepareRemotePackageAsync(
            manifest,
            token => Client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, token),
            response => response.EnsureSuccessStatusCode(),
            progress,
            status,
            cancellationToken);
    }

    internal Task<PreparedApplicationUpdate> PrepareGitHubReleaseAsync(
        GitHubReleaseUpdate update,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        ValidateManifest(update.Manifest, null);
        if (update.PackageAssetId <= 0)
        {
            throw new InvalidDataException("GitHub 更新包资产编号无效。");
        }

        var repository = NormalizeGitHubRepository(update.Source.Repository);
        var accessToken = GitHubUpdateCredentialStore.NormalizeAccessToken(update.Source.AccessToken);
        if (repository.Length == 0 || accessToken.Length == 0)
        {
            throw new InvalidOperationException("GitHub 私有更新源未完成配置。");
        }

        return PrepareRemotePackageAsync(
            update.Manifest,
            token => DownloadGitHubAssetAsync(repository, accessToken, update.PackageAssetId, token),
            response => EnsureGitHubResponse(response, "更新包"),
            progress,
            status,
            cancellationToken);
    }

    internal async Task<PreparedApplicationUpdate> PrepareRemotePackageAsync(
        OnlineUpdateManifest manifest,
        Func<CancellationToken, Task<HttpResponseMessage>> download,
        Action<HttpResponseMessage> ensureResponse,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (download is null) throw new ArgumentNullException(nameof(download));
        if (ensureResponse is null) throw new ArgumentNullException(nameof(ensureResponse));

        var downloadRoot = Path.Combine(UpdatesRoot, "Downloads");
        Directory.CreateDirectory(downloadRoot);
        var safeVersion = SafeName(manifest.Version);
        var packageFile = Path.Combine(downloadRoot, $"MCPanel-{safeVersion}.zip");
        status?.Report("正在准备下载更新包...");
        using var packageLock = await AcquirePackageFileLockAsync(packageFile, cancellationToken);
        var temporary = packageFile + ".part";
        var expectedHash = NormalizeSha256(manifest.Sha256);
        var reusedCachedPackage = false;
        if (File.Exists(packageFile))
        {
            status?.Report("正在检查已下载的更新包...");
            string cachedHash;
            try
            {
                cachedHash = await Task.Run(() => ComputeSha256(packageFile), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("已下载的更新包正在被其他进程使用，无法继续更新。请关闭其他 MCPanel 更新任务后重试。", ex);
            }

            if (cachedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                reusedCachedPackage = true;
                status?.Report("已复用已下载的更新包。");
            }
            else
            {
                try
                {
                    File.Delete(packageFile);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException("旧的更新包无法替换，可能正被其他进程使用。请关闭其他 MCPanel 更新任务后重试。", ex);
                }
            }
        }

        if (!reusedCachedPackage)
        {
            status?.Report("正在下载更新包...");
            try
            {
                using var response = await download(cancellationToken);
                ensureResponse(response);
                await DownloadService.SaveResponseAsync(
                    response,
                    packageFile,
                    snapshot =>
                    {
                        if (snapshot.TotalBytes is > 0)
                        {
                            progress?.Report(Math.Min(
                                75,
                                snapshot.BytesReceived * 75d / snapshot.TotalBytes.Value));
                        }
                    },
                    cancellationToken: cancellationToken,
                    temporarySuffix: ".part",
                    bufferSize: 1024 * 1024);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // DownloadService already performs best-effort partial-file cleanup.
                }
            }

            status?.Report("正在校验 SHA-256...");
            var actualHash = await Task.Run(() => ComputeSha256(packageFile), cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packageFile);
                throw new InvalidDataException("更新包 SHA-256 校验失败，文件可能不完整或已被替换。");
            }
        }

        progress?.Report(80);
        return await PreparePackageAsync(
            packageFile,
            manifest.Version,
            manifest.ReleaseNotes,
            progress,
            status,
            cancellationToken,
            deletePackageFileAfterApply: true);
    }

    private static async Task<FileStream> AcquirePackageFileLockAsync(
        string packageFile,
        CancellationToken cancellationToken)
    {
        var lockFile = packageFile + ".lock";
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A kernel-backed file lock also coordinates separate MCPanel
                // processes. DeleteOnClose releases stale locks after a crash.
                return new FileStream(
                    lockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException error)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new IOException(
                        "更新包正在被其他更新任务使用，等待超时。请关闭其他 MCPanel 更新任务后重试。",
                        error);
                }

                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public async Task<PreparedApplicationUpdate> PrepareLocalAsync(
        string packageFile,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageFile))
            throw new FileNotFoundException("请选择有效的 ZIP 更新包。", packageFile);

        var normalizedPackageFile = Path.GetFullPath(packageFile);
        if (!File.Exists(normalizedPackageFile) ||
            !normalizedPackageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("请选择有效的 ZIP 更新包。", normalizedPackageFile);
        }

        var sidecar = normalizedPackageFile + ".sha256";
        if (File.Exists(sidecar))
        {
            status?.Report("正在校验本地更新包...");
            var fields = File.ReadAllText(sidecar)
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var expected = fields.Length > 0 ? NormalizeSha256(fields[0]) : string.Empty;
            if (expected.Length != 64)
                throw new InvalidDataException("本地更新包的 .sha256 校验文件格式无效。\n请重新生成更新包及其校验文件。");

            var actual = await Task.Run(() => ComputeSha256(normalizedPackageFile), cancellationToken);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地更新包与 .sha256 校验文件不匹配。");
        }
        progress?.Report(10);
        return await PreparePackageAsync(
            normalizedPackageFile,
            string.Empty,
            string.Empty,
            progress,
            status,
            cancellationToken,
            deletePackageFileAfterApply: false);
    }

    internal static bool ShouldRequestPreauthorizedUpdater(bool isAdministrator) => !isAdministrator;

    internal ApplicationUpdateAuthorization? RequestUpdateAuthorization()
    {
        if (!ShouldRequestPreauthorizedUpdater(ProcessRunner.IsAdministrator()))
        {
            return null;
        }

        var helperRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCPanel", "Updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperRoot);
        try
        {
            CopyUpdaterRuntime(AppContext.BaseDirectory, helperRoot);
            var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var planFile = Path.Combine(helperRoot, "update-plan.json");
            var cancelFile = Path.Combine(helperRoot, "cancel.flag");
            var parentProcessId = Process.GetCurrentProcess().Id;
            var helperExe = Path.Combine(helperRoot, "MCPanel.exe");
            Process helperProcess;
            try
            {
                helperProcess = ProcessRunner.Start(
                    helperExe,
                    string.Join(
                        " ",
                        "--wait-update-plan",
                        Compat.QuoteCommandLineArgument(planFile),
                        Compat.QuoteCommandLineArgument(cancelFile),
                        Compat.QuoteCommandLineArgument(installDirectory),
                        parentProcessId.ToString()),
                    helperRoot,
                    elevated: true);
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("已取消管理员授权，更新未开始。", error);
            }

            return new ApplicationUpdateAuthorization
            {
                HelperRoot = helperRoot,
                PlanFile = planFile,
                CancelFile = cancelFile,
                InstallDirectory = installDirectory,
                ParentProcessId = parentProcessId,
                HelperProcess = helperProcess
            };
        }
        catch
        {
            TryDeleteDirectory(helperRoot);
            throw;
        }
    }

    internal void CancelUpdateAuthorization(ApplicationUpdateAuthorization? authorization)
    {
        if (authorization is null || authorization.PlanSubmitted)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(authorization.CancelFile, "cancel");
        }
        catch
        {
        }

        try
        {
            if (authorization.HelperProcess is { HasExited: false })
            {
                authorization.HelperProcess.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            try { authorization.HelperProcess?.Dispose(); } catch { }
            authorization.HelperProcess = null;
        }

        TryDeleteDirectory(authorization.HelperRoot);
    }

    public void LaunchUpdater(PreparedApplicationUpdate update) =>
    LaunchUpdater(update, authorization: null);

internal void LaunchUpdater(
    PreparedApplicationUpdate update,
    ApplicationUpdateAuthorization? authorization)
{
        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var stagingRoot = ResolvePreparedStagingRoot(
            update.PayloadDirectory,
            Path.Combine(UpdatesRoot, "Staging"));
        var plan = new ApplicationUpdatePlan
        {
            InstallDirectory = installDirectory,
            PayloadDirectory = update.PayloadDirectory,
            TargetVersion = update.Version,
            ParentProcessId = Process.GetCurrentProcess().Id,
            StagingRoot = stagingRoot,
            PackageFile = update.PackageFile,
            DeletePackageFileAfterApply = update.DeletePackageFileAfterApply
        };

        if (authorization is not null)
        {
            if (authorization.PlanSubmitted ||
                !PathsEqual(authorization.InstallDirectory, installDirectory) ||
                authorization.ParentProcessId != plan.ParentProcessId ||
                !Directory.Exists(authorization.HelperRoot))
            {
                throw new InvalidOperationException("管理员更新授权会话已经失效，请重新点击检测并更新。 ");
            }

            try
            {
                if (authorization.HelperProcess is null || authorization.HelperProcess.HasExited)
                {
                    throw new InvalidOperationException("管理员更新授权进程已退出，请重新点击检测并更新。 ");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("无法确认管理员更新授权进程状态，请重新点击检测并更新。", error);
            }

            AtomicFile.WriteAllText(authorization.PlanFile, JsonSerializer.Serialize(plan, JsonOptions));
            authorization.PlanSubmitted = true;
            try { authorization.HelperProcess.Dispose(); } catch { }
            authorization.HelperProcess = null;
            return;
        }

        var helperRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCPanel", "Updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperRoot);
        CopyUpdaterRuntime(AppContext.BaseDirectory, helperRoot);
        var planFile = Path.Combine(helperRoot, "update-plan.json");
        AtomicFile.WriteAllText(planFile, JsonSerializer.Serialize(plan, JsonOptions));
        var helperExe = Path.Combine(helperRoot, "MCPanel.exe");
        using var updater = ProcessRunner.Start(
            helperExe,
            $"--apply-update {Compat.QuoteCommandLineArgument(planFile)}",
            helperRoot,
            elevated: !ProcessRunner.IsAdministrator() || RequiresElevation(installDirectory));
    }

    public void DiscardPreparedUpdate(PreparedApplicationUpdate? update)
    {
        if (update is null || string.IsNullOrWhiteSpace(update.PayloadDirectory))
        {
            return;
        }

        try
        {
            var stagingRoot = ResolvePreparedStagingRoot(
                update.PayloadDirectory,
                Path.Combine(UpdatesRoot, "Staging"));
            TryDeleteDirectory(stagingRoot);
        }
        catch
        {
            // Cleanup is best effort and must not hide the update result.
        }
    }

    public static Task<int> ApplyUpdatePlanAsync(string planFile) =>
        ApplyUpdatePlanAsync(planFile, expectedInstallDirectory: null, expectedParentProcessId: null);

    internal static async Task<int> WaitForAuthorizedUpdatePlanAsync(
        string planFile,
        string cancelFile,
        string expectedInstallDirectory,
        int expectedParentProcessId)
    {
        var validatedPlanFile = ValidatePlanFileLocation(planFile, requireExistingFile: false);
        var helperRoot = Path.GetDirectoryName(validatedPlanFile)!;
        var validatedCancelFile = Path.GetFullPath(cancelFile);
        if (!PathsEqual(Path.GetDirectoryName(validatedCancelFile) ?? string.Empty, helperRoot) ||
            !string.Equals(Path.GetFileName(validatedCancelFile), "cancel.flag", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var installDirectory = NormalizeDirectoryPath(expectedInstallDirectory, "安装目录");
        if (expectedParentProcessId <= 0 ||
            !Directory.Exists(installDirectory) ||
            !File.Exists(Path.Combine(installDirectory, "MCPanel.exe")))
        {
            return 2;
        }

        var deadline = DateTime.UtcNow.AddHours(6);
        while (!File.Exists(validatedPlanFile))
        {
            if (File.Exists(validatedCancelFile))
            {
                return 0;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return 3;
            }

            try
            {
                using var parent = Process.GetProcessById(expectedParentProcessId);
                if (parent.HasExited)
                {
                    return 0;
                }
            }
            catch (ArgumentException)
            {
                return 0;
            }

            await Task.Delay(250);
        }

        return await ApplyUpdatePlanAsync(
            validatedPlanFile,
            installDirectory,
            expectedParentProcessId);
    }

    private static async Task<int> ApplyUpdatePlanAsync(
        string planFile,
        string? expectedInstallDirectory,
        int? expectedParentProcessId)
    {
        ApplicationUpdatePlan? plan = null;
        var planValidated = false;
        try
        {
            var validatedPlanFile = ValidatePlanFileLocation(planFile);
            plan = JsonSerializer.Deserialize<ApplicationUpdatePlan>(File.ReadAllText(validatedPlanFile), JsonOptions)
                ?? throw new InvalidDataException("更新计划无效。");
            ValidateApplyPlan(plan);
            if (!string.IsNullOrWhiteSpace(expectedInstallDirectory) &&
                !PathsEqual(plan.InstallDirectory, expectedInstallDirectory!))
            {
                throw new InvalidDataException("管理员更新授权与当前 MCPanel 安装目录不一致。");
            }
            if (expectedParentProcessId.HasValue &&
                plan.ParentProcessId != expectedParentProcessId.Value)
            {
                throw new InvalidDataException("管理员更新授权与当前 MCPanel 进程不一致。");
            }
            planValidated = true;
            await WaitForParentAndStopInstalledProcessesAsync(plan);
            ApplyTransaction(plan);
            WriteResult(
                plan.InstallDirectory,
                true,
                $"已更新到版本 {plan.TargetVersion}，正在重启。",
                plan.TargetVersion);
            TryDeleteDirectory(plan.StagingRoot);
            if (plan.DeletePackageFileAfterApply)
            {
                try { if (File.Exists(plan.PackageFile)) File.Delete(plan.PackageFile); } catch { }
            }

            Exception? restartError = null;
            try
            {
                using var restarted = ProcessRunner.Start(
                    Path.Combine(plan.InstallDirectory, plan.MainExecutableName),
                    string.Empty,
                    plan.InstallDirectory);
            }
            catch (Exception error)
            {
                restartError = error;
                WriteResult(
                    plan.InstallDirectory,
                    true,
                    $"已更新到版本 {plan.TargetVersion}，但自动重启失败：{restartError.Message}。请手动启动 MCPanel.exe。",
                    plan.TargetVersion);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (planValidated && plan is not null)
            {
                WriteResult(plan.InstallDirectory, false, ex.ToString(), plan.TargetVersion);
            }
            return 2;
        }
    }

    private async Task<PreparedApplicationUpdate> PreparePackageAsync(
        string packageFile, string declaredVersion, string releaseNotes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken cancellationToken,
        bool deletePackageFileAfterApply)
    {
        status?.Report("正在安全解压并检查更新包...");
        var stagingRoot = Path.Combine(UpdatesRoot, "Staging", Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(stagingRoot, "Payload");
        Directory.CreateDirectory(extractRoot);
        try
        {
            await Task.Run(() => ExtractArchiveSafely(packageFile, extractRoot, progress, cancellationToken), cancellationToken);
            var payload = ResolvePayloadRoot(extractRoot);
            var executable = Path.Combine(payload, "MCPanel.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("更新包中未找到 MCPanel.exe。");
            if (!File.Exists(Path.Combine(payload, "MCPanel.exe.config")))
                throw new InvalidDataException("更新包缺少 MCPanel.exe.config，不是完整发布包。");
            var fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
            var executableVersionText = NormalizeVersionText(fileVersion);
            var version = string.IsNullOrWhiteSpace(declaredVersion) ? executableVersionText : NormalizeVersionText(declaredVersion);
            if (!Version.TryParse(version, out var parsedVersion)) throw new InvalidDataException("更新包版本号无效。");
            if (!Version.TryParse(executableVersionText, out var executableVersion))
                throw new InvalidDataException("更新包中的 MCPanel.exe FileVersion 无效，无法确认版本绑定。");
            if (!string.IsNullOrWhiteSpace(declaredVersion))
            {
                var normalizedDeclared = new Version(
                    parsedVersion.Major,
                    parsedVersion.Minor,
                    Math.Max(0, parsedVersion.Build),
                    Math.Max(0, parsedVersion.Revision));
                var normalizedExecutable = new Version(
                    executableVersion.Major,
                    executableVersion.Minor,
                    Math.Max(0, executableVersion.Build),
                    Math.Max(0, executableVersion.Revision));
                if (!normalizedDeclared.Equals(normalizedExecutable))
                {
                    throw new InvalidDataException(
                        $"更新清单声明版本 {version}，但包内 MCPanel.exe FileVersion 为 {executableVersionText}，已拒绝应用该更新包。");
                }
            }
            progress?.Report(100);
            status?.Report($"更新包已就绪：版本 {version}");
            return new PreparedApplicationUpdate
            {
                Version = version,
                PayloadDirectory = payload,
                PackageFile = packageFile,
                ReleaseNotes = releaseNotes,
                DeletePackageFileAfterApply = deletePackageFileAfterApply
            };
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    internal static void ExtractArchiveSafely(string packageFile, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packageFile);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("更新包为空或文件数量超出安全限制。");
        var totalBytes = archive.Entries.Sum(entry => entry.Length);
        if (totalBytes <= 0 || totalBytes > MaxExpandedBytes)
            throw new InvalidDataException("更新包解压大小超出安全限制。");
        EnsureDiskSpace(destination, totalBytes * 2);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long extracted = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新包包含不安全路径：{entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
            extracted += entry.Length;
            progress?.Report(80 + Math.Min(18, extracted * 18d / totalBytes));
        }
    }

    internal static void ApplyTransaction(ApplicationUpdatePlan plan)
    {
        ValidateTransactionPlan(plan);
        var installRoot = plan.InstallDirectory;
        var payloadRoot = plan.PayloadDirectory;
        var rollbackRoot = Path.Combine(ComponentPaths.GetUpdatesRoot(installRoot), "Rollback", "Current");
        var existingConfigFile = Path.Combine(installRoot, "MCPanel.exe.config");
        var preservedAppSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UpdateManifestUrl"] = ReadAppSetting(existingConfigFile, "UpdateManifestUrl"),
            [GitHubUpdateRepositoryConfigKey] = ReadAppSetting(existingConfigFile, GitHubUpdateRepositoryConfigKey)
        };
        foreach (var key in EnvironmentDownloadSettings.ConfigKeys)
        {
            preservedAppSettings[key] = ReadAppSetting(existingConfigFile, key);
        }
        foreach (var key in AiProviderSettings.ConfigKeys)
        {
            preservedAppSettings[key] = ReadAppSetting(existingConfigFile, key);
        }
        TryDeleteDirectory(rollbackRoot);
        Directory.CreateDirectory(rollbackRoot);
        var replacementStarted = false;
        try
        {
            CopyReplaceableEntries(installRoot, rollbackRoot);
            replacementStarted = true;
            DeleteReplaceableEntries(installRoot);
            CopyReplaceableEntries(payloadRoot, installRoot);
            foreach (var setting in preservedAppSettings)
            {
                if (!string.IsNullOrWhiteSpace(setting.Value))
                {
                    WriteAppSetting(Path.Combine(installRoot, "MCPanel.exe.config"), setting.Key, setting.Value);
                }
            }
            if (!File.Exists(Path.Combine(installRoot, plan.MainExecutableName)))
                throw new InvalidDataException("更新后主程序文件不存在。");

            // The rollback copy is needed only while the replacement is in
            // progress. Keeping it after a successful update would leave a
            // complete copy of the old MCPanel program under StoreData and
            // make a clean installation look like it still contains the old
            // version. A failed replacement still takes the catch path below
            // and retains the restored state for diagnosis/retry.
            TryDeleteDirectory(rollbackRoot);
        }
        catch
        {
            if (replacementStarted)
            {
                DeleteReplaceableEntries(installRoot);
                CopyReplaceableEntries(rollbackRoot, installRoot);
            }
            else
            {
                // A backup failure happens before the installed program is
                // touched. Never replace a complete installation with a
                // partial rollback snapshot.
                TryDeleteDirectory(rollbackRoot);
            }
            throw;
        }
    }

    internal static void ValidateTransactionPlan(ApplicationUpdatePlan plan)
    {
        if (plan is null)
        {
            throw new InvalidDataException("更新计划无效。");
        }

        if (!string.Equals(plan.MainExecutableName, "MCPanel.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新计划中的主程序名称无效。");
        }

        if (!Version.TryParse(NormalizeVersionText(plan.TargetVersion), out _))
        {
            throw new InvalidDataException("更新计划中的目标版本无效。");
        }

        var installRoot = NormalizeDirectoryPath(plan.InstallDirectory, "安装目录");
        var installDriveRoot = Path.GetPathRoot(installRoot);
        if (string.IsNullOrWhiteSpace(installDriveRoot) ||
            PathsEqual(installRoot, installDriveRoot))
        {
            throw new InvalidDataException("更新计划不能把磁盘根目录作为安装目录。");
        }

        if (!Directory.Exists(installRoot) ||
            !File.Exists(Path.Combine(installRoot, "MCPanel.exe")))
        {
            throw new InvalidDataException("更新计划中的 MCPanel 安装目录无效。");
        }

        var payloadRoot = NormalizeDirectoryPath(plan.PayloadDirectory, "更新暂存目录");
        var stagingRoot = NormalizeDirectoryPath(plan.StagingRoot, "更新暂存根目录");
        var stagingParent = NormalizeDirectoryPath(
            Path.Combine(ComponentPaths.GetUpdatesRoot(installRoot), "Staging"),
            "更新暂存父目录");
        var stagingParentInfo = Directory.GetParent(stagingRoot);
        if (stagingParentInfo is null ||
            !PathsEqual(stagingParentInfo.FullName, stagingParent) ||
            !Guid.TryParseExact(Path.GetFileName(stagingRoot), "N", out _))
        {
            throw new InvalidDataException("更新暂存根目录不属于当前 MCPanel 安装目录。");
        }

        if (!IsPathInside(stagingRoot, payloadRoot) ||
            PathsEqual(installRoot, payloadRoot) ||
            !Directory.Exists(payloadRoot) ||
            !File.Exists(Path.Combine(payloadRoot, "MCPanel.exe")) ||
            !File.Exists(Path.Combine(payloadRoot, "MCPanel.exe.config")))
        {
            throw new InvalidDataException("暂存更新文件已丢失或位置不安全。");
        }

        plan.InstallDirectory = installRoot;
        plan.PayloadDirectory = payloadRoot;
        plan.StagingRoot = stagingRoot;
        plan.TargetVersion = NormalizeVersionText(plan.TargetVersion);
        plan.MainExecutableName = "MCPanel.exe";
    }

    private static void ValidateApplyPlan(ApplicationUpdatePlan plan)
    {
        ValidateTransactionPlan(plan);
        var helperRoot = NormalizeDirectoryPath(AppContext.BaseDirectory, "更新器目录");
        if (PathsEqual(helperRoot, plan.InstallDirectory) || plan.ParentProcessId <= 0)
        {
            throw new InvalidDataException("独立更新器计划无效。");
        }

        if (!plan.DeletePackageFileAfterApply)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(plan.PackageFile))
        {
            throw new InvalidDataException("在线更新包路径无效。");
        }

        var packageFile = Path.GetFullPath(plan.PackageFile);
        var downloadRoot = Path.Combine(ComponentPaths.GetUpdatesRoot(plan.InstallDirectory), "Downloads");
        if (!IsPathInside(downloadRoot, packageFile) ||
            !packageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("在线更新包不属于当前 MCPanel 下载目录。");
        }

        plan.PackageFile = packageFile;
    }

    private static string ValidatePlanFileLocation(string planFile, bool requireExistingFile = true)
    {
        if (string.IsNullOrWhiteSpace(planFile))
        {
            throw new InvalidDataException("更新计划文件无效。");
        }

        var fullPlanFile = Path.GetFullPath(planFile);
        var helperRoot = NormalizeDirectoryPath(AppContext.BaseDirectory, "更新器目录");
        var planParent = Path.GetDirectoryName(fullPlanFile);
        var updaterRoot = NormalizeDirectoryPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCPanel",
                "Updater"),
            "更新器父目录");
        var helperParent = Directory.GetParent(helperRoot);
        if (!string.Equals(Path.GetFileName(fullPlanFile), "update-plan.json", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(planParent) ||
            !PathsEqual(planParent, helperRoot) ||
            helperParent is null ||
            !PathsEqual(helperParent.FullName, updaterRoot) ||
            !Guid.TryParseExact(Path.GetFileName(helperRoot), "N", out _) ||
            (requireExistingFile && !File.Exists(fullPlanFile)))
        {
            throw new InvalidDataException("更新计划文件不在受控的独立更新器目录中。");
        }

        return fullPlanFile;
    }

    private static async Task WaitForParentAndStopInstalledProcessesAsync(ApplicationUpdatePlan plan)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            for (var i = 0; i < 60 && !parent.HasExited; i++) await Task.Delay(500);
            if (!parent.HasExited)
            {
                ProcessLifecycle.TryKill(parent);
                if (!parent.HasExited)
                {
                    throw new InvalidOperationException("更新前无法关闭原 MCPanel 进程，请先手动退出 MCPanel 后重试。");
                }
            }
        }
        catch (ArgumentException)
        {
            // The launcher may already have exited before the helper observes it.
        }

        var installedExe = Path.GetFullPath(Path.Combine(plan.InstallDirectory, plan.MainExecutableName));
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(plan.MainExecutableName)))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Process.GetCurrentProcess().Id &&
                        process.MainModule is not null &&
                        Path.GetFullPath(process.MainModule.FileName).Equals(installedExe, StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessLifecycle.TryKill(process);
                        if (!process.HasExited)
                        {
                            throw new InvalidOperationException(
                                "更新前无法关闭已安装的 MCPanel 进程，请先手动退出 MCPanel 后重试。");
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        "更新前无法关闭已安装的 MCPanel 进程，请先手动退出 MCPanel 后重试。",
                        error);
                }
            }
        }
    }

    private static bool RequiresElevation(string installDirectory)
    {
        var probe = Path.Combine(installDirectory, ".mcpanel-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            try { File.Delete(probe); } catch { }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            try { File.Delete(probe); } catch { }
            return true;
        }
        catch
        {
            try { File.Delete(probe); } catch { }
            return false;
        }
    }

    private static void CopyUpdaterRuntime(string sourceRoot, string destinationRoot)
    {
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destinationRoot, Path.GetFileName(file)), true);
        var runtimes = Path.Combine(sourceRoot, "runtimes");
        if (Directory.Exists(runtimes)) CopyDirectory(runtimes, Path.Combine(destinationRoot, "runtimes"));
    }

    private static void CopyReplaceableEntries(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            var name = Path.GetFileName(entry);
            if (PreservedTopLevelNames.Contains(name)) continue;
            var destination = Path.Combine(destinationRoot, name);
            if (Directory.Exists(entry)) CopyDirectory(entry, destination);
            else File.Copy(entry, destination, true);
        }
    }

    private static void DeleteReplaceableEntries(string installRoot)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(installRoot))
        {
            if (PreservedTopLevelNames.Contains(Path.GetFileName(entry))) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string ResolvePreparedStagingRoot(string payloadDirectory, string stagingParent)
    {
        var normalizedParent = NormalizeDirectoryPath(stagingParent, "更新暂存父目录");
        var current = NormalizeDirectoryPath(payloadDirectory, "更新暂存目录");
        while (IsPathInside(normalizedParent, current))
        {
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            if (PathsEqual(parent.FullName, normalizedParent) &&
                Guid.TryParseExact(Path.GetFileName(current), "N", out _))
            {
                return current;
            }

            current = NormalizeDirectoryPath(parent.FullName, "更新暂存目录");
        }

        throw new InvalidDataException("更新暂存目录不属于当前 MCPanel 安装目录。");
    }

    private static string NormalizeDirectoryPath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{label}无效。");
        }

        var fullPath = Path.GetFullPath(value);
        var rootLength = (Path.GetPathRoot(fullPath) ?? string.Empty).Length;
        return fullPath.Length > rootLength
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                NormalizeDirectoryPath(first, "路径"),
                NormalizeDirectoryPath(second, "路径"),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathInside(string root, string path)
    {
        try
        {
            var normalizedRoot = NormalizeDirectoryPath(root, "根目录") + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupUpdaterDirectories()
    {
        try
        {
            var updaterRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCPanel",
                "Updater");
            if (!Directory.Exists(updaterRoot))
            {
                return;
            }

            var currentRoot = NormalizeDirectoryPath(AppContext.BaseDirectory, "当前程序目录");
            foreach (var directory in Directory.EnumerateDirectories(updaterRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    PathsEqual(directory, currentRoot))
                {
                    continue;
                }

                TryDeleteDirectory(directory);
            }
        }
        catch
        {
            // Stale helper cleanup is best effort and must never affect startup.
        }
    }

    private static string ResolvePayloadRoot(string extractRoot)
    {
        if (File.Exists(Path.Combine(extractRoot, "MCPanel.exe"))) return extractRoot;
        var directories = Directory.EnumerateDirectories(extractRoot).ToArray();
        var files = Directory.EnumerateFiles(extractRoot).ToArray();
        return files.Length == 0 && directories.Length == 1 && File.Exists(Path.Combine(directories[0], "MCPanel.exe"))
            ? directories[0]
            : extractRoot;
    }

    private static string ReadAppSetting(string configFile, string key)
    {
        try
        {
            if (!File.Exists(configFile)) return string.Empty;
            var document = XDocument.Load(configFile, LoadOptions.PreserveWhitespace);
            return document.Root?.Element("appSettings")?.Elements("add")
                .FirstOrDefault(element => string.Equals((string?)element.Attribute("key"), key, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteAppSetting(string configFile, string key, string value)
    {
        if (!File.Exists(configFile)) return;
        var document = XDocument.Load(configFile, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("更新后的应用配置文件无效。");
        var settings = root.Element("appSettings");
        if (settings is null)
        {
            settings = new XElement("appSettings");
            root.AddFirst(settings);
        }
        var item = settings.Elements("add")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("key"), key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new XElement("add", new XAttribute("key", key));
            settings.Add(item);
        }
        item.SetAttributeValue("value", value);
        document.Save(configFile, SaveOptions.DisableFormatting);
    }

    internal static string NormalizeGitHubRepository(string? value)
    {
        var repository = (value ?? string.Empty).Trim();
        if (repository.Length == 0)
        {
            return string.Empty;
        }

        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => !IsValidGitHubRepositoryPart(part)))
        {
            throw new InvalidDataException("GitHub 更新仓库必须使用 owner/repository 格式。");
        }

        return $"{parts[0]}/{parts[1]}";
    }

    private static bool IsValidGitHubRepositoryPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 100)
        {
            return false;
        }

        return value.All(ch =>
            (ch is >= 'a' and <= 'z') ||
            (ch is >= 'A' and <= 'Z') ||
            (ch is >= '0' and <= '9') ||
            ch is '-' or '_' or '.');
    }

    internal static GitHubReleaseManifest ValidateGitHubReleaseManifest(GitHubReleaseManifest? manifest)
    {
        if (manifest is null ||
            !string.Equals(manifest.Format, GitHubReleaseManifestFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 更新清单格式无效。");
        }

        var version = NormalizeVersionText(manifest.Version);
        if (!Version.TryParse(version, out _))
        {
            throw new InvalidDataException("GitHub 更新清单版本号无效。");
        }

        var packageName = (manifest.PackageName ?? string.Empty).Trim();
        if (packageName.Length == 0 ||
            packageName.Any(ch => ch is '/' or '\\' || Path.GetInvalidFileNameChars().Contains(ch)) ||
            !packageName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packageName, $"MCPanel-{version}.zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 更新清单中的更新包名称无效。");
        }

        var hash = NormalizeSha256(manifest.Sha256);
        if (hash.Length != 64)
        {
            throw new InvalidDataException("GitHub 更新清单必须提供完整的 SHA-256。");
        }

        return new GitHubReleaseManifest
        {
            Format = GitHubReleaseManifestFormat,
            Version = version,
            PackageName = packageName,
            Sha256 = hash,
            ReleaseNotes = manifest.ReleaseNotes ?? string.Empty,
            Mandatory = manifest.Mandatory
        };
    }

    private static Uri CreateGitHubLatestReleaseUri(string repository) =>
        CreateGitHubReleaseApiUri(repository, "releases/latest");

    private static Uri CreateGitHubReleaseAssetUri(string repository, long assetId)
    {
        if (assetId <= 0)
        {
            throw new InvalidDataException("GitHub Release 资产编号无效。");
        }

        return CreateGitHubReleaseApiUri(repository, $"releases/assets/{assetId}");
    }

    private static Uri CreateGitHubReleaseApiUri(string repository, string path)
    {
        var normalized = NormalizeGitHubRepository(repository);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("GitHub 更新仓库不能为空。");
        }

        var parts = normalized.Split('/');
        return new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/{path}",
            UriKind.Absolute);
    }

    private static async Task<HttpResponseMessage> SendGitHubApiRequestAsync(
        Uri uri,
        string accessToken,
        bool acceptBinary,
        CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(uri, accessToken, acceptBinary, includeAuthorization: true);
        return await GitHubClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<HttpResponseMessage> DownloadGitHubAssetAsync(
        string repository,
        string accessToken,
        long assetId,
        CancellationToken cancellationToken)
    {
        var current = CreateGitHubReleaseAssetUri(repository, assetId);
        for (var redirectCount = 0; redirectCount < 4; redirectCount++)
        {
            var includeAuthorization = redirectCount == 0;
            using var request = CreateGitHubRequest(current, accessToken, acceptBinary: true, includeAuthorization);
            var response = await GitHubClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException("GitHub 更新包重定向地址缺失。");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("GitHub 更新包重定向必须使用 HTTPS。");
            }
        }

        throw new InvalidDataException("GitHub 更新包重定向次数过多。");
    }

    private static HttpRequestMessage CreateGitHubRequest(
        Uri uri,
        string accessToken,
        bool acceptBinary,
        bool includeAuthorization)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub 更新请求必须使用 HTTPS。");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MCPanel", CurrentVersionText));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            acceptBinary ? "application/octet-stream" : "application/vnd.github+json"));

        if (includeAuthorization)
        {
            if (!string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Dispose();
                throw new InvalidDataException("GitHub 更新凭据只能发送给 GitHub API。");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        }

        return request;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private static void EnsureGitHubResponse(HttpResponseMessage response, string resource)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "无法访问 GitHub 私有更新源。请重新保存仅限该仓库、Contents: read 权限的访问令牌。");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException($"GitHub 更新仓库中未找到{resource}。");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new InvalidOperationException("GitHub 更新服务请求过多，请稍后重试。");
        }

        throw new HttpRequestException($"GitHub {resource}请求失败（HTTP {(int)response.StatusCode}）。");
    }

    private static void ValidateManifest(OnlineUpdateManifest manifest, Uri? manifestUri)
    {
        if (!Version.TryParse(NormalizeVersionText(manifest.Version), out _)) throw new InvalidDataException("更新清单版本号无效。");
        var packageUri = manifestUri is not null && Uri.TryCreate(manifestUri, manifest.PackageUrl, out var relative)
            ? relative
            : ValidateHttpsUrl(manifest.PackageUrl, "更新包地址");
        if (packageUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("在线更新包必须使用 HTTPS。");
        if (NormalizeSha256(manifest.Sha256).Length != 64) throw new InvalidDataException("更新清单必须提供完整的 SHA-256。");
        manifest.PackageUrl = packageUri.AbsoluteUri;
        manifest.Version = NormalizeVersionText(manifest.Version);
    }

    private static Uri ValidateHttpsUrl(string value, string label)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"{label}必须是有效的 HTTPS 地址。");
        return uri;
    }

    private static string ComputeSha256(string file)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static async Task<string> ReadResponseTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > MaxManifestBytes)
        {
            throw new InvalidDataException("在线更新清单过大，无法读取。");
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

            if (buffer.Length + read > MaxManifestBytes)
            {
                throw new InvalidDataException("在线更新清单过大，无法读取。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string NormalizeGitHubAssetDigest(string value)
    {
        var digest = (value ?? string.Empty).Trim();
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            digest = digest.Substring("sha256:".Length);
        }

        return NormalizeSha256(digest);
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("sha256:".Length);
        }

        return new string(normalized.Where(Uri.IsHexDigit).ToArray());
    }
    private static string NormalizeVersionText(string value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var parts = text.Split('.').Take(4).ToArray();
        return string.Join(".", parts);
    }
    private static string SafeName(string value) => string.Concat((value ?? "update").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(root))
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                throw new IOException($"更新所需磁盘空间不足，至少需要 {PanelSettingsService.FormatStorageSize(requiredBytes)} 可用空间。");
        }
    }

    private static void WriteResult(string installRoot, bool success, string message, string version)
    {
        try
        {
            var path = Path.Combine(ComponentPaths.GetUpdatesRoot(installRoot), "last-update.json");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new { Success = success, Message = message, Version = version, Time = DateTime.Now }, JsonOptions));
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

}
