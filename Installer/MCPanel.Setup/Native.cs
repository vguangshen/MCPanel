using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MCPanel.Setup
{
    internal static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ReplaceFileW(string replaced, string replacement, string backup, uint flags, IntPtr exclude, IntPtr reserved);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        public static Encoding ConsoleEncoding
        {
            get
            {
                try { return Encoding.GetEncoding((int)GetOEMCP()); }
                catch { return Encoding.UTF8; }
            }
        }

        /// <summary>Atomically replaces <paramref name="destination"/> with <paramref name="source"/>.</summary>
        public static void ReplaceFile(string destination, string source)
        {
            if (!ReplaceFileW(destination, source, null, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new IOException("无法原子替换文件：" + destination + "（" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message + "）");
            }
        }

        /// <summary>
        /// Opens a file exclusively to prove nothing else holds it. Fails for a
        /// DLL that another process has loaded or opened.
        /// </summary>
        public static void AssertFileAvailable(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            using (var handle = CreateFileW(path, 0x80000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException("文件正在被占用或无法访问，原程序未修改：" + path);
                }
            }
        }

        /// <summary>Takes the in-app updater's transaction lock (delete on close, exclusive).</summary>
        public static FileStream AcquireExclusiveLock(string path)
        {
            var handle = CreateFileW(path, 0xC0010000, 0, IntPtr.Zero, 4, 0x04000080, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("该目录正在被其他更新任务使用。请等待更新完成后重试安装。");
            }

            return new FileStream(handle, FileAccess.ReadWrite);
        }

        public static bool MutexExists(string name)
        {
            var handle = OpenMutexW(0x00100000, false, name);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            CloseHandle(handle);
            return true;
        }

        public static ulong? AvailableBytes(string directory)
        {
            var probe = directory;
            while (!string.IsNullOrEmpty(probe) && !System.IO.Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
            }

            if (string.IsNullOrEmpty(probe))
            {
                return null;
            }

            ulong available, total, free;
            return GetDiskFreeSpaceExW(probe, out available, out total, out free) ? available : (ulong?)null;
        }

        public static bool IsReparsePoint(string path) =>
            (File.Exists(path) || System.IO.Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        /// <summary>Creates a Unicode-safe .lnk through IShellLinkW.</summary>
        public static void CreateShortcut(string shortcut, string target, string workingDirectory, string description)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(shortcut));
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workingDirectory);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(shortcut, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
