#nullable disable

using System;
using System.IO;

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

    }
}
