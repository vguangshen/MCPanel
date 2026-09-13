$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = Get-Content -LiteralPath $Path -Raw
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "Expected exactly one $Description block in $Path, found $count."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText((Resolve-Path $Path), $text, [Text.UTF8Encoding]::new($true))
    Write-Host "Updated $Path: $Description."
}

# 1) Windows-logon restoration must follow the normal Start semantics introduced
# in 1.3.50. Catalina run is reserved for the explicit diagnostic action.
$path = 'TomcatLogonStartup.cs'
Replace-ExactlyOnce -Path $path -Description 'logon startup summary' -Old @'
/// Windows logon Run entry (--tray). Logon and manual startup intentionally use
/// the same visible Catalina-console launcher.
'@ -New @'
/// Windows logon Run entry (--tray). Logon and normal manual startup intentionally
/// use the same visible standard-start launcher; Catalina run stays diagnostic-only.
'@
Replace-ExactlyOnce -Path $path -Description 'logon startup installation probe' -Old @'
File.Exists(Path.Combine(tomcatRoot, "bin", "catalina.bat"));
'@ -New @'
File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat"));
'@
Replace-ExactlyOnce -Path $path -Description 'logon startup message' -Old @'
$"Windows 登录恢复：立即以可见 Catalina CMD 启动共享 Tomcat，CATALINA_BASE={root}",
'@ -New @'
$"Windows 登录恢复：立即以标准 start 模式启动共享 Tomcat，CATALINA_BASE={root}",
'@
Replace-ExactlyOnce -Path $path -Description 'logon startup launcher comment' -Old @'
            // Use exactly the same launcher as normal manual Tomcat startup.
            // Do not delay, hide the console, wait for readiness or retry in a
            // second background process. Catalina output remains visible so the
            // operator can diagnose startup in the same way as a manual start.
            EnvironmentRuntimeService.LaunchTomcatConsole(root);
            TomcatProductStartupManager.WriteLog("Windows 登录恢复：已打开共享 Tomcat Catalina CMD 控制台。", null);
'@ -New @'
            // Use exactly the same standard-start launcher as normal manual Tomcat
            // startup. Do not delay, hide the console, wait for readiness or retry
            // in a second background process. Explicit Catalina run remains a
            // user-invoked diagnostic action only.
            EnvironmentRuntimeService.LaunchTomcatStartConsole(root);
            TomcatProductStartupManager.WriteLog("Windows 登录恢复：已按标准 start 模式打开共享 Tomcat CMD 控制台。", null);
'@

$path = 'App.xaml.cs'
Replace-ExactlyOnce -Path $path -Description 'tray Tomcat restore comment' -Old @'
            // Match normal manual Tomcat startup at Windows logon: when MCPanel
            // is launched through the --tray Run entry, immediately restore the
            // shared server with the same visible Catalina CMD launcher. No
            // startup delay or hidden-console path is used here.
'@ -New @'
            // Match normal manual Tomcat startup at Windows logon: when MCPanel
            // is launched through the --tray Run entry, immediately restore the
            // shared server with the same visible standard-start launcher. The
            // explicit Catalina run path remains a manual diagnostic action.
'@

# 2) Installation completion is a normal start, not an implicit Catalina diagnostic run.
$path = 'EnvironmentInstaller.cs'
Replace-ExactlyOnce -Path $path -Description 'Tomcat post-install launcher' -Old @'
        progress(InstallingProgress(92, "正在打开 Tomcat Server CMD 控制台...", 78));
        EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并已在可见 CMD 控制台中启动；后续启动与重启不再通过 Windows Service 隐藏运行。"));
'@ -New @'
        progress(InstallingProgress(92, "正在以标准 start 模式打开 Tomcat Server CMD 控制台...", 78));
        EnvironmentRuntimeService.LaunchTomcatStartConsole(tomcatRoot);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并已按标准 start 模式在可见 CMD 控制台中启动；后续启动与重启不再通过 Windows Service 隐藏运行。"));
'@

# 3) Keep only the legacy Tomcat service surface that is still needed to detect,
# stop and delete services left by older MCPanel versions. Registration/start APIs
# are dead after the interactive-start migration and should not be callable again.
$path = 'ManagedComponentWindowsServices.cs'
Replace-ExactlyOnce -Path $path -Description 'Tomcat legacy service manager surface' -Old @'
internal static class TomcatWindowsServiceManager
{
    public const string ServiceName = "MCPanelTomcat";
    public const string ServiceDisplayName = "Tomcat Server (MCPanel)";
    public const string ServiceDescription = "Tomcat 8.5.57 Web 服务器（由 MCPanel 管理；不自动恢复崩溃实例）。";

    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRegisteredForRoot(string tomcatRoot) =>
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    public static string BuildServiceImagePath(string executablePath, string tomcatRoot) =>
        ManagedWindowsServiceController.BuildServiceImagePath(executablePath, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    public static void EnsureRegistered(string executablePath, string tomcatRoot) =>
        ManagedWindowsServiceController.EnsureRegistered(
            ServiceName,
            ServiceDisplayName,
            ServiceDescription,
            executablePath,
            TomcatWindowsServiceHost.ServiceArgument,
            tomcatRoot,
            configureRecovery: false);

    public static void Start()
    {
        // Tomcat is launched through SCM, but MCPanel deliberately does not wait
        // for SCM status transitions. The Java process/ports are the runtime truth.
        ManagedWindowsServiceController.DisableRecovery(ServiceName, ServiceDisplayName);
        ManagedWindowsServiceController.StartWithoutStatusWait(ServiceName, ServiceDisplayName);
    }

    public static void Stop() => ManagedWindowsServiceController.StopWithoutStatusWait(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.DeleteWithoutStatusWait(ServiceName, ServiceDisplayName);
}
'@ -New @'
internal static class TomcatWindowsServiceManager
{
    public const string ServiceName = "MCPanelTomcat";
    public const string ServiceDisplayName = "Tomcat Server (MCPanel)";

    // Cleanup-only compatibility for services registered by older MCPanel builds.
    // Current Tomcat startup must never register or start an SCM service again.
    public static bool IsRegisteredForRoot(string tomcatRoot) =>
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    public static void Stop() => ManagedWindowsServiceController.StopWithoutStatusWait(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.DeleteWithoutStatusWait(ServiceName, ServiceDisplayName);
}
'@

# 4) Align regression tests with the cleaned semantics.
$path = 'MCPanel.Tests/ReliabilityTests.TomcatLogonStartup147.cs'
Replace-ExactlyOnce -Path $path -Description 'logon startup regression method' -Old @'
    public void TomcatLogonStartup148_UsesImmediateVisibleManualCatalinaLauncher()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var startup = ReadRepositoryFile("TomcatLogonStartup.cs");

        StringAssert.Contains(app, "if (startInTray)");
        StringAssert.Contains(app, "TomcatLogonStartup.TryRestoreSharedTomcatAsync()");
        StringAssert.Contains(startup, "EnvironmentRuntimeService.LaunchTomcatConsole(root)");
        Assert.IsFalse(startup.Contains("InitialDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("RetryDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartupProbeTimeout", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("ProcessWindowStyle.Hidden", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartHiddenCatalina", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("WaitForSharedTomcatAsync", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartProduct", StringComparison.OrdinalIgnoreCase));

        // Manual environment actions remain on the exact same visible launcher.
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "windowStyle: ProcessWindowStyle.Normal");
    }
'@ -New @'
    public void TomcatLogonStartup151_UsesImmediateVisibleStandardStartLauncher()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var startup = ReadRepositoryFile("TomcatLogonStartup.cs");

        StringAssert.Contains(app, "if (startInTray)");
        StringAssert.Contains(app, "TomcatLogonStartup.TryRestoreSharedTomcatAsync()");
        StringAssert.Contains(startup, "EnvironmentRuntimeService.LaunchTomcatStartConsole(root)");
        Assert.IsFalse(startup.Contains("EnvironmentRuntimeService.LaunchTomcatConsole(root)", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("InitialDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("RetryDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartupProbeTimeout", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("ProcessWindowStyle.Hidden", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartHiddenCatalina", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("WaitForSharedTomcatAsync", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartProduct", StringComparison.OrdinalIgnoreCase));

        StringAssert.Contains(runtime, "internal static void LaunchTomcatStartConsole(string tomcatRoot)");
        StringAssert.Contains(runtime, "call startup.bat");
        StringAssert.Contains(runtime, "internal static void LaunchTomcatConsole(string tomcatRoot)");
        StringAssert.Contains(runtime, "call catalina.bat run");
    }
'@

$path = 'MCPanel.Tests/ReliabilityTests.VisibleSharedTomcat143.cs'
Replace-ExactlyOnce -Path $path -Description 'Tomcat installer launcher regression' -Old @'
        StringAssert.Contains(block, "EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(block, "TomcatWindowsServiceManager.Delete()");
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
'@ -New @'
        StringAssert.Contains(block, "EnvironmentRuntimeService.LaunchTomcatStartConsole(tomcatRoot)");
        StringAssert.Contains(block, "TomcatWindowsServiceManager.Delete()");
        Assert.IsFalse(block.Contains("EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot)", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
'@

$path = 'MCPanel.Tests/ReliabilityTests.ServiceAutostart136.cs'
Replace-ExactlyOnce -Path $path -Description 'obsolete Tomcat service registration regression' -Old @'
    [TestMethod]
    public void TomcatServiceImagePathTargetsSharedServerRoot()
    {
        var exe = Path.Combine(Path.GetTempPath(), "MCPanel", "MCPanel.exe");
        var root = Path.Combine(Path.GetTempPath(), "MCPanel", "Tomcat", "apache-tomcat-8.5.57");
        var imagePath = TomcatWindowsServiceManager.BuildServiceImagePath(exe, root);

        StringAssert.Contains(imagePath, TomcatWindowsServiceHost.ServiceArgument);
        StringAssert.Contains(imagePath, Path.GetFullPath(root));
        Assert.AreEqual("MCPanelTomcat", TomcatWindowsServiceManager.ServiceName);
    }
'@ -New @'
    [TestMethod]
    public void TomcatLegacyServiceManagerExposesCleanupOnly()
    {
        var source = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var start = source.IndexOf("internal static class TomcatWindowsServiceManager", StringComparison.Ordinal);
        var end = source.IndexOf("internal static class FrpWindowsServiceManager", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var manager = source.Substring(start, end - start);

        StringAssert.Contains(manager, "IsRegisteredForRoot");
        StringAssert.Contains(manager, "StopWithoutStatusWait");
        StringAssert.Contains(manager, "DeleteWithoutStatusWait");
        Assert.IsFalse(manager.Contains("BuildServiceImagePath", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("public static void Start()", StringComparison.Ordinal));
    }
'@

# 5) Bump patch version only after all semantic replacements succeeded.
$path = 'MCPanel.csproj'
Replace-ExactlyOnce -Path $path -Description 'Version' -Old '<Version>1.3.50</Version>' -New '<Version>1.3.51</Version>'
Replace-ExactlyOnce -Path $path -Description 'FileVersion' -Old '<FileVersion>1.3.50.0</FileVersion>' -New '<FileVersion>1.3.51.0</FileVersion>'
Replace-ExactlyOnce -Path $path -Description 'AssemblyVersion' -Old '<AssemblyVersion>1.3.50.0</AssemblyVersion>' -New '<AssemblyVersion>1.3.51.0</AssemblyVersion>'

Write-Host 'MCPanel 1.3.51 legacy cleanup prepared.'
