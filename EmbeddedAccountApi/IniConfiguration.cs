#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MarchCenter.AccountApi
{
    public static class IniConfiguration
    {
        public static AccountApiOptions LoadOptions(string path)
        {
            var values = Load(path);
            var options = new AccountApiOptions();
            options.Server.BindAddress = Text(values, "AccountApi:Server:BindAddress", "127.0.0.1");
            options.Server.Port = Number(values, "AccountApi:Server:Port", 8088);
            options.Server.RequestBodyLimitBytes = Number(values, "AccountApi:Server:RequestBodyLimitBytes", 65536);
            options.Server.RequestsPerMinute = Number(values, "AccountApi:Server:RequestsPerMinute", 120);
            options.Authentication.Mode = Text(values, "AccountApi:Authentication:Mode", "Hmac");
            options.Authentication.SigningSecret = Text(values, "AccountApi:Authentication:SigningSecret", "");
            options.Authentication.ClockSkewSeconds = Number(values, "AccountApi:Authentication:ClockSkewSeconds", 300);
            options.AuditLogDirectory = Text(values, "AccountApi:AuditLogDirectory", "logs");
            // 数据库连接只能由网页后台加密下发，并由 RemoteConfigurationStore 从
            // Windows 机器级 DPAPI 文件 database.config 加载。本地 INI 不再接受
            // 任何数据库地址、账号或密码，初始状态只保留两个禁用的类型占位。
            options.Systems["sqlserver"] = EmptyRemoteProfile("SQL Server", "SqlServer", "sqlserver");
            options.Systems["mysql"] = EmptyRemoteProfile("MySQL", "MySql", "java");
            Validate(options);
            return options;
        }

        public static Dictionary<string, string> Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("未找到 config.ini", path);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = "";
            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (section.Length == 0) throw new InvalidDataException("config.ini 第 " + lineNumber + " 行的分组名称为空");
                    continue;
                }
                var separator = line.IndexOf('=');
                if (separator <= 0) throw new InvalidDataException("config.ini 第 " + lineNumber + " 行格式错误，应写成 配置名=配置值");
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                var fullKey = section.Length == 0 ? key : section + ":" + key;
                if (result.ContainsKey(fullKey)) throw new InvalidDataException("config.ini 第 " + lineNumber + " 行存在重复配置：" + fullKey);
                result[fullKey] = value;
            }
            return result;
        }

        private static SystemProfile EmptyRemoteProfile(string name, string provider, string adapter)
        {
            return new SystemProfile
            {
                Enabled = false,
                DisplayName = name,
                AdapterType = adapter,
                Provider = provider,
                ConnectionString = "",
                PasswordAlgorithm = "plain",
                ResetPassword = "123",
                CommandTimeoutSeconds = 10
            };
        }

        private static void Validate(AccountApiOptions options)
        {
            if (options.Server.Port < 1024 || options.Server.Port > 65535) throw new InvalidDataException("Port 必须在 1024 到 65535 之间");
            if (string.Equals(options.Authentication.Mode, "hmac", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(options.Authentication.SigningSecret) || options.Authentication.SigningSecret.Length < 32 || options.Authentication.SigningSecret.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("HMAC 签名密钥尚未生成或长度不足。请打开 MarchCenter 账号管理器，点击“生成 HMAC 签名密钥”。");
            if (string.Equals(options.Authentication.Mode, "none", StringComparison.OrdinalIgnoreCase) && options.Server.BindAddress != "127.0.0.1" && options.Server.BindAddress != "::1" && !string.Equals(options.Server.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("无认证模式只能监听 127.0.0.1");
        }

        private static string Text(Dictionary<string, string> values, string key, string fallback) { string value; return values.TryGetValue(key, out value) ? value : fallback; }
        private static int Number(Dictionary<string, string> values, string key, int fallback) { string value; int number; return values.TryGetValue(key, out value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : fallback; }
    }
}
