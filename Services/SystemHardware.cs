using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MCPanel;

public sealed class CpuSampler
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;

    public double NextValue()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = idle.ToInt64();
        var kernelTicks = kernel.ToInt64();
        var userTicks = user.ToInt64();

        var total = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
        var idleDelta = idleTicks - _lastIdle;

        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;

        if (total <= 0)
        {
            return 0;
        }

        return (total - idleDelta) * 100d / total;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;
        public long ToInt64() => ((long)_high << 32) + _low;
    }
}

public static class SystemHardware
{
    public static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return Normalize(name);
            }
        }
        catch
        {
            // Ignore registry failures and fall back to a generic label.
        }

        return Environment.Is64BitProcess ? "X64" : "X86";
    }

    public static string GetOsText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                var productName = ReadString(key, "ProductName");
                var editionId = ReadString(key, "EditionID");
                var displayVersion = ReadString(key, "DisplayVersion");
                var releaseId = ReadString(key, "ReleaseId");
                var build = ReadInt(key, "CurrentBuildNumber");
                var ubr = ReadInt(key, "UBR");

                var name = NormalizeWindowsName(productName, editionId, build);
                var version = !string.IsNullOrWhiteSpace(displayVersion) ? displayVersion : releaseId;
                var buildText = build > 0
                    ? ubr >= 0 ? $"{build}.{ubr}" : build.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

                return string.Join(" ",
                    new[] { name, version, string.IsNullOrWhiteSpace(buildText) ? string.Empty : $"({buildText})" }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
            }
        }
        catch
        {
            // Fall back below. Some locked-down systems block registry reads.
        }

        return Environment.OSVersion.VersionString;
    }

    private static string Normalize(string value)
    {
        var text = string.Join(" ", value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return text
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string NormalizeWindowsName(string productName, string editionId, int build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? string.Empty : productName.Trim();
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name.Substring("Windows 10".Length);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (build >= 22000)
        {
            return string.IsNullOrWhiteSpace(editionId) ? "Windows 11" : $"Windows 11 {editionId}";
        }

        if (build >= 10240)
        {
            return string.IsNullOrWhiteSpace(editionId) ? "Windows 10" : $"Windows 10 {editionId}";
        }

        return "Windows";
    }

    private static string ReadString(RegistryKey key, string name) =>
        key.GetValue(name)?.ToString() ?? string.Empty;

    private static int ReadInt(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int number => number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => -1
        };
    }
}

public static class SystemMemory
{
    public static double GetMemoryUsagePercent()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status) || status.TotalPhys == 0)
        {
            return 0;
        }

        return (status.TotalPhys - status.AvailPhys) * 100d / status.TotalPhys;
    }

    public static string GetTotalMemoryText()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status) || status.TotalPhys == 0)
        {
            return "未知";
        }

        return $"{status.TotalPhys / 1024d / 1024d / 1024d:N1} GB";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
