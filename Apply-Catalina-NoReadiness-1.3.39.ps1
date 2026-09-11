$ErrorActionPreference = 'Stop'

$runtimePath = 'EnvironmentRuntimeService.cs'
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$startMarker = '    public async Task<string> StartTomcatInCatalinaConsoleAsync('
$endMarker = '    public NginxRuntimeOptions GetNginxOptions()'
$start = $runtime.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $runtime.IndexOf($endMarker, $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) {
    throw 'Unable to locate StartTomcatInCatalinaConsoleAsync block.'
}

$replacement = @'
    public Task<string> StartTomcatInCatalinaConsoleAsync(
        CancellationToken cancellationToken = default, Action<TomcatStartupProgress>? tomcatProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tomcatRoot = RequireTomcatRoot();
        if (IsRunning(EnvironmentKind.Tomcat))
        {
            throw new InvalidOperationException("Tomcat 已经在运行。请先停止后台 Tomcat，再使用 Catalina 前台方式启动。");
        }

        var binDirectory = Path.Combine(tomcatRoot, "bin");
        var catalina = Path.Combine(binDirectory, "catalina.bat");
        if (!File.Exists(catalina))
        {
            throw new FileNotFoundException("未找到 Tomcat Catalina 启动脚本。", catalina);
        }

        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(tomcatRoot);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "run-tomcat-catalina.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Catalina
            cd /d "{binDirectory}"
            call catalina.bat run
            echo.
            echo Tomcat has exited. Review the Catalina output above.
            echo Press any key to close this window.
            pause >nul
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var catalinaWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);

        return Task.FromResult(
            "Catalina 窗口已启动。MCPanel 不再等待端口或执行 HTTP 就绪诊断；启动过程请直接查看 Catalina 窗口。");
    }

'@
$runtime = $runtime.Substring(0, $start) + $replacement + $runtime.Substring($end)
Set-Content -LiteralPath $runtimePath -Value $runtime -Encoding UTF8

$projectPath = 'MCPanel.csproj'
$project = Get-Content -LiteralPath $projectPath -Raw
$project = $project.Replace('<Version>1.3.38</Version>', '<Version>1.3.39</Version>')
$project = $project.Replace('<FileVersion>1.3.38.0</FileVersion>', '<FileVersion>1.3.39.0</FileVersion>')
$project = $project.Replace('<AssemblyVersion>1.3.38.0</AssemblyVersion>', '<AssemblyVersion>1.3.39.0</AssemblyVersion>')
if (-not $project.Contains('<Version>1.3.39</Version>')) {
    throw 'Version bump to 1.3.39 failed.'
}
Set-Content -LiteralPath $projectPath -Value $project -Encoding UTF8

$notes = @'
# MCPanel 1.3.39

- 修复“以 Catalina 方式启动”仍执行自动就绪诊断的问题：打开 Catalina 前台窗口后立即返回，不再等待所有端口，也不再逐个应用执行 HTTP 就绪检查。
- 删除 Catalina 启动流程中的“诊断模式”状态与完成判定，避免 Tomcat 已经正常在后台/前台启动却被 MCPanel 因应用 HTTP 检查失败误报为启动失败。
- Catalina 窗口只负责展示 Tomcat 原始控制台输出；MCPanel 不再根据应用页面是否能返回 HTTP 响应来决定 Catalina 是否启动成功。
'@
Set-Content -LiteralPath 'RELEASE-NOTES.md' -Value $notes -Encoding UTF8

$testPath = 'MCPanel.Tests/ReliabilityTests.CatalinaNoReadiness139.cs'
$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void CatalinaConsoleLaunchDoesNotRunAutomaticReadinessDiagnostics()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("public Task<string> StartTomcatInCatalinaConsoleAsync", StringComparison.Ordinal);
        var end = runtime.IndexOf("public NginxRuntimeOptions GetNginxOptions()", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = runtime.Substring(start, end - start);

        StringAssert.Contains(method, "ProcessRunner.StartFile");
        StringAssert.Contains(method, "call catalina.bat run");
        StringAssert.Contains(method, "不再等待端口或执行 HTTP 就绪诊断");
        Assert.IsFalse(method.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ReadHttpPorts", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ArePortsListening", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("Catalina 端口就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("真正 HTTP 就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("诊断模式", StringComparison.Ordinal));
    }
}
'@
Set-Content -LiteralPath $testPath -Value $test -Encoding UTF8

Write-Host 'Applied Catalina no-readiness changes for MCPanel 1.3.39.'
