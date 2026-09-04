#nullable disable

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MySqlConnector;

namespace MarchCenter.AccountApi
{
    public sealed class DeviceIdentity
    {
        public string DeviceId { get; set; }
        public string PrivateKeyXml { get; set; }
    }

    public static class DeviceIdentityStore
    {
        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static string PathName { get { return AccountApiStorage.PathFor("device.identity"); } }

        public static DeviceIdentity LoadOrCreate()
        {
            lock (Gate)
            {
                DeviceIdentity identity = null;
                if (File.Exists(PathName))
                {
                    try
                    {
                        var protectedBytes = File.ReadAllBytes(PathName);
                        var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                        identity = Json.Deserialize<DeviceIdentity>(Encoding.UTF8.GetString(bytes));
                    }
                    catch (Exception error)
                    {
                        // device.identity 用机器级 DPAPI 加密，跨机器复制或文件损坏会解密失败；
                        // 视为无效身份，重新生成（旧文件将被覆盖），不再导致启动崩溃。
                        ServiceLog.Write("警告", "load_device_identity", "无法解密 device.identity（可能来自其他机器或已损坏）：" + error.Message);
                        identity = null;
                    }
                }
                if (identity == null || string.IsNullOrWhiteSpace(identity.PrivateKeyXml))
                {
                    using (var rsa = new RSACryptoServiceProvider(4096))
                    {
                        rsa.PersistKeyInCsp = false;
                        identity = new DeviceIdentity
                        {
                            DeviceId = Guid.NewGuid().ToString("N"),
                            PrivateKeyXml = rsa.ToXmlString(true)
                        };
                    }
                    Save(identity);
                }
                return identity;
            }
        }

        public static IDictionary<string, object> PublicJwk(DeviceIdentity identity)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(identity.PrivateKeyXml);
                var value = rsa.ExportParameters(false);
                return new Dictionary<string, object>
                {
                    { "kty", "RSA" }, { "alg", "RSA-OAEP" }, { "ext", true },
                    { "key_ops", new[] { "encrypt" } },
                    { "n", Base64Url(value.Modulus) }, { "e", Base64Url(value.Exponent) }
                };
            }
        }

        private static void Save(DeviceIdentity identity)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(identity));
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            var temporary = PathName + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            if (File.Exists(PathName)) File.Replace(temporary, PathName, null); else File.Move(temporary, PathName);
        }

        private static string Base64Url(byte[] value) { return Convert.ToBase64String(value ?? new byte[0]).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }
    public sealed class DeviceConfigEnvelope
    {
        public string DeviceId { get; set; }
        // Use Int64 so future web clients may use either Unix seconds or
        // millisecond revision values without overflowing during JSON parsing.
        public long Version { get; set; }
        public string Algorithm { get; set; }
        public string SqlServer { get; set; }
        public string MySql { get; set; }
    }

    public sealed class RemoteProviderConfig
    {
        public bool Enabled { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string InitialDatabase { get; set; }
        public bool IntegratedSecurity { get; set; }
        public bool Encrypt { get; set; }
        public bool TrustServerCertificate { get; set; }
        public string CharacterSet { get; set; }
        public string SslMode { get; set; }
        public int CommandTimeoutSeconds { get; set; }
    }

    public static class RemoteConfigurationStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly object Gate = new object();
        private static string PathName { get { return AccountApiStorage.PathFor("database.config"); } }
        private static string PreviousPath { get { return AccountApiStorage.PathFor("database.config.previous"); } }

        public static void ApplyPersisted(AccountApiOptions options, DeviceIdentity identity)
        {
            if (!File.Exists(PathName)) return;
            var protectedBytes = File.ReadAllBytes(PathName);
            var bytes = ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(identity.DeviceId), DataProtectionScope.LocalMachine);
            ApplyPlainConfiguration(options, Encoding.UTF8.GetString(bytes), false);
        }

        public static IDictionary<string, object> ApplyEnvelope(AccountApiOptions options, DeviceIdentity identity, string body)
        {
            var envelope = Json.Deserialize<DeviceConfigEnvelope>(body);
            if (envelope == null || !string.Equals(envelope.DeviceId, identity.DeviceId, StringComparison.OrdinalIgnoreCase)) throw new AccountApiException("设备编号不匹配", 409, "device_mismatch");
            if (!string.Equals(envelope.Algorithm, "RSA-OAEP-SHA1", StringComparison.OrdinalIgnoreCase)) throw new AccountApiException("不支持的配置加密算法", 400, "unsupported_encryption");
            var plain = Json.Serialize(new Dictionary<string, object>
            {
                { "version", envelope.Version },
                { "sqlserver", DecryptProvider(identity, envelope.SqlServer) },
                { "mysql", DecryptProvider(identity, envelope.MySql) }
            });
            var previousProfiles = new Dictionary<string, SystemProfile>(options.Systems, StringComparer.OrdinalIgnoreCase);
            ApplyPlainConfiguration(options, plain, true);
            try
            {
                var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Encoding.UTF8.GetBytes(identity.DeviceId), DataProtectionScope.LocalMachine);
                lock (Gate)
                {
                    var temporary = PathName + ".tmp";
                    File.WriteAllBytes(temporary, protectedBytes);
                    if (File.Exists(PathName)) File.Replace(temporary, PathName, PreviousPath, true); else File.Move(temporary, PathName);
                }
            }
            catch
            {
                options.Systems = previousProfiles;
                throw;
            }
            return new Dictionary<string, object> { { "success", true }, { "version", envelope.Version }, { "appliedAt", DateTimeOffset.UtcNow.ToString("o") } };
        }

        public static IDictionary<string, object> TestEnvelope(AccountApiOptions options, DeviceIdentity identity, string body)
        {
            var envelope = Json.Deserialize<DeviceConfigEnvelope>(body);
            if (envelope == null || !string.Equals(envelope.DeviceId, identity.DeviceId, StringComparison.OrdinalIgnoreCase))
                throw new AccountApiException("设备编号不匹配", 409, "device_mismatch");
            if (!string.Equals(envelope.Algorithm, "RSA-OAEP-SHA1", StringComparison.OrdinalIgnoreCase))
                throw new AccountApiException("不支持的配置加密算法", 400, "unsupported_encryption");

            var plain = Json.Serialize(new Dictionary<string, object>
            {
                { "version", envelope.Version },
                { "sqlserver", DecryptProvider(identity, envelope.SqlServer) },
                { "mysql", DecryptProvider(identity, envelope.MySql) }
            });
            var profiles = BuildProfiles(plain);
            TestProfiles(profiles);
            var tested = new List<string>();
            foreach (var pair in profiles) if (pair.Value.Enabled) tested.Add(pair.Key);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "version", envelope.Version },
                { "testedAt", DateTimeOffset.UtcNow.ToString("o") },
                { "providers", tested.ToArray() }
            };
        }

        public static IDictionary<string, object> Rollback(AccountApiOptions options, DeviceIdentity identity)
        {
            if (!File.Exists(PreviousPath)) throw new AccountApiException("没有可回滚的上一版数据库配置", 409, "rollback_unavailable");
            var previousBytes = File.ReadAllBytes(PreviousPath);
            var plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(previousBytes, Encoding.UTF8.GetBytes(identity.DeviceId), DataProtectionScope.LocalMachine));
            var previousProfiles = new Dictionary<string, SystemProfile>(options.Systems, StringComparer.OrdinalIgnoreCase);
            ApplyPlainConfiguration(options, plain, true);
            try
            {
                lock (Gate)
                {
                    var currentBytes = File.Exists(PathName) ? File.ReadAllBytes(PathName) : null;
                    File.WriteAllBytes(PathName + ".tmp", previousBytes);
                    if (File.Exists(PathName)) File.Replace(PathName + ".tmp", PathName, null, true); else File.Move(PathName + ".tmp", PathName);
                    if (currentBytes != null) File.WriteAllBytes(PreviousPath, currentBytes); else File.Delete(PreviousPath);
                }
            }
            catch
            {
                options.Systems = previousProfiles;
                throw;
            }
            return new Dictionary<string, object> { { "success", true }, { "rolledBackAt", DateTimeOffset.UtcNow.ToString("o") } };
        }

        private static RemoteProviderConfig DecryptProvider(DeviceIdentity identity, string encrypted)
        {
            if (string.IsNullOrWhiteSpace(encrypted)) return new RemoteProviderConfig();
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(identity.PrivateKeyXml);
                var plain = rsa.Decrypt(FromBase64Url(encrypted), true);
                var provider = Json.Deserialize<RemoteProviderConfig>(Encoding.UTF8.GetString(plain));
                return provider ?? new RemoteProviderConfig();
            }
        }

        private static void ApplyPlainConfiguration(AccountApiOptions options, string plain, bool testConnections)
        {
            var next = BuildProfiles(plain);
            if (testConnections) TestProfiles(next);
            lock (Gate)
            {
                // Publish the complete profile set in one assignment. Requests
                // therefore observe either the previous snapshot or the new one,
                // never a transient empty/partially rebuilt dictionary.
                options.Systems = next;
            }
        }

        private static Dictionary<string, SystemProfile> BuildProfiles(string plain)
        {
            var root = Json.Deserialize<Dictionary<string, object>>(plain) ?? new Dictionary<string, object>();
            var sql = ConvertProvider(root, "sqlserver");
            var mysql = ConvertProvider(root, "mysql");
            return new Dictionary<string, SystemProfile>(StringComparer.OrdinalIgnoreCase)
            {
                { "sqlserver", BuildSqlServer(sql) }, { "mysql", BuildMySql(mysql) }
            };
        }

        private static void TestProfiles(Dictionary<string, SystemProfile> profiles)
        {
            var temporary = new AccountApiOptions { Systems = profiles };
            var repository = new AccountRepository(temporary);
            foreach (var pair in profiles) if (pair.Value.Enabled) repository.TestConnection(pair.Value);
        }

        private static RemoteProviderConfig ConvertProvider(IDictionary<string, object> root, string key)
        {
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return new RemoteProviderConfig();
            return Json.ConvertToType<RemoteProviderConfig>(value) ?? new RemoteProviderConfig();
        }

        private static SystemProfile BuildSqlServer(RemoteProviderConfig value)
        {
            var profile = BaseProfile(value, "SQL Server", "SqlServer", "sqlserver");
            if (!value.Enabled) return profile;
            ValidateRemote(value, 1433);
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = value.Host + "," + (value.Port <= 0 ? 1433 : value.Port), InitialCatalog = value.InitialDatabase ?? "",
                IntegratedSecurity = value.IntegratedSecurity, Encrypt = value.Encrypt, TrustServerCertificate = value.TrustServerCertificate
            };
            if (!value.IntegratedSecurity) { builder.UserID = value.User ?? ""; builder.Password = value.Password ?? ""; }
            profile.ConnectionString = builder.ConnectionString;
            return profile;
        }

        private static SystemProfile BuildMySql(RemoteProviderConfig value)
        {
            var profile = BaseProfile(value, "MySQL", "MySql", "java");
            if (!value.Enabled) return profile;
            ValidateRemote(value, 3306);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = value.Host, Port = (uint)(value.Port <= 0 ? 3306 : value.Port), UserID = value.User ?? "", Password = value.Password ?? "",
                Database = value.InitialDatabase ?? "", CharacterSet = string.IsNullOrWhiteSpace(value.CharacterSet) ? "utf8mb4" : value.CharacterSet
            };
            builder["SslMode"] = string.IsNullOrWhiteSpace(value.SslMode) ? "None" : value.SslMode;
            profile.ConnectionString = builder.ConnectionString;
            return profile;
        }

        private static SystemProfile BaseProfile(RemoteProviderConfig value, string name, string provider, string adapter)
        {
            return new SystemProfile
            {
                Enabled = value.Enabled, DisplayName = name, Provider = provider, AdapterType = adapter,
                PasswordAlgorithm = "plain", ResetPassword = "123", CommandTimeoutSeconds = Math.Min(30, Math.Max(2, value.CommandTimeoutSeconds <= 0 ? 10 : value.CommandTimeoutSeconds))
            };
        }

        private static void ValidateRemote(RemoteProviderConfig value, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(value.Host)) throw new AccountApiException("数据库服务器地址不能为空", 400, "database_host_required");
            var port = value.Port <= 0 ? defaultPort : value.Port;
            if (port < 1 || port > 65535) throw new AccountApiException("数据库端口无效", 400, "database_port_invalid");
            if (!value.IntegratedSecurity && string.IsNullOrWhiteSpace(value.User)) throw new AccountApiException("数据库账号不能为空", 400, "database_user_required");
        }

        private static byte[] FromBase64Url(string value)
        {
            var text = (value ?? "").Replace('-', '+').Replace('_', '/');
            while (text.Length % 4 != 0) text += "=";
            return Convert.FromBase64String(text);
        }
    }
}
