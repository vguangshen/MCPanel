from pathlib import Path
import re


def load(path: str):
    raw = Path(path).read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom


def save(path: str, text: str, bom: bool):
    Path(path).write_text(text, encoding="utf-8-sig" if bom else "utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, found {count}")
    return updated


# 1) Restart must not hide a real stop failure.
path = "EnvironmentRuntimeService.cs"
text, bom = load(path)
text = replace_once(
    text,
    """        try
        {
            await StopAsync(kind, cancellationToken);
        }
        catch
        {
            // Continue with start even when the service was already stopped.
        }

        await Task.Delay(1200, cancellationToken);""",
    """        try
        {
            await StopAsync(kind, cancellationToken);
        }
        catch (Exception stopError)
        {
            // A stop command may report an error even though the process/service
            // actually reached Stopped. Continue only in that benign case.
            if (IsRunning(kind))
            {
                throw new InvalidOperationException(
                    $"{DisplayName(kind)} 停止失败，当前仍在运行，已取消重启：{stopError.Message}",
                    stopError);
            }
        }

        await Task.Delay(1200, cancellationToken);""",
    "restart stop verification")

# 2) Starting/restarting MySQL must never silently rewrite root password.
auto_reset = "                        if (!(Test-RootPassword $paths)) { Reset-RootPassword $paths }"
replacement = "                        if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但保存的 root 凭据无法验证。为避免意外修改数据库密码，MCPanel 已停止自动重置；请恢复正确的凭据文件后重试。' }"
count = text.count(auto_reset)
if count != 2:
    raise RuntimeError(f"MySQL automatic password reset: expected 2 calls, found {count}")
text = text.replace(auto_reset, replacement)

# 3) SQL Server uninstall is scoped to MCPanel's default instance. Shared drivers,
# other named instances, Program Files/ProgramData, and global SQL registry trees stay intact.
sql_scope_block = r"""            Invoke-Step '停止 MCPanel SQL Server 默认实例服务' {
                Get-Service -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
                } | ForEach-Object { Stop-Sql-Service $_.Name }
            }

            Invoke-Step '调用官方安装器卸载 SQL Server 默认实例' {
                $instanceNames = @('MSSQLSERVER')
                $setupCandidates = @()
                foreach ($root in @(
                    (Join-Path $workRoot 'SqlServer2012ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025EnterpriseDeveloperMedia'),
                    (Join-Path $workRoot 'SqlServer2022ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2017ExpressMedia'),
                    $componentRoot,
                    $migratedDataRoot,
                    $legacyDataRoot
                )) {
                    if (Test-Path $root) {
                        $setupCandidates += Get-ChildItem $root -Filter setup.exe -Recurse -ErrorAction SilentlyContinue
                    }
                }

                $setupCandidates |
                    Sort-Object FullName -Unique |
                    ForEach-Object {
                        $setupPath = $_.FullName
                        foreach ($instance in $instanceNames) {
                            Write-Output ('Running setup uninstall: ' + $setupPath + ' instance=' + $instance)
                            $args = '/ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=' + $instance + ' /Q'
                            $process = Start-Process -FilePath $setupPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                            if ($process) { Write-Output ('setup exit code: ' + $process.ExitCode) }
                        }
                    }
            }

            Invoke-Step '保留共享 SQL Server 客户端组件' {
                Write-Output '保留 SQL Native Client、ODBC/OLE DB Driver、SQL Browser、VSS Writer 等共享组件，避免影响其他软件或 SQL Server 实例。'
            }

            Invoke-Step '删除 MCPanel SQL Server 默认实例残留服务项' {
                Get-Service -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
                } | ForEach-Object { Delete-Sql-Service $_.Name }
            }

            Invoke-Step '删除 MCPanel 管理目录和安装缓存' {
                $paths = @(
                    $componentRoot,
                    $migratedDataRoot,
                    $legacyDataRoot,
                    (Join-Path $workRoot 'SqlServer2012ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2022ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025EnterpriseDeveloperMedia'),
                    (Join-Path $workRoot 'SqlServer2017ExpressMedia')
                )
                foreach ($path in $paths) { Remove-Tree $path }
            }

            Invoke-Step '保留共享 SQL Server 注册表与程序目录' {
                Write-Output '全局 Microsoft SQL Server 注册表树、Program Files 和 ProgramData 由官方卸载器管理；MCPanel 不再强制删除，以保护其他实例。'
                Write-Output '保留 Windows NVMe 4KB 扇区兼容项，避免 SQL Server 卸载后立即重装需要重启系统。'
            }

            Invoke-Step '清理 MCPanel SQL Server 防火墙规则' {
                Get-NetFirewallRule -ErrorAction SilentlyContinue |
                    Where-Object { $_.DisplayName -like 'MCPanel SQL Server *' } |
                    Remove-NetFirewallRule -ErrorAction SilentlyContinue
            }

            $remainingServices = @(Get-Service -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
            })
            $remainingManagedPaths ="""
text = regex_once(
    text,
    r"            Invoke-Step '停止 SQL Server 服务' \{.*?            \$remainingManagedPaths =",
    sql_scope_block,
    "SQL Server uninstall scope")

text = replace_once(
    text,
    '                return "SQL Server 已执行完整卸载清理。建议重启 Windows 后再重新安装。";',
    '                return "SQL Server 默认实例及 MCPanel 管理的数据、安装缓存和防火墙规则已卸载；共享驱动、其他实例及全局 SQL Server 目录已保留。";',
    "SQL uninstall result")
save(path, text, bom)


# 4) A corrupt deployment-state JSON is quarantined instead of destructively deleted.
path = "ProductDeploymentService.cs"
text, bom = load(path)
start = text.index("    public void PruneStaleDeploymentState()")
end = text.index("    public async Task<ProductDeploymentResult> DeployAsync", start)
method = text[start:end]
old_catch = """            catch
            {
                DeleteFileIfExists(stateFile);
            }"""
count = method.count(old_catch)
if count != 2:
    raise RuntimeError(f"deployment state prune: expected 2 destructive catches, found {count}")
method = method.replace(
    old_catch,
    """            catch (Exception error)
            {
                QuarantineCorruptDeploymentState(stateFile, error);
            }""")
helper = """    private static void QuarantineCorruptDeploymentState(string stateFile, Exception error)
    {
        try
        {
            if (!File.Exists(stateFile))
            {
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var quarantine = stateFile + $".corrupt-{timestamp}";
            for (var suffix = 1; File.Exists(quarantine); suffix++)
            {
                quarantine = stateFile + $".corrupt-{timestamp}-{suffix}";
            }

            File.Move(stateFile, quarantine);
            Directory.CreateDirectory(ComponentPaths.WorkRoot);
            RollingLogWriter.Append(
                Path.Combine(ComponentPaths.WorkRoot, "deployment-state-recovery.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 部署状态文件损坏，已隔离而不是删除：{stateFile} -> {quarantine}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // A damaged state file must not prevent MCPanel from opening. If it
            // cannot be quarantined, leave it in place for a later repair.
        }
    }

"""
text = text[:start] + method + helper + text[end:]
save(path, text, bom)


# 5) A release manifest version must be bound to the actual MCPanel.exe FileVersion.
path = "ApplicationUpdateService.cs"
text, bom = load(path)
text = replace_once(
    text,
    """            var fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
            var version = string.IsNullOrWhiteSpace(declaredVersion) ? NormalizeVersionText(fileVersion) : NormalizeVersionText(declaredVersion);
            if (!Version.TryParse(version, out _)) throw new InvalidDataException("更新包版本号无效。");""",
    """            var fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
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
            }""",
    "application update version binding")
save(path, text, bom)


# Existing remote update test intentionally used a fake manifest version. That is no longer valid.
changed = 0
for candidate in Path("MCPanel.Tests").glob("ReliabilityTests*.cs"):
    text, bom = load(str(candidate))
    marker = 'const string version = "999.0.0.1";'
    if marker in text:
        text = text.replace(marker, 'var version = ApplicationUpdateService.CurrentVersion.ToString();', 1)
        save(str(candidate), text, bom)
        changed += 1
if changed != 1:
    raise RuntimeError(f"remote update fake-version test: expected 1 change, found {changed}")


# 6) Update the SQL Server uninstall confirmation wherever that partial MainWindow method lives.
old_confirmation = '        EnvironmentKind.SqlServer => "确定完整卸载 SQL Server 吗？\\n会停止并移除 SQL Server 服务、相关系统组件、注册表项、Program Files/ProgramData 残留目录，以及 MCPanel 的 MSSQL 数据目录。\\n仅在确认本机没有其他需要保留的 SQL Server 实例时执行。",'
new_confirmation = '        EnvironmentKind.SqlServer => "确定卸载 MCPanel 使用的 SQL Server 默认实例吗？\\n会停止并移除 MSSQLSERVER 默认实例，以及 MCPanel 的数据、安装缓存和防火墙规则；其他 SQL Server 实例、共享 ODBC/OLE DB 驱动和全局程序目录会保留。",'
changed = 0
for candidate in Path(".").glob("MainWindow*.cs"):
    text, bom = load(str(candidate))
    if old_confirmation in text:
        text = text.replace(old_confirmation, new_confirmation, 1)
        save(str(candidate), text, bom)
        changed += 1
if changed != 1:
    raise RuntimeError(f"SQL uninstall confirmation: expected 1 change, found {changed}")

print("1.2.9 reliability patch applied successfully")
