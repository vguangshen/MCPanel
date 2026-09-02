#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MarchCenter.AccountApi
{
    public sealed class OriginalPasswordStore
    {
        private readonly object _gate = new object();
        private readonly string _path = AccountApiStorage.PathFor("data", "original-passwords.dat");
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public void Remember(string key, string storedPassword)
        {
            lock (_gate) { var values = Load(); if (values.ContainsKey(key)) return; values[key] = storedPassword ?? ""; Save(values); }
        }

        public bool TryGet(string key, out string storedPassword) { lock (_gate) return Load().TryGetValue(key, out storedPassword); }
        public void Remove(string key) { lock (_gate) { var values = Load(); if (values.Remove(key)) Save(values); } }

        public static string Key(AccountLookupRequest value)
        {
            var raw = string.Join("\n", new[] { value.System, value.ProfileId, value.PlatformUrl, value.DatabaseName, value.AccountTable, value.UsernameColumn, value.PasswordColumn, value.Username });
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToLowerInvariant())));
        }

        private Dictionary<string, string> Load()
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            try { var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.LocalMachine); return _json.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? new Dictionary<string, string>(StringComparer.Ordinal); }
            catch (Exception error) { throw new AccountApiException("原始密码备份无法读取：" + error.Message, 500, "original_password_store_unavailable"); }
        }

        private void Save(Dictionary<string, string> values)
        {
            var directory = Path.GetDirectoryName(_path); if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(_json.Serialize(values)), null, DataProtectionScope.LocalMachine);
            var temporary = _path + ".tmp"; File.WriteAllBytes(temporary, protectedBytes);
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
        }
    }
}
