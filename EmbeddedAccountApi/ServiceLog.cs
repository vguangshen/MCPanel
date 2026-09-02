#nullable disable

using System;
using System.IO;
using System.Text;

namespace MarchCenter.AccountApi
{
    /// <summary>
    /// 服务启动/错误日志：把真实异常（端口占用、配置无效、缺 .NET 等）写入
    /// logs/service.log，供图形管理器在启动失败时读取并展示，替代通用文案。
    /// </summary>
    public static class ServiceLog
    {
        private static readonly object Gate = new object();
        private static string DirectoryPath { get { return AccountApiStorage.LogDirectory; } }
        private static string PathName { get { return Path.Combine(DirectoryPath, "service.log"); } }

        public static void Write(string level, string operation, string detail)
        {
            try
            {
                lock (Gate)
                {
                    MCPanel.RollingLogWriter.Append(PathName,
                        $"[{DateTimeOffset.Now:o}] {level} 操作={operation} 说明={detail}{Environment.NewLine}");
                }
            }
            catch { /* 日志写入失败不再抛异常，避免掩盖原始错误 */ }
        }

        /// <summary>读取最近一条"错误"级记录（供管理器展示真实原因）。</summary>
        public static string LastError()
        {
            try
            {
                if (!File.Exists(PathName)) return "";
                var lines = File.ReadAllLines(PathName, Encoding.UTF8);
                for (var i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].IndexOf("错误", StringComparison.Ordinal) >= 0) return lines[i];
                return "";
            }
            catch { return ""; }
        }
    }
}
