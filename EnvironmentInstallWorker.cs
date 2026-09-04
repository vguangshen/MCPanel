using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace MCPanel;

internal static class EnvironmentInstallWorker
{
    private const string WorkerArgument = "--environment-install-worker";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static bool IsWorkerRequest(string[] args) =>
        Array.Exists(args, argument =>
            string.Equals(argument, WorkerArgument, StringComparison.OrdinalIgnoreCase));

    public static bool RequiresImmediateElevation(EnvironmentKind kind) =>
        kind is EnvironmentKind.Iis or EnvironmentKind.Nginx or EnvironmentKind.MySql or EnvironmentKind.SqlServer;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryGetRequest(args, out var kind, out var progressPath, out var selectedReleaseId))
        {
            return 2;
        }

        var reporter = new ProgressReporter(progressPath);
        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException("管理员安装进程未获得管理员权限。请重新点击安装并允许 UAC 授权。");
            }

            reporter.Write(
                "running",
                0,
                "管理员安装会话已启动，正在准备安装...",
                force: true,
                stage: InstallProgressStage.Preparing,
                stagePercent: 0);
            using var installer = new EnvironmentInstaller();
            var item = new EnvironmentItem(kind, kind.ToString(), string.Empty, string.Empty);
            if (kind == EnvironmentKind.MySql)
            {
                item.SelectedMySqlReleaseId = selectedReleaseId!;
            }
            else if (kind == EnvironmentKind.SqlServer)
            {
                item.SelectedSqlServerReleaseId = selectedReleaseId!;
            }

            await installer.InstallAsync(item, reporter.Report, CancellationToken.None).ConfigureAwait(true);
            reporter.Write(
                "completed",
                100,
                "安装流程完成。",
                force: true,
                stage: InstallProgressStage.Completed,
                stagePercent: 100);
            return 0;
        }
        catch (Exception ex)
        {
            var restartRequired = ex is InstallRestartRequiredException;
            var logPath = restartRequired
                ? null
                : EnvironmentOperationDiagnostics.RecordFailure(
                    kind.ToString(),
                    "安装",
                    ex,
                    Path.Combine(
                        ComponentPaths.WorkRoot,
                        $"environment-install-{kind.ToString().ToLowerInvariant()}.log"));
            var detail = restartRequired ? ex.Message : "安装失败：" + ex.Message;
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                detail += $"{Environment.NewLine}{Environment.NewLine}完整诊断日志：{logPath}";
            }
            reporter.Write(
                restartRequired ? "restart-required" : "failed",
                0,
                detail,
                force: true,
                logPath: logPath);
            return restartRequired ? 2 : 1;
        }
    }

    public static Process Start(EnvironmentKind kind, string progressPath, string? selectedReleaseId = null)
    {
        var executable = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法定位 MCPanel 主程序，不能启动管理员安装会话。");
        }
        var executablePath = executable!;

        var arguments = string.Join(" ",
            WorkerArgument,
            Compat.QuoteCommandLineArgument(kind.ToString()),
            Compat.QuoteCommandLineArgument(progressPath));
        if (kind is EnvironmentKind.MySql or EnvironmentKind.SqlServer)
        {
            var releaseId = kind == EnvironmentKind.MySql
                ? MySqlReleaseCatalog.Contains(selectedReleaseId) ? selectedReleaseId! : MySqlReleaseCatalog.Default.Id
                : SqlServerReleaseCatalog.Contains(selectedReleaseId) ? selectedReleaseId! : SqlServerReleaseCatalog.Recommended.Id;
            arguments += " " + Compat.QuoteCommandLineArgument(releaseId);
        }

        try
        {
            return ProcessRunner.Start(
                executablePath,
                arguments,
                Path.GetDirectoryName(executablePath) ?? ComponentPaths.ApplicationRoot,
                elevated: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员授权，安装未开始。", ex);
        }
    }

    public static string CreateProgressFile()
    {
        var directory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "environment-install-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(
            new EnvironmentInstallWorkerProgress(
                "waiting",
                0,
                "等待管理员安装会话启动。",
                Stage: InstallProgressStage.Preparing,
                StagePercent: 0),
            JsonOptions));
        return path;
    }

    public static bool TryReadProgress(string path, out EnvironmentInstallWorkerProgress progress)
    {
        try
        {
            if (!File.Exists(path))
            {
                progress = default!;
                return false;
            }

            var parsed = JsonSerializer.Deserialize<EnvironmentInstallWorkerProgress>(
                File.ReadAllText(path), JsonOptions);
            if (parsed is null)
            {
                progress = default!;
                return false;
            }

            progress = parsed;
            return true;
        }
        catch
        {
            progress = default!;
            return false;
        }
    }

    public static void DeleteProgressFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The status file is temporary state; a later startup can safely leave it alone.
        }
    }

    private static bool TryGetRequest(
        string[] args,
        out EnvironmentKind kind,
        out string progressPath,
        out string? selectedReleaseId)
    {
        kind = default;
        progressPath = string.Empty;
        selectedReleaseId = null;
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, WorkerArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length ||
            !Enum.TryParse(args[index + 1], ignoreCase: true, out kind) ||
            !RequiresImmediateElevation(kind))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(args[index + 2]))
        {
            return false;
        }

        if (kind is EnvironmentKind.MySql or EnvironmentKind.SqlServer)
        {
            var isValidRelease = kind == EnvironmentKind.MySql
                ? MySqlReleaseCatalog.Contains(index + 3 < args.Length ? args[index + 3] : null)
                : SqlServerReleaseCatalog.Contains(index + 3 < args.Length ? args[index + 3] : null);
            if (index + 3 >= args.Length || !isValidRelease)
            {
                return false;
            }

            selectedReleaseId = args[index + 3];
        }

        try
        {
            var progressRoot = Path.GetFullPath(ComponentPaths.WorkRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            progressPath = Path.GetFullPath(args[index + 2]);
            return progressPath.StartsWith(progressRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            progressPath = string.Empty;
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class ProgressReporter
    {
        private readonly string _path;
        private readonly object _sync = new();
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private string _lastMessage = string.Empty;
        private InstallProgressStage _stage = InstallProgressStage.Preparing;
        private double? _stagePercent;
        private string? _speedText;

        public ProgressReporter(string path)
        {
            _path = path;
        }

        public void Report(InstallProgress progress)
        {
            _stage = progress.Stage;
            _stagePercent = progress.StagePercent;
            _speedText = progress.SpeedText;
            Write(
                "running",
                progress.Percent,
                progress.Message,
                stage: progress.Stage,
                stagePercent: progress.StagePercent,
                speedText: progress.SpeedText);
        }

        public void Write(
            string state,
            double percent,
            string message,
            bool force = false,
            string? logPath = null,
            InstallProgressStage? stage = null,
            double? stagePercent = null,
            string? speedText = null)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                if (!force && now - _lastWriteUtc < TimeSpan.FromMilliseconds(250) &&
                    string.Equals(_lastMessage, message, StringComparison.Ordinal))
                {
                    return;
                }

                _lastWriteUtc = now;
                _lastMessage = message;
                try
                {
                    var effectiveStage = stage ?? _stage;
                    var effectiveStagePercent = stagePercent ?? _stagePercent;
                    var effectiveSpeedText = speedText ?? _speedText;
                    AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(
                        new EnvironmentInstallWorkerProgress(
                            state,
                            Compat.Clamp(percent, 0, 100),
                            message,
                            logPath,
                            effectiveStage,
                            effectiveStagePercent,
                            effectiveSpeedText),
                        JsonOptions));
                }
                catch
                {
                    // Progress reporting must not interrupt the actual elevated installation.
                }
            }
        }
    }
}

internal sealed record EnvironmentInstallWorkerProgress(
    string State,
    double Percent,
    string Message,
    string? LogPath = null,
    InstallProgressStage Stage = InstallProgressStage.Installing,
    double? StagePercent = null,
    string? SpeedText = null);
