using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MCPanel;

internal static class DatabaseToolUninstaller
{
    private static readonly int[] SuccessfulExitCodes = [0, 1641, 3010];

    public static async Task<string> UninstallAsync(
        DatabaseToolInstallation installation,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        if (installation is null)
        {
            throw new ArgumentNullException(nameof(installation));
        }
        Directory.CreateDirectory(workDirectory);

        if (installation.Kind == DatabaseToolKind.Navicat)
        {
            var innoUninstaller = FindNavicatUninstaller(installation.ExecutablePath);
            if (innoUninstaller is not null)
            {
                var logPath = Path.Combine(workDirectory, $"uninstall-navicat-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                var arguments = string.Join(" ",
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    $"/LOG={Compat.QuoteCommandLineArgument(logPath)}");
                await RunAsync(
                    innoUninstaller,
                    arguments,
                    hidden: true,
                    acceptedExitCodes: SuccessfulExitCodes,
                    $"Navicat 卸载失败。请查看日志：{logPath}",
                    cancellationToken);
                return $"Navicat 已卸载。卸载日志：{logPath}";
            }
        }

        if (installation.Kind == DatabaseToolKind.SqlServerManagementStudio &&
            TryGetModernSsmsRoot(installation.ExecutablePath, installation.Version, out var ssmsRoot))
        {
            var visualStudioInstaller = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio",
                "Installer",
                "setup.exe");
            if (File.Exists(visualStudioInstaller))
            {
                var metadata = FindModernSsmsMetadata(ssmsRoot, installation.Version);
                var arguments = BuildModernSsmsUninstallArguments(metadata.ProductId, metadata.ChannelId);
                await RunAsync(
                    visualStudioInstaller,
                    arguments,
                    hidden: true,
                    acceptedExitCodes: SuccessfulExitCodes,
                    $"SQL Server Management Studio 卸载失败。请查看 %TEMP% 中最新的 dd_setup*.log。卸载通道：{metadata.ChannelId}",
                    cancellationToken);
                return $"SQL Server Management Studio 已卸载（{metadata.ChannelId}）；安装器日志位于 %TEMP% 的 dd_setup*.log。";
            }
        }

        var registered = FindRegisteredUninstaller(installation);
        if (registered is null)
        {
            throw new InvalidOperationException(
                $"未找到可安全调用的 {installation.DisplayName} 卸载程序。" +
                "请在 Windows“已安装的应用”中卸载，或重新配置到正式安装版本；面板不会直接删除未知目录。");
        }

        var command = ParseRegisteredCommand(registered.CommandLine);
        if (command is null || !IsSafeRegisteredCommand(command.FileName, registered, installation))
        {
            throw new InvalidOperationException(
                $"{installation.DisplayName} 的系统卸载命令无效或不受信任，已停止执行。" +
                "请在 Windows“已安装的应用”中卸载。");
        }

        var argumentsToRun = command.Arguments;
        string? logPathForMessage = null;
        if (IsWindowsInstaller(command.FileName))
        {
            argumentsToRun = ConvertMsiInstallToUninstall(argumentsToRun);
            if (!Regex.IsMatch(argumentsToRun, @"(^|\s)/l", RegexOptions.IgnoreCase))
            {
                logPathForMessage = Path.Combine(
                    workDirectory,
                    $"uninstall-{installation.Kind.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                argumentsToRun += $" /L*v {Compat.QuoteCommandLineArgument(logPathForMessage)}";
            }
        }

        var failureMessage = logPathForMessage is null
            ? $"{installation.DisplayName} 卸载失败。请查看 Windows 安装日志。"
            : $"{installation.DisplayName} 卸载失败。请查看日志：{logPathForMessage}";
        await RunAsync(
            command.FileName,
            argumentsToRun,
            hidden: false,
            acceptedExitCodes: SuccessfulExitCodes,
            failureMessage,
            cancellationToken);

        return logPathForMessage is null
            ? $"{installation.DisplayName} 已完成卸载。"
            : $"{installation.DisplayName} 已完成卸载。卸载日志：{logPathForMessage}";
    }

    internal static RegisteredUninstallCommand? ParseRegisteredCommand(string commandLine)
    {
        var value = Environment.ExpandEnvironmentVariables(commandLine ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        string executable;
        string arguments;
        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                return null;
            }

            executable = value.Substring(1, closingQuote - 1).Trim();
            arguments = value.Substring(closingQuote + 1).Trim();
        }
        else
        {
            var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                executableEnd += 4;
                executable = value.Substring(0, executableEnd).Trim();
                arguments = value.Substring(executableEnd).Trim();
            }
            else
            {
                var separator = value.IndexOf(' ');
                executable = separator < 0 ? value : value.Substring(0, separator);
                arguments = separator < 0 ? string.Empty : value.Substring(separator + 1).Trim();
            }
        }

        return executable.Length == 0 ? null : new RegisteredUninstallCommand(executable, arguments);
    }

    internal static string ConvertMsiInstallToUninstall(string arguments)
    {
        return Regex.Replace(
            arguments,
            @"(^|\s)/i(?=\s*\{)",
            match => match.Groups[1].Value + "/X",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private static string? FindNavicatUninstaller(string executablePath)
    {
        var installDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(installDirectory, "unins*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^unins\d+\.exe$", RegexOptions.IgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string BuildModernSsmsUninstallArguments(string productId, string channelId)
    {
        return string.Join(" ",
            "uninstall",
            "--quiet",
            "--productId",
            Compat.QuoteCommandLineArgument(productId),
            "--channelId",
            Compat.QuoteCommandLineArgument(channelId),
            "--noweb");
    }

    private static bool TryGetModernSsmsRoot(string executablePath, string installedVersion, out string root)
    {
        root = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return false;
        }

        var suffixes = new[]
        {
            Path.Combine("Release", "Common7", "IDE", "Ssms.exe"),
            Path.Combine("Common7", "IDE", "Ssms.exe")
        };
        foreach (var suffix in suffixes)
        {
            if (!fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = fullPath.Substring(0, fullPath.Length - suffix.Length)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directoryName = Path.GetFileName(candidate);
            var managedModernInstall = IsSameOrChildPath(candidate, ComponentPaths.SsmsRoot) &&
                                       TryParseMajorVersion(installedVersion) is >= 21;
            if (managedModernInstall ||
                IsModernSsmsDirectoryName(directoryName))
            {
                root = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsModernSsmsDirectoryName(string directoryName)
    {
        const string prefix = "Microsoft SQL Server Management Studio ";
        if (!directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionText = directoryName.Substring(prefix.Length).Split(' ')[0];
        return int.TryParse(versionText, out var majorVersion) && majorVersion >= 21;
    }

    private static SsmsInstallerMetadata FindModernSsmsMetadata(string ssmsRoot, string installedVersion)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default }.Distinct())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var instances = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\Setup\Instances");
                if (instances is null)
                {
                    continue;
                }

                foreach (var instanceName in instances.GetSubKeyNames())
                {
                    using var instance = instances.OpenSubKey(instanceName);
                    if (instance is null)
                    {
                        continue;
                    }

                    var productId = ReadRegistryString(instance, "ProductId");
                    var channelId = ReadRegistryString(instance, "ChannelId");
                    var installationPath = ReadRegistryString(instance, "InstallationPath");
                    if (productId.IndexOf("Ssms", StringComparison.OrdinalIgnoreCase) < 0 ||
                        (!IsSameOrChildPath(installationPath, ssmsRoot) && !IsSameOrChildPath(ssmsRoot, installationPath)))
                    {
                        continue;
                    }

                    var major = TryParseMajorVersion(ReadRegistryString(instance, "InstallationVersion")) ??
                                TryParseMajorVersion(installedVersion) ??
                                22;
                    return new SsmsInstallerMetadata(
                        string.IsNullOrWhiteSpace(productId) ? "Microsoft.VisualStudio.Product.Ssms" : productId,
                        string.IsNullOrWhiteSpace(channelId) ? $"SSMS.{major}.SSMS.Release" : channelId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Continue with the remaining registry view or the version fallback.
            }
        }

        var parsedMajor = TryParseMajorVersion(installedVersion);
        var fallbackMajor = parsedMajor.HasValue && parsedMajor.Value >= 21
            ? parsedMajor.Value
            : 22;
        return new SsmsInstallerMetadata(
            "Microsoft.VisualStudio.Product.Ssms",
            $"SSMS.{fallbackMajor}.SSMS.Release");
    }

    private static int? TryParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = Regex.Match(version, @"(?<!\d)(\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : null;
    }

    private static RegisteredUninstallEntry? FindRegisteredUninstaller(DatabaseToolInstallation installation)
    {
        var matches = new List<RegisteredUninstallEntry>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallRoot = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallRoot is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                    {
                        using var subKey = uninstallRoot.OpenSubKey(subKeyName);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var displayName = ReadRegistryString(subKey, "DisplayName");
                        var publisher = ReadRegistryString(subKey, "Publisher");
                        if (!IsExpectedRegistryProduct(installation.Kind, displayName, publisher))
                        {
                            continue;
                        }

                        var commandLine = ReadRegistryString(subKey, "UninstallString");
                        if (string.IsNullOrWhiteSpace(commandLine))
                        {
                            commandLine = ReadRegistryString(subKey, "QuietUninstallString");
                        }

                        if (string.IsNullOrWhiteSpace(commandLine))
                        {
                            continue;
                        }

                        var installLocation = ReadRegistryString(subKey, "InstallLocation");
                        var displayIcon = ReadRegistryString(subKey, "DisplayIcon");
                        var score = ScoreRegistryEntry(installation, displayName, installLocation, displayIcon);
                        matches.Add(new RegisteredUninstallEntry(
                            commandLine,
                            displayName,
                            publisher,
                            installLocation,
                            score));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // A protected registry view must not prevent checking the remaining views.
                }
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var associated = matches
            .OrderByDescending(entry => entry.MatchScore)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        return associated.MatchScore > 0 || matches.Count == 1 ? associated : null;
    }

    private static bool IsExpectedRegistryProduct(DatabaseToolKind kind, string displayName, string publisher)
    {
        if (kind == DatabaseToolKind.Navicat)
        {
            return displayName.IndexOf("Navicat", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (publisher.Length == 0 || publisher.IndexOf("PremiumSoft", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        return (displayName.IndexOf("SQL Server Management Studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                displayName.Equals("SSMS", StringComparison.OrdinalIgnoreCase)) &&
               (publisher.Length == 0 || publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static int ScoreRegistryEntry(
        DatabaseToolInstallation installation,
        string displayName,
        string installLocation,
        string displayIcon)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(installLocation) &&
            IsSameOrChildPath(installation.ExecutablePath, installLocation))
        {
            score += 100;
        }

        var iconCommand = ParseRegisteredCommand(displayIcon.TrimEnd(',', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));
        if (iconCommand is not null && PathsEqual(iconCommand.FileName, installation.ExecutablePath))
        {
            score += 80;
        }

        var versionTokens = Regex.Matches(installation.ExecutablePath, @"(?<!\d)(\d{2}|\d{3})(?!\d)")
            .Cast<Match>()
            .Select(match => match.Value)
            .Distinct()
            .ToArray();
        if (versionTokens.Any(token => displayName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            score += 20;
        }

        return score;
    }

    private static bool IsSafeRegisteredCommand(
        string fileName,
        RegisteredUninstallEntry registered,
        DatabaseToolInstallation installation)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName.Trim());
        if (IsWindowsInstaller(expanded))
        {
            return true;
        }

        if (!Path.IsPathRooted(expanded) || !File.Exists(expanded))
        {
            return false;
        }

        var installDirectory = Path.GetDirectoryName(installation.ExecutablePath) ?? string.Empty;
        if (IsSameOrChildPath(expanded, installDirectory))
        {
            return true;
        }

        if (installation.Kind == DatabaseToolKind.Navicat)
        {
            return registered.Publisher.IndexOf("PremiumSoft", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return registered.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWindowsInstaller(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadRegistryString(RegistryKey key, string name) =>
        key.GetValue(name) as string ?? string.Empty;

    private sealed record SsmsInstallerMetadata(string ProductId, string ChannelId);

    private static async Task RunAsync(
        string executable,
        string arguments,
        bool hidden,
        IReadOnlyCollection<int> acceptedExitCodes,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        using var process = ProcessRunner.Start(
            executable,
            arguments,
            Path.GetDirectoryName(executable),
            elevated: true,
            windowStyle: hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal);

        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (!acceptedExitCodes.Contains(process.ExitCode))
        {
            throw new InvalidOperationException($"{failureMessage} 退出码：{process.ExitCode}。");
        }
    }

    internal sealed class RegisteredUninstallCommand
    {
        public RegisteredUninstallCommand(string fileName, string arguments)
        {
            FileName = fileName;
            Arguments = arguments;
        }

        public string FileName { get; }
        public string Arguments { get; }
    }

    private sealed class RegisteredUninstallEntry
    {
        public RegisteredUninstallEntry(
            string commandLine,
            string displayName,
            string publisher,
            string installLocation,
            int matchScore)
        {
            CommandLine = commandLine;
            DisplayName = displayName;
            Publisher = publisher;
            InstallLocation = installLocation;
            MatchScore = matchScore;
        }

        public string CommandLine { get; }
        public string DisplayName { get; }
        public string Publisher { get; }
        public string InstallLocation { get; }
        public int MatchScore { get; }
    }
}
