using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MCPanel;

public sealed class TomcatProductInstanceManager
{
    internal static readonly TimeSpan ProductStartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly SemaphoreSlim RuntimeLock = new(1, 1);
    private static readonly Regex NetstatListeningLineRegex = new(
        @"^\s*TCP\s+(?:\[[^\]]+\]|[^\s:]+):(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed class JavaProcessInfo
    {
        public JavaProcessInfo(int processId, string commandLine)
        {
            ProcessId = processId;
            CommandLine = commandLine;
        }

        public int ProcessId { get; }
        public string CommandLine { get; }
    }

    public bool IsRunning(string productId)
    {
        var info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId);
        return info is not null && IsJavaPortListening(info.Port);
    }

    public string GetLogDirectory(string productId) =>
        Path.Combine(GetInstanceRoot(productId), "logs");

    public async Task<string> StartAsync(string productId, bool catalinaMode, CancellationToken cancellationToken = default)
    {
        var info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId)
            ?? throw new InvalidOperationException($"未找到 {productId} 的 Tomcat 部署信息，请先修复绑定。");
        var tomcatHome = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");

        await RuntimeLock.WaitAsync(cancellationToken);
        try
        {
            await ProductDeploymentService.EnsureTomcatProductServiceAsync(info, cancellationToken);
            info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId) ?? info;
            var instanceRoot = await PrepareInstanceAsync(tomcatHome, info, cancellationToken);

            if (await IsJavaProcessForBaseAsync(instanceRoot, cancellationToken) && IsTcpPortListening(info.Port))
            {
                return $"{productId} 已在端口 {info.Port} 单独运行。";
            }

            // The shared Windows service owns the same product ports. An explicit
            // single-application start switches the server into manual product mode;
            // the shared service will return automatically on the next Windows boot.
            if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatHome))
            {
                TomcatWindowsServiceManager.Stop();
            }
            await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));
            await StopInstanceAsync(tomcatHome, instanceRoot, info.Port, cancellationToken, throwOnFailure: true);
            if (IsTcpPortListening(info.Port))
            {
                throw new InvalidOperationException($"端口 {info.Port} 已被其他程序占用，无法单独启动 {productId}。");
            }

            if (catalinaMode)
            {
                StartCatalinaConsole(tomcatHome, instanceRoot, productId);
                return $"已打开 {productId} 的 Catalina 诊断窗口，仅加载该应用，端口 {info.Port}。";
            }

            await StartTomcatAsync(tomcatHome, instanceRoot, cancellationToken);
            try
            {
                await WaitForPortAsync(info.Port, listening: true, ProductStartupTimeout, cancellationToken);
                await TomcatRuntimeProbe.EnsureStableAsync(
                    new[] { info.Port },
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
                SaveInstanceProcessId(instanceRoot, info.Port);
                WriteOperationLog(productId, $"单应用启动成功，端口 {info.Port}，PID {ReadInstanceProcessId(instanceRoot)?.ToString() ?? "未识别"}。");
            }
            catch (OperationCanceledException)
            {
                await StopInstanceAsync(
                    tomcatHome,
                    instanceRoot,
                    info.Port,
                    CancellationToken.None,
                    throwOnFailure: false);
                throw;
            }
            catch (Exception ex)
            {
                var log = ReadRecentTomcatLog(instanceRoot);
                await StopInstanceAsync(
                    tomcatHome,
                    instanceRoot,
                    info.Port,
                    CancellationToken.None,
                    throwOnFailure: false);
                throw new TimeoutException(string.IsNullOrWhiteSpace(log)
                    ? ex.Message
                    : $"{ex.Message}{Environment.NewLine}{Environment.NewLine}最近的 Tomcat 日志：{Environment.NewLine}{log}", ex);
            }

            return $"{productId} 已单独启动，访问地址：{info.Url}";
        }
        finally
        {
            RuntimeLock.Release();
        }
    }

    public async Task<string> StopAsync(string productId, CancellationToken cancellationToken = default)
    {
        var info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId)
            ?? throw new InvalidOperationException($"未找到 {productId} 的 Tomcat 部署信息。");
        var tomcatHome = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");
        var instanceRoot = GetInstanceRoot(productId);

        if (!await RuntimeLock.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            throw new TimeoutException("Tomcat 正在执行其他启动或停止操作，请稍后重试。");
        }
        try
        {
            var recordedProcessId = ReadInstanceProcessId(instanceRoot);
            var instanceProcessIds = GetJavaProcessIdsForBase(instanceRoot).ToArray();
            var sharedTomcatRunning = IsSharedTomcatRunning();
            WriteOperationLog(productId,
                $"收到停止请求，记录 PID={recordedProcessId?.ToString() ?? "无"}，匹配 PID={string.Join(",", instanceProcessIds)}，端口={info.Port}。");

            if (recordedProcessId.HasValue ||
                instanceProcessIds.Length > 0 ||
                IsShutdownPortListening(instanceRoot) ||
                (!sharedTomcatRunning && IsJavaPortListening(info.Port)))
            {
                await StopInstanceAsync(tomcatHome, instanceRoot, info.Port, cancellationToken, throwOnFailure: true);
                ForceStopRecordedProcess(instanceRoot, recordedProcessId, info.Port);
                await WaitForPortAsync(info.Port, listening: false, TimeSpan.FromSeconds(8), cancellationToken);
                ClearInstanceProcessId(instanceRoot);
                WriteOperationLog(productId, "单应用停止成功。");
                return $"{productId} 已停止，其他单独运行的应用不受影响。";
            }

            if (!IsTcpPortListening(info.Port))
            {
                ClearInstanceProcessId(instanceRoot);
                return $"{productId} 当前未运行。";
            }

            throw new InvalidOperationException(
                $"{productId} 当前由环境管理中的 Tomcat Server 全部启动模式运行。" +
                "请在环境管理中停止 Tomcat Server；单独启动后即可独立停止此应用。");
        }
        finally
        {
            RuntimeLock.Release();
        }
    }

    public async Task PrepareAsync(TomcatProductDeploymentInfo info, CancellationToken cancellationToken = default)
    {
        var tomcatHome = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");
        await ProductDeploymentService.EnsureTomcatProductServiceAsync(info, cancellationToken);
        info = ProductDeploymentService.LoadTomcatDeploymentInfo(info.ProductId) ?? info;
        await PrepareInstanceAsync(tomcatHome, info, cancellationToken);
    }

    public Task RemoveAsync(string productId, CancellationToken cancellationToken = default)
    {
        var info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId);
        return RemoveAsync(productId, info?.Port, cancellationToken);
    }

    public async Task RemoveAsync(
        string productId,
        int? expectedHttpPort,
        CancellationToken cancellationToken = default)
    {
        var tomcatHome = FindTomcatRoot();
        var instanceRoot = GetInstanceRoot(productId);
        if (tomcatHome is not null && Directory.Exists(instanceRoot))
        {
            await StopInstanceAsync(tomcatHome, instanceRoot, expectedHttpPort, cancellationToken, throwOnFailure: true);
        }

        DeleteDirectory(instanceRoot);
        DeleteDirectory(GetLegacyInstanceRoot(productId));
    }

    public static async Task StopAllProductInstancesAsync(CancellationToken cancellationToken = default)
    {
        var tomcatHome = FindTomcatRoot();
        if (tomcatHome is null || !Directory.Exists(InstancesRoot))
        {
            return;
        }

        await RuntimeLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var instanceRoot in Directory.EnumerateDirectories(InstancesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                await StopInstanceAsync(
                    tomcatHome,
                    instanceRoot,
                    GetTomcatHttpPortForInstance(instanceRoot),
                    cancellationToken,
                    throwOnFailure: true);
            }
        }
        finally
        {
            RuntimeLock.Release();
        }
    }

    public static async Task StopAllTomcatProcessesAsync(
        CancellationToken cancellationToken = default,
        bool throwOnFailure = true)
    {
        var tomcatHome = FindTomcatRoot();
        if (tomcatHome is null)
        {
            return;
        }

        await RuntimeLock.WaitAsync(cancellationToken);
        try
        {
            var tomcatPorts = GetTomcatHttpPorts(tomcatHome).ToArray();
            try
            {
                await StopCatalinaBaseAsync(tomcatHome, cancellationToken, tomcatPorts);
            }
            catch (Exception error) when (!throwOnFailure)
            {
                WriteOperationLog("shared", "停止 Tomcat 全部应用模式失败，保留进程和目录：" + error.Message);
            }
            if (Directory.Exists(InstancesRoot))
            {
                foreach (var instanceRoot in Directory.EnumerateDirectories(InstancesRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        await StopInstanceAsync(
                            tomcatHome,
                            instanceRoot,
                            GetTomcatHttpPortForInstance(instanceRoot),
                            cancellationToken,
                            throwOnFailure: true);
                    }
                    catch (Exception error) when (!throwOnFailure)
                    {
                        WriteOperationLog(Path.GetFileName(instanceRoot), "停止 Tomcat 实例失败，保留目录以便稍后重试：" + error.Message);
                    }
                }
            }

            if (IsJavaProcessForBase(tomcatHome))
            {
                try
                {
                    await StopJavaProcessesForBaseAsync(tomcatHome, cancellationToken);
                }
                catch (Exception error) when (!throwOnFailure)
                {
                    WriteOperationLog("shared", "终止 Tomcat Java 进程失败：" + error.Message);
                }
            }

            try
            {
                await StopJavaProcessesForPortsAsync(tomcatPorts, cancellationToken);
            }
            catch (Exception error) when (!throwOnFailure)
            {
                WriteOperationLog("shared", "按端口终止 Tomcat Java 进程失败：" + error.Message);
            }
            var remainingPorts = tomcatPorts.Where(IsTcpPortListening).Distinct().ToArray();
            if (remainingPorts.Length > 0)
            {
                var message =
                    $"Tomcat 仍有端口在监听：{string.Join(", ", remainingPorts)}。已取消后续删除操作，请先确认 Java 进程已停止。";
                if (throwOnFailure)
                {
                    throw new InvalidOperationException(message);
                }

                WriteOperationLog("shared", message);
            }
        }
        finally
        {
            RuntimeLock.Release();
        }
    }

    public static bool IsSharedTomcatRunning()
    {
        var tomcatHome = FindTomcatRoot();
        if (tomcatHome is null)
        {
            return false;
        }

        if (IsJavaProcessForBase(tomcatHome))
        {
            return true;
        }

        var sharedPorts = TomcatRuntimeProbe.ReadHttpPorts(tomcatHome).ToArray();
        return sharedPorts.Length > 0 && sharedPorts.All(IsJavaPortListening);
    }

    public static bool IsAnyManagedTomcatRunning()
    {
        var tomcatHome = FindTomcatRoot();
        return tomcatHome is not null &&
               (EnumerateJavaCommandLines().Any(commandLine =>
                    ContainsJavaOptionPath(commandLine, "-Dcatalina.home", tomcatHome)) ||
                GetTomcatHttpPorts(tomcatHome).Any(IsJavaPortListening));
    }

    public static bool IsAnyManagedTomcatHealthy()
    {
        var javaProcesses = EnumerateJavaProcesses().ToArray();
        foreach (var instanceRoot in EnumerateManagedInstanceRoots())
        {
            var ports = TomcatRuntimeProbe.ReadHttpPorts(instanceRoot).ToArray();
            if (ports.Length == 0 && GetTomcatHttpPortForInstance(instanceRoot) is int fallbackPort)
            {
                ports = new[] { fallbackPort };
            }

            if (ports.Length == 0)
            {
                continue;
            }

            var commandLineHealthy = javaProcesses.Any(process =>
                ContainsJavaOptionPath(process.CommandLine, "-Dcatalina.base", instanceRoot)) &&
                ports.Any(IsTcpPortListening);
            if (commandLineHealthy || ports.Any(IsJavaPortListening))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<int> GetTomcatHttpPorts(string tomcatHome)
    {
        var ports = new HashSet<int>(TomcatRuntimeProbe.ReadHttpPorts(tomcatHome));
        foreach (var deployment in ProductDeploymentService.LoadTomcatDeploymentInfos())
        {
            if (deployment.Port is > 0 and <= 65535)
            {
                ports.Add(deployment.Port);
            }
        }

        return ports.OrderBy(port => port).ToArray();
    }

    private static int? GetTomcatHttpPortForInstance(string instanceRoot)
    {
        var instanceName = Path.GetFileName(instanceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var port = ProductDeploymentService.LoadTomcatDeploymentInfos()
            .FirstOrDefault(info => SafeName(info.ProductId).Equals(instanceName, StringComparison.OrdinalIgnoreCase))?
            .Port;
        return port is > 0 and <= 65535 ? port : null;
    }

    private static bool IsJavaPortListening(int port)
    {
        if (!IsTcpPortListening(port))
        {
            return false;
        }

        var processIds = GetListeningProcessIds(new[] { port });
        return processIds.Count == 0 || processIds.Any(IsJavaProcessId);
    }

    private static async Task<string> PrepareInstanceAsync(
        string tomcatHome,
        TomcatProductDeploymentInfo info,
        CancellationToken cancellationToken)
    {
        if (!ProductDeploymentService.IsValidTomcatProductPort(info.Port))
        {
            throw new InvalidDataException(
                $"{info.ProductId} 的 Tomcat 产品端口 {info.Port} 无效；自动修复部署状态后仍未获得固定端口。");
        }

        var instanceRoot = GetInstanceRoot(info.ProductId);
        var sourceConf = Path.Combine(tomcatHome, "conf");
        var targetConf = Path.Combine(instanceRoot, "conf");
        Directory.CreateDirectory(targetConf);
        CopyDirectory(sourceConf, targetConf, skipFileName: "server.xml");
        foreach (var directory in new[] { "logs", "temp", "work", "webapps" })
        {
            Directory.CreateDirectory(Path.Combine(instanceRoot, directory));
        }

        var mainServerXml = Path.Combine(sourceConf, "server.xml");
        var document = XDocument.Load(mainServerXml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Tomcat server.xml 缺少 Server 根节点。");
        var targetService = root.Elements("Service").FirstOrDefault(service =>
            service.Elements("Connector").Any(connector =>
                int.TryParse(connector.Attribute("port")?.Value, out var port) && port == info.Port) &&
            service.Descendants("Context").Any(context =>
                string.Equals(Path.GetFullPath(context.Attribute("docBase")?.Value ?? string.Empty),
                    Path.GetFullPath(info.PhysicalPath), StringComparison.OrdinalIgnoreCase)));
        if (targetService is null)
        {
            throw new InvalidDataException($"主 server.xml 中未找到 {info.ProductId} 对应的 Service。");
        }

        foreach (var service in root.Elements("Service").Where(service => !ReferenceEquals(service, targetService)).ToList())
        {
            service.Remove();
        }

        root.SetAttributeValue("port", SelectShutdownPort(instanceRoot, info.Port));
        root.SetAttributeValue("shutdown", "SHUTDOWN");
        var targetServerXml = Path.Combine(targetConf, "server.xml");
        await SaveXmlAsync(targetServerXml, document, cancellationToken);
        return instanceRoot;
    }

    private static int SelectShutdownPort(string instanceRoot, int productPort)
    {
        var existing = Path.Combine(instanceRoot, "conf", "server.xml");
        if (File.Exists(existing))
        {
            try
            {
                var value = XDocument.Load(existing).Root?.Attribute("port")?.Value;
                if (int.TryParse(value, out var savedPort) && savedPort > 0 && savedPort != productPort)
                {
                    return savedPort;
                }
            }
            catch
            {
                // Recreate a damaged generated configuration below.
            }
        }

        var candidate = 10000 + (productPort % 40000);
        var active = GetActiveTcpPorts();
        while (candidate == productPort || active.Contains(candidate))
        {
            candidate++;
            if (candidate > 60000)
            {
                candidate = 10000;
            }
        }

        return candidate;
    }

    private static async Task StartTomcatAsync(string tomcatHome, string catalinaBase, CancellationToken cancellationToken)
    {
        var consoleLog = Path.Combine(catalinaBase, "logs", "console.log");
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
            catalinaBase,
            useInstanceLocalErrorFile: true);
        Directory.CreateDirectory(Path.GetDirectoryName(consoleLog)!);
        var startInfo = BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: true);
        var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法执行 Tomcat 单应用启动命令。");
        var captureTask = CaptureTomcatOutputAsync(launcher, consoleLog);

        // Starting Java directly is intentional. Some supplier catalina.bat
        // variants reset CATALINA_BASE to CATALINA_HOME while recursively
        // invoking themselves. That silently loads the shared server.xml and
        // defeats per-product isolation. Explicit -D properties cannot be
        // overwritten by those scripts and keep every product on its own base.
        try
        {
            await Task.Delay(300, cancellationToken);
            if (launcher.HasExited)
            {
                var exitCode = launcher.ExitCode;
                await captureTask;
                var log = ReadRecentTomcatLog(catalinaBase);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
                    ? $"Tomcat 启动进程提前退出，退出码：{exitCode}。"
                    : $"Tomcat 启动进程提前退出，退出码：{exitCode}。最近日志：{Environment.NewLine}{log}");
            }
        }
        finally
        {
            _ = DisposeProcessAfterCaptureAsync(launcher, captureTask);
        }
    }

    internal static ProcessStartInfo BuildTomcatJavaStartInfo(
        string tomcatHome,
        string catalinaBase,
        bool redirectOutput)
    {
        var normalizedHome = Path.GetFullPath(tomcatHome);
        var normalizedBase = Path.GetFullPath(catalinaBase);
        var java = ResolveTomcatJavaExecutable(normalizedHome);
        var bootstrap = Path.Combine(normalizedHome, "bin", "bootstrap.jar");
        var tomcatJuli = Path.Combine(normalizedHome, "bin", "tomcat-juli.jar");
        if (!File.Exists(bootstrap) || !File.Exists(tomcatJuli))
        {
            throw new FileNotFoundException("Tomcat 启动类库不完整，请重新安装 Tomcat。", !File.Exists(bootstrap) ? bootstrap : tomcatJuli);
        }

        var arguments = BuildTomcatJavaArguments(
            normalizedHome,
            normalizedBase,
            ReadTomcatJvmOptions(normalizedBase));
        var startInfo = new ProcessStartInfo
        {
            FileName = java,
            Arguments = arguments,
            WorkingDirectory = Path.Combine(normalizedHome, "bin"),
            UseShellExecute = !redirectOutput,
            CreateNoWindow = redirectOutput,
            WindowStyle = redirectOutput ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        if (redirectOutput)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);
        }

        return startInfo;
    }

    internal static string BuildTomcatJavaArguments(
        string tomcatHome,
        string catalinaBase,
        string? jvmOptions)
    {
        var classPath = string.Join(
            Path.PathSeparator.ToString(),
            Path.Combine(tomcatHome, "bin", "bootstrap.jar"),
            Path.Combine(tomcatHome, "bin", "tomcat-juli.jar"));
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(jvmOptions))
        {
            arguments.Add(jvmOptions!.Trim());
        }

        arguments.Add(Compat.QuoteCommandLineArgument(
            $"-Djava.util.logging.config.file={Path.Combine(catalinaBase, "conf", "logging.properties")}"));
        arguments.Add("-Djava.util.logging.manager=org.apache.juli.ClassLoaderLogManager");
        arguments.Add("-Djdk.tls.ephemeralDHKeySize=2048");
        arguments.Add("-Djava.protocol.handler.pkgs=org.apache.catalina.webresources");
        arguments.Add("-Dignore.endorsed.dirs=");
        arguments.Add("-Dfile.encoding=UTF-8");
        arguments.Add("-classpath");
        arguments.Add(Compat.QuoteCommandLineArgument(classPath));
        arguments.Add(Compat.QuoteCommandLineArgument($"-Dcatalina.base={catalinaBase}"));
        arguments.Add(Compat.QuoteCommandLineArgument($"-Dcatalina.home={tomcatHome}"));
        arguments.Add(Compat.QuoteCommandLineArgument(
            $"-Djava.io.tmpdir={Path.Combine(catalinaBase, "temp")}"));
        arguments.Add("org.apache.catalina.startup.Bootstrap");
        arguments.Add("start");
        return string.Join(" ", arguments);
    }

    private static string ResolveTomcatJavaExecutable(string tomcatHome)
    {
        var candidates = new List<string>
        {
            Path.Combine(tomcatHome, "jre", "jre", "bin", "java.exe"),
            Path.Combine(tomcatHome, "jre", "bin", "java.exe")
        };
        foreach (var variable in new[] { "JRE_HOME", "JAVA_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(Path.Combine(value!, "bin", "java.exe"));
            }
        }

        var java = candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
        return java ?? throw new FileNotFoundException(
            "未找到 Tomcat 所需的 Java 运行时，请重新安装 Tomcat 环境。",
            candidates[0]);
    }

    private static string? ReadTomcatJvmOptions(string catalinaBase)
    {
        var path = Path.Combine(catalinaBase, "conf", "jvm.properties");
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadLines(path, new UTF8Encoding(false))
            .Select(line => line.TrimStart('\uFEFF').Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));
    }

    private static async Task CaptureTomcatOutputAsync(Process process, string consoleLog)
    {
        try
        {
            using var stream = new FileStream(
                consoleLog,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var gate = new object();
            lock (gate)
            {
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MCPanel 启动独立 Tomcat 实例。");
            }

            await Task.WhenAll(
                PumpTomcatReaderAsync(process.StandardOutput, writer, gate),
                PumpTomcatReaderAsync(process.StandardError, writer, gate));
        }
        catch (Exception ex)
        {
            WriteOperationLog(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(consoleLog))!),
                "记录 Tomcat 控制台输出失败：" + ex.Message);
        }
    }

    private static async Task PumpTomcatReaderAsync(StreamReader reader, StreamWriter writer, object gate)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (gate)
            {
                writer.WriteLine(line);
            }
        }
    }

    private static async Task DisposeProcessAfterCaptureAsync(Process process, Task captureTask)
    {
        try
        {
            await captureTask;
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task StopInstanceAsync(
        string tomcatHome,
        string instanceRoot,
        int? expectedHttpPort,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        if (!Directory.Exists(instanceRoot))
        {
            return;
        }

        _ = tomcatHome;
        var fallbackPorts = expectedHttpPort is > 0
            ? new[] { expectedHttpPort.Value }
            : Array.Empty<int>();
        try
        {
            await StopCatalinaBaseAsync(instanceRoot, cancellationToken, fallbackPorts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (!throwOnFailure)
        {
            WriteOperationLog(Path.GetFileName(instanceRoot), "停止单应用实例失败，保留目录以便稍后重试：" + error.Message);
        }

        if (IsJavaProcessForBase(instanceRoot) || fallbackPorts.Any(IsTcpPortListening))
        {
            var portText = fallbackPorts.Length == 0 ? string.Empty : $"端口：{string.Join(", ", fallbackPorts)}。";
            var message = $"Tomcat 单应用实例仍在运行，{portText}已保留实例目录，请先确认 Java 进程停止后重试。";
            if (throwOnFailure)
            {
                throw new InvalidOperationException(message);
            }

            WriteOperationLog(Path.GetFileName(instanceRoot), message);
        }
    }

    private static async Task StopCatalinaBaseAsync(
        string catalinaBase,
        CancellationToken cancellationToken,
        IEnumerable<int>? fallbackHttpPorts = null)
    {
        var fallbackPorts = NormalizePorts(fallbackHttpPorts);
        if (!IsJavaProcessForBase(catalinaBase) &&
            !IsShutdownPortListening(catalinaBase) &&
            !fallbackPorts.Any(IsTcpPortListening))
        {
            return;
        }

        var shutdownSent = await TrySendShutdownCommandAsync(catalinaBase, cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && IsJavaProcessForBase(catalinaBase))
        {
            await Task.Delay(250, cancellationToken);
        }

        if (IsJavaProcessForBase(catalinaBase))
        {
            await StopJavaProcessesForBaseAsync(catalinaBase, cancellationToken);
        }

        await StopJavaProcessesForPortsAsync(fallbackPorts, cancellationToken);

        var portDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < portDeadline && IsShutdownPortListening(catalinaBase))
        {
            await Task.Delay(150, cancellationToken);
        }

        if (!shutdownSent || IsJavaProcessForBase(catalinaBase))
        {
            await StopJavaProcessesForBaseAsync(catalinaBase, cancellationToken);
        }

        await StopJavaProcessesForPortsAsync(fallbackPorts, cancellationToken);

        if (IsJavaProcessForBase(catalinaBase) ||
            IsShutdownPortListening(catalinaBase) ||
            fallbackPorts.Any(IsTcpPortListening))
        {
            throw new InvalidOperationException("Tomcat 单应用进程未能停止，已保留实例目录。请先确认 Java 进程可终止后重试。");
        }
    }

    private static async Task<bool> TrySendShutdownCommandAsync(string catalinaBase, CancellationToken cancellationToken)
    {
        if (!TryGetShutdownConfiguration(catalinaBase, out var shutdownPort, out var shutdownCommand) ||
            !IsTcpPortListening(shutdownPort))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", shutdownPort);
            if (await Task.WhenAny(connectTask, Task.Delay(1500, cancellationToken)) != connectTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            await connectTask;
            var bytes = Encoding.ASCII.GetBytes(shutdownCommand);
            using var stream = client.GetStream();
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsShutdownPortListening(string catalinaBase) =>
        TryGetShutdownConfiguration(catalinaBase, out var port, out _) && IsTcpPortListening(port);

    private static bool TryGetShutdownConfiguration(string catalinaBase, out int port, out string command)
    {
        port = -1;
        command = "SHUTDOWN";
        try
        {
            var serverXml = Path.Combine(catalinaBase, "conf", "server.xml");
            if (!File.Exists(serverXml))
            {
                return false;
            }

            var root = XDocument.Load(serverXml).Root;
            command = root?.Attribute("shutdown")?.Value ?? "SHUTDOWN";
            return int.TryParse(root?.Attribute("port")?.Value, out port) && port > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void StartCatalinaConsole(string tomcatHome, string instanceRoot, string productId)
    {
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
            instanceRoot,
            useInstanceLocalErrorFile: true);
        var startInfo = BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法打开 {SafeName(productId)} 的 Tomcat Catalina 诊断窗口。");
    }

    private static async Task<bool> IsJavaProcessForBaseAsync(string catalinaBase, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return IsJavaProcessForBase(catalinaBase);
    }

    private static bool IsJavaProcessForBase(string catalinaBase) =>
        EnumerateJavaCommandLines().Any(commandLine =>
            ContainsJavaOptionPath(commandLine, "-Dcatalina.base", catalinaBase));

    private static IEnumerable<string> EnumerateJavaCommandLines()
        => EnumerateJavaProcesses().Select(process => process.CommandLine);

    private static IEnumerable<int> GetJavaProcessIdsForBase(string catalinaBase) =>
        EnumerateJavaProcesses()
            .Where(process => ContainsJavaOptionPath(process.CommandLine, "-Dcatalina.base", catalinaBase))
            .Select(process => process.ProcessId);

    private static IEnumerable<JavaProcessInfo> EnumerateJavaProcesses()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'");
            using var results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(item => new
                {
                    ProcessId = Convert.ToInt32(item["ProcessId"]),
                    CommandLine = item["CommandLine"]?.ToString() ?? string.Empty
                })
                // Keep Java processes whose command line is hidden by WMI.  The
                // port/PID fallback below can still identify and stop them.
                .Select(item => new JavaProcessInfo(item.ProcessId, item.CommandLine))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool ContainsJavaOptionPath(string commandLine, string option, string path)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd('\\');
        return commandLine.Contains($"{option}=\"{normalizedPath}\"", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains($"{option}={normalizedPath} ", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> GetListeningProcessIds(IEnumerable<int> ports)
    {
        var wantedPorts = NormalizePorts(ports);
        if (wantedPorts.Length == 0)
        {
            return [];
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                ProcessLifecycle.TryKill(process);
                return [];
            }

            return ParseNetstatListeningProcessIds(output, wantedPorts);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<int> ParseNetstatListeningProcessIds(
        string output,
        IEnumerable<int> ports)
    {
        var wantedPorts = new HashSet<int>(NormalizePorts(ports));
        if (wantedPorts.Count == 0 || string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var processIds = new HashSet<int>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NetstatListeningLineRegex.Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups["port"].Value, out var port) ||
                !wantedPorts.Contains(port) ||
                !int.TryParse(match.Groups["pid"].Value, out var processId) ||
                processId <= 0)
            {
                continue;
            }

            processIds.Add(processId);
        }

        return processIds.OrderBy(processId => processId).ToArray();
    }

    private static int[] NormalizePorts(IEnumerable<int>? ports) =>
        (ports ?? Array.Empty<int>())
        .Where(port => port is > 0 and <= 65535)
        .Distinct()
        .OrderBy(port => port)
        .ToArray();

    private static bool IsJavaProcessId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsJavaProcess(process);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJavaProcess(Process process)
    {
        try
        {
            return string.Equals(process.ProcessName, "java", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(process.ProcessName, "javaw", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Task StopJavaProcessesForBaseAsync(string catalinaBase, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var processInfo in EnumerateJavaProcesses().Where(processInfo =>
                     ContainsJavaOptionPath(processInfo.CommandLine, "-Dcatalina.base", catalinaBase)))
        {
            try
            {
                using var process = Process.GetProcessById(processInfo.ProcessId);
                process.Kill();
                process.WaitForExit(3000);
            }
            catch
            {
                // The process may have exited between discovery and termination.
            }
        }

        return Task.CompletedTask;
    }

    private static async Task StopJavaProcessesForPortsAsync(
        IEnumerable<int> ports,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var processId in GetListeningProcessIds(ports))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!IsJavaProcess(process) || process.HasExited)
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // The post-stop port check remains authoritative.  If the process
                // could not be inspected or terminated, deletion will be blocked.
            }
        }

        await Task.CompletedTask;
    }

    private static void SaveInstanceProcessId(string instanceRoot, int expectedHttpPort)
    {
        var processId = GetJavaProcessIdsForBase(instanceRoot).FirstOrDefault();
        if (processId <= 0)
        {
            processId = GetListeningProcessIds(new[] { expectedHttpPort })
                .FirstOrDefault(IsJavaProcessId);
        }

        if (processId <= 0)
        {
            return;
        }

        File.WriteAllText(GetInstancePidFile(instanceRoot), processId.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
    }

    private static int? ReadInstanceProcessId(string instanceRoot)
    {
        try
        {
            var file = GetInstancePidFile(instanceRoot);
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var processId) && processId > 0
                ? processId
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ForceStopRecordedProcess(string instanceRoot, int? processId, int expectedHttpPort)
    {
        if (!processId.HasValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited ||
                !string.Equals(process.ProcessName, "java", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(process.ProcessName, "javaw", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var commandLineMatches = EnumerateJavaProcesses().Any(info =>
                info.ProcessId == processId.Value &&
                ContainsJavaOptionPath(info.CommandLine, "-Dcatalina.base", instanceRoot));
            var portMatches = GetListeningProcessIds(new[] { expectedHttpPort }).Contains(processId.Value);
            if (commandLineMatches || portMatches)
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // The process may already have exited after graceful shutdown.
        }
    }

    private static void ClearInstanceProcessId(string instanceRoot)
    {
        try
        {
            var file = GetInstancePidFile(instanceRoot);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // A stale PID file is validated before use and is safe to retain.
        }
    }

    private static string GetInstancePidFile(string instanceRoot) => Path.Combine(instanceRoot, "tomcat.pid");

    public static void WriteOperationLog(string productId, string message)
    {
        try
        {
            var logs = ComponentPaths.LogsRoot;
            RollingLogWriter.Append(
                Path.Combine(logs, "tomcat-operations.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{SafeName(productId)}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never block runtime operations.
        }
    }

    private static async Task WaitForPortAsync(int port, bool listening, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTcpPortListening(port) == listening)
            {
                return;
            }

            await Task.Delay(400, cancellationToken);
        }

        throw new TimeoutException(listening
            ? $"Tomcat 产品端口 {port} 未在规定时间内开始监听。"
            : $"Tomcat 产品端口 {port} 未在规定时间内停止。" );
    }

    private static bool IsTcpPortListening(int port) => GetActiveTcpPorts().Contains(port);

    private static HashSet<int> GetActiveTcpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    private static string ReadRecentTomcatLog(string instanceRoot)
    {
        try
        {
            var logs = Path.Combine(instanceRoot, "logs");
            var file = Directory.EnumerateFiles(logs, "*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null)
            {
                return string.Empty;
            }

            var lines = new Queue<string>();
            foreach (var line in File.ReadLines(file))
            {
                lines.Enqueue(line);
                if (lines.Count > 40)
                {
                    lines.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Task SaveXmlAsync(string path, XDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = document.Declaration is null
        };
        using var writer = System.Xml.XmlWriter.Create(path, settings);
        document.Save(writer);
        return Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string destination, string? skipFileName = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), skipFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string? FindTomcatRoot()
    {
        return new ComponentLocator().FindTomcatRoot(TomcatComponentRequirements.CatalinaScript);
    }

    private static IEnumerable<string> EnumerateManagedInstanceRoots()
    {
        var roots = new[]
        {
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns"),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatProductRuns"),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances"),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances")
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] instances;
            try
            {
                instances = Directory.Exists(root)
                    ? Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();
            }
            catch
            {
                continue;
            }

            foreach (var instance in instances)
            {
                yield return instance;
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Normal);
                }

                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
        }

        // Do not report a successful uninstall while the instance is still on
        // disk. The caller can show the exact path and allow a retry.
        Directory.Delete(path, recursive: true);
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        var safe = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "product" : safe;
    }

    private static string GetInstanceRoot(string productId) =>
        Path.Combine(InstancesRoot, SafeName(productId));

    private static string GetLegacyInstanceRoot(string productId) =>
        Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", SafeName(productId));

    private static string InstancesRoot =>
        Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns");

}
