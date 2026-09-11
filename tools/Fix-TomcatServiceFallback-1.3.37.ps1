$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path (Get-Location) $Path))
}

function Write-Text([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $Path), $Text, $utf8)
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) {
        throw "Expected text was not found in $Path.`n--- expected ---`n$Old"
    }
    Write-Text $Path ($text.Replace($Old, $New))
}

Write-Host '1/3 Make manual/shared Tomcat start succeed independently of SCM timing...'
Replace-Exact 'EnvironmentRuntimeService.cs' @'
                TomcatProductStartupManager.RemoveRegistration();
                TomcatWindowsServiceManager.Start();
                await Task.Delay(500, cancellationToken);
                return $"Tomcat Server 启动请求已发送；不再等待 Windows 服务状态或读取启动进度。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@ @'
                TomcatProductStartupManager.RemoveRegistration();
                if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    try
                    {
                        // Best effort only: SCM acknowledgement is not runtime truth.
                        TomcatWindowsServiceManager.Start();
                    }
                    catch (Exception serviceError)
                    {
                        EnvironmentOperationDiagnostics.RecordFailure(
                            "环境管理",
                            "发送 Tomcat Windows Service 启动请求",
                            serviceError);
                    }

                    for (var attempt = 0; attempt < 10 && !TomcatProductInstanceManager.IsSharedTomcatRunning(); attempt++)
                    {
                        await Task.Delay(200, cancellationToken);
                    }
                }

                if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    // If SCM is still Starting/Stopping (or returns a false timeout),
                    // start the real Tomcat process directly. This keeps panel start
                    // and post-uninstall restore independent from SCM timing.
                    var startup = Path.Combine(tomcatRoot, "bin", "startup.bat");
                    if (!File.Exists(startup))
                    {
                        throw new FileNotFoundException("未找到 Tomcat startup.bat。", startup);
                    }
                    EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(tomcatRoot);
                    var startResult = await ProcessRunner.RunFileAsync(
                        startup,
                        string.Empty,
                        Path.Combine(tomcatRoot, "bin"),
                        elevated: false,
                        cancellationToken,
                        captureOutput: false,
                        timeout: TimeSpan.FromSeconds(30));
                    if (startResult.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Tomcat startup.bat 退出码：{startResult.ExitCode}");
                    }
                }

                return $"Tomcat Server 启动请求已发送；运行状态按实际 Java 进程判断，不再等待 Windows 服务状态或读取启动进度。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@

Write-Host '2/3 Stop the Tomcat service wrapper before product-uninstall process cleanup...'
Replace-Exact 'ProductDeploymentService.cs' @'
        if (sharedTomcatWasRunning)
        {
            // server.xml cannot be safely edited while the shared Catalina
            // process owns the connector.  Stop it, remove the Service, then
            // restore the previous all-applications mode in the finally block.
            ReportUninstallProgress(progress, 42, "正在停止 Tomcat 全部应用模式...");
            await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
        }
'@ @'
        if (sharedTomcatWasRunning)
        {
            // Stop the service wrapper first so the later restore is not blocked
            // by an SCM service that still says Running while its Java child is gone.
            // SCM status itself is never used as the success criterion.
            ReportUninstallProgress(progress, 42, "正在停止 Tomcat 全部应用模式...");
            if (tomcatRoot is not null && TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
            {
                try
                {
                    TomcatWindowsServiceManager.Stop();
                }
                catch (Exception serviceError)
                {
                    EnvironmentOperationDiagnostics.RecordFailure(
                        "产品管理",
                        $"卸载 {product.ProductId} 时发送 Tomcat Windows Service 停止请求",
                        serviceError);
                }
            }
            await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
        }
'@

Write-Host '3/3 Extend regression coverage for service-timeout fallback and uninstall restore...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatServiceDecoupling137.cs'
$test = Read-Text $testPath
$needle = @'
        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
'@
$replacement = @'
        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(deployment, "TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot)");
        StringAssert.Contains(deployment, "await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);");

        StringAssert.Contains(runtime, "var startup = Path.Combine(tomcatRoot, \"bin\", \"startup.bat\")");
        StringAssert.Contains(runtime, "运行状态按实际 Java 进程判断");
        StringAssert.Contains(runtime, "catch (Exception serviceError)");
'@
if (-not $test.Contains($needle)) {
    throw 'Unable to extend Tomcat service decoupling regression.'
}
Write-Text $testPath ($test.Replace($needle, $replacement))

Write-Host 'Tomcat service timing fallback fixes applied.'
