using System.ComponentModel;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

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
        if (!TryGetRequest(args, out var kind, out var progressPath, out var selectedReleaseId, out var interactiveWindowsUser))
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

            if (kind == EnvironmentKind.SqlServer)
            {
                await PrepareSqlServerInstallAsync(reporter).ConfigureAwait(true);
            }

            var pendingRenameSnapshot = kind == EnvironmentKind.SqlServer
                ? CapturePendingFileRenameOperations()
                : null;

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

            try
            {
                await installer.InstallAsync(item, reporter.Report, CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                if (kind == EnvironmentKind.SqlServer)
                {
                    RestorePendingFileRenameOperations(pendingRenameSnapshot);
                }
            }

            if (kind == EnvironmentKind.SqlServer && !string.IsNullOrWhiteSpace(interactiveWindowsUser))
            {
                reporter.Write(
                    "running",
                    98,
                    "正在确认当前桌面用户的 SQL Server 管理权限...",
                    force: true,
                    stage: InstallProgressStage.Installing,
                    stagePercent: 98);
                try
                {
                    EnsureSqlServerWindowsLogin(interactiveWindowsUser!);
                }
                catch (Exception accessError)
                {
                    // SQL Server itself is already installed and usable through sa.
                    // Do not turn a convenience-login repair into a false installation
                    // failure; retain a diagnostic so the user can still troubleshoot it.
                    EnvironmentOperationDiagnostics.RecordFailure(
                        "SqlServer",
                        "补充当前桌面用户 SQL Server 管理权限",
                        accessError,
                        Path.Combine(ComponentPaths.WorkRoot, "sqlserver-interactive-login.log"));
                }
            }

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

    private static async Task PrepareSqlServerInstallAsync(ProgressReporter reporter)
    {
        var runtimeService = new EnvironmentRuntimeService();
        var state = runtimeService.GetState(EnvironmentKind.SqlServer);
        if (state.IsInstalled)
        {
            if (!state.IsRunning)
            {
                reporter.Write(
                    "running",
                    2,
                    "检测到已有 SQL Server，正在先尝试正常启动现有实例...",
                    force: true,
                    stage: InstallProgressStage.Preparing,
                    stagePercent: 2);
                try
                {
                    await runtimeService.StartAsync(EnvironmentKind.SqlServer).ConfigureAwait(true);
                }
                catch (Exception startError)
                {
                    throw new InvalidOperationException(
                        "检测到已有 MSSQLSERVER 实例，但当前实例无法正常启动。为避免普通“安装”操作覆盖或清理现有数据库，MCPanel 已停止本次安装。请先在“环境”页尝试启动/重启 SQL Server；如果确认该实例可以删除，再使用“卸载”完成清理后重新安装。",
                        startError);
                }

                if (!runtimeService.IsRunning(EnvironmentKind.SqlServer))
                {
                    throw new InvalidOperationException(
                        "检测到已有 MSSQLSERVER 实例，但启动后仍未进入运行状态。MCPanel 已停止本次安装，不会自动清理现有 SQL Server。请先修复或明确卸载旧实例后再安装。");
                }
            }

            // A healthy existing instance is intentionally allowed through. The
            // installer will only repair MCPanel's expected local connection settings
            // and then exit instead of performing a fresh installation.
            return;
        }

        if (HasStaleSqlServerDefaultInstanceRegistration())
        {
            throw new InvalidOperationException(
                "检测到 MSSQLSERVER 默认实例的注册信息仍然存在，但 Windows 服务已经不存在。普通安装不会自动删除这些系统残留。请先使用“卸载 SQL Server”完成清理，或确认旧实例已处理后再重新安装。");
        }
    }

    private static string[]? CapturePendingFileRenameOperations()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager",
                writable: false);
            var value = key?.GetValue("PendingFileRenameOperations");
            return value switch
            {
                string[] values => values.Where(item => item is not null).ToArray(),
                string text when !string.IsNullOrEmpty(text) => new[] { text },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void RestorePendingFileRenameOperations(string[]? originalValues)
    {
        if (originalValues is null || originalValues.Length == 0)
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager",
                writable: true);
            if (key is null)
            {
                return;
            }

            var current = key.GetValue("PendingFileRenameOperations") switch
            {
                string[] values => values,
                string text when !string.IsNullOrEmpty(text) => new[] { text },
                _ => Array.Empty<string>()
            };

            var merged = originalValues
                .Concat(current)
                .Where(item => item is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (merged.Length > 0)
            {
                key.SetValue("PendingFileRenameOperations", merged, RegistryValueKind.MultiString);
            }
        }
        catch (Exception restoreError)
        {
            EnvironmentOperationDiagnostics.RecordFailure(
                "SqlServer",
                "恢复 Windows PendingFileRenameOperations",
                restoreError,
                Path.Combine(ComponentPaths.WorkRoot, "sqlserver-pending-rename-restore.log"));
        }
    }

    private static bool HasStaleSqlServerDefaultInstanceRegistration()
    {
        if (WindowsServiceExists("MSSQLSERVER"))
        {
            return false;
        }

        foreach (var view in RegistryViews())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var instances = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                var instanceId = Convert.ToString(instances?.GetValue("MSSQLSERVER"));
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static IEnumerable<RegistryView> RegistryViews()
    {
        if (Environment.Is64BitOperatingSystem)
        {
            yield return RegistryView.Registry64;
        }
        yield return RegistryView.Registry32;
    }

    private static bool WindowsServiceExists(string serviceName)
    {
        try
        {
            return ProcessRunner.RunSynchronously(
                "sc.exe",
                $"query {Compat.QuoteCommandLineArgument(serviceName)}",
                ComponentPaths.ApplicationRoot,
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSqlServerWindowsLogin(string windowsUser)
    {
        var user = (windowsUser ?? string.Empty).Trim();
        if (user.Length == 0 || user.Length > 256 || user.Any(char.IsControl) ||
            user.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var credentials = SqlServerCredentialStore.Load();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SqlServerCredentialStore.GetServerTarget(credentials),
            InitialCatalog = "master",
            UserID = credentials.UserName,
            Password = credentials.Password,
            IntegratedSecurity = false,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        };

        var literal = user.Replace("'", "''", StringComparison.Ordinal);
        var identifier = user.Replace("]", "]]", StringComparison.Ordinal);
        var query = $@"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{literal}')
BEGIN
    CREATE LOGIN [{identifier}] FROM WINDOWS;
END;
IF NOT EXISTS (
    SELECT 1
    FROM sys.server_role_members AS rm
    INNER JOIN sys.server_principals AS rolePrincipal ON rolePrincipal.principal_id = rm.role_principal_id
    INNER JOIN sys.server_principals AS memberPrincipal ON memberPrincipal.principal_id = rm.member_principal_id
    WHERE rolePrincipal.name = N'sysadmin' AND memberPrincipal.name = N'{literal}'
)
BEGIN
    ALTER SERVER ROLE [sysadmin] ADD MEMBER [{identifier}];
END;";

        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 30;
        command.ExecuteNonQuery();
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

            if (kind == EnvironmentKind.SqlServer)
            {
                var interactiveUser = WindowsIdentity.GetCurrent().Name ?? string.Empty;
                arguments += " " + Compat.QuoteCommandLineArgument(interactiveUser);
            }
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
        out string? selectedReleaseId,
        out string? interactiveWindowsUser)
    {
        kind = default;
        progressPath = string.Empty;
        selectedReleaseId = null;
        interactiveWindowsUser = null;
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
            if (kind == EnvironmentKind.SqlServer && index + 4 < args.Length)
            {
                var suppliedUser = (args[index + 4] ?? string.Empty).Trim();
                if (suppliedUser.Length <= 256 && !suppliedUser.Any(char.IsControl))
                {
                    interactiveWindowsUser = suppliedUser;
                }
            }
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
