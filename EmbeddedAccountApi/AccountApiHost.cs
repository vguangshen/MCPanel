#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MCPanel;

namespace MarchCenter.AccountApi
{
    public sealed class AccountApiHost : IDisposable
    {
        private readonly AccountApiOptions _options;
        private readonly AccountRepository _repository;
        private readonly AuditLog _audit;
        private readonly DeviceIdentity _identity;
        private readonly OriginalPasswordStore _originalPasswords = new OriginalPasswordStore();
        private HttpListener _listener = new HttpListener();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, List<DateTime>> _requests = new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _nonces = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly string _configuredBindAddress;
        private string _effectiveBindAddress;
        private Task _loop;
        private int _disposed;

        public AccountApiHost(AccountApiOptions options)
        {
            _options = options; _repository = new AccountRepository(options); _audit = new AuditLog(options);
            _identity = DeviceIdentityStore.LoadOrCreate();
            _configuredBindAddress = string.IsNullOrWhiteSpace(options.Server.BindAddress)
                ? "127.0.0.1"
                : options.Server.BindAddress.Trim();
            _effectiveBindAddress = _configuredBindAddress;
            var host = FormatListenerHost(_configuredBindAddress);
            _listener.Prefixes.Add("http://" + host + ":" + options.Server.Port + "/");
        }

        public string EffectiveBindAddress { get { return _effectiveBindAddress; } }

        public bool IsRunning
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                try
                {
                    return _listener != null && _listener.IsListening && _loop != null && !_loop.IsCompleted;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException error) when (error.ErrorCode == 5 && !IsLoopback(_configuredBindAddress))
            {
                // A normal desktop user often has no HTTP.sys URL ACL for a
                // wildcard prefix. Keep the API usable locally instead of
                // making the whole MCPanel startup fail; the effective bind
                // address is returned by /health and shown in the UI.
                try { _listener.Close(); } catch { }
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + _options.Server.Port + "/");
                _listener.Start();
                _effectiveBindAddress = "127.0.0.1";
                ServiceLog.Write("警告", "bind_fallback", "当前用户没有外部 HTTP 监听权限，已回退到 127.0.0.1:" + _options.Server.Port);
            }
            _loop = Task.Run((Func<Task>)AcceptLoop);
        }

        internal static string FormatListenerHost(string value)
        {
            var host = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            var normalized = host.Trim('[', ']');
            return string.Equals(normalized, "0.0.0.0", StringComparison.Ordinal) ||
                   string.Equals(normalized, "::", StringComparison.Ordinal)
                ? "+"
                : AccountApiConfiguration.FormatUriHost(host);
        }

        private static bool IsLoopback(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Trim('[', ']');
            return string.Equals(normalized, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "::1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { if (_stop.IsCancellationRequested) break; throw; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => Process(context));
            }
        }

        private void Process(HttpListenerContext http)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var audit = new AuditContext
            {
                RequestId = requestId,
                RemoteIp = http.Request.RemoteEndPoint == null ? "" : http.Request.RemoteEndPoint.Address.ToString(),
                Actor = http.Request.Headers["X-MarchCenter-Actor"] ?? ""
            };
            try
            {
                ApplyResponseHeaders(http.Response, requestId);
                EnforceRateLimit(audit.RemoteIp);
                var body = ReadBody(http.Request);
                if (!Authenticate(http.Request, body)) throw new AccountApiException("账号管理 API 认证失败", 401, "unauthorized");
                Route(http, body, audit);
            }
            catch (AccountApiException error)
            {
                _audit.Write(audit, false, error.Message, error.Code, error);
                WriteJson(http.Response, error.StatusCode, new { error = error.Message, code = error.Code, requestId });
            }
            catch (Exception error)
            {
                _audit.Write(audit, false, error.Message, "internal_error", error);
                WriteJson(http.Response, 500, new { error = "账号管理服务内部错误", code = "internal_error", requestId });
            }
            finally { try { http.Response.Close(); } catch { } }
        }

        private void Route(HttpListenerContext http, string body, AuditContext audit)
        {
            var request = http.Request; var path = request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant(); if (path.Length == 0) path = "/";
            if (request.HttpMethod == "GET" && path == "/api/v1/device/info")
            {
                SetAudit(audit, "device_info", "device", "", null, null, "read_public_identity");
                audit.Stage = "completed"; _audit.Write(audit, true, "device_identity_returned");
                WriteJson(http.Response, 200, new { success = true, deviceId = _identity.DeviceId, publicKey = DeviceIdentityStore.PublicJwk(_identity), encryption = "RSA-OAEP-SHA1", version = ServiceVersion }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/device/config/test")
            {
                SetAudit(audit, "test_device_config", "device", "", null, null, "decrypt_and_test");
                var result = RemoteConfigurationStore.TestEnvelope(_options, _identity, body);
                audit.Stage = "completed"; _audit.Write(audit, true, "configuration_tested");
                WriteJson(http.Response, 200, result); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/device/config/apply")
            {
                SetAudit(audit, "apply_device_config", "device", "", null, null, "decrypt_and_test");
                var result = RemoteConfigurationStore.ApplyEnvelope(_options, _identity, body);
                audit.Stage = "completed"; _audit.Write(audit, true, "configuration_applied");
                WriteJson(http.Response, 200, result); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/device/config/rollback")
            {
                SetAudit(audit, "rollback_device_config", "device", "", null, null, "test_previous_configuration");
                var result = RemoteConfigurationStore.Rollback(_options, _identity);
                audit.Stage = "completed"; _audit.Write(audit, true, "configuration_rolled_back");
                WriteJson(http.Response, 200, result); return;
            }
            if (request.HttpMethod == "GET" && path == "/health") { Health(http.Response); return; }
            if (request.HttpMethod == "GET" && path == "/api/v1/capabilities") { Capabilities(http.Response); return; }
            if (request.HttpMethod == "GET" && path == "/api/v1/connections/test")
            {
                var provider = request.QueryString["provider"] ?? ""; SetAudit(audit, "test_connection", provider, "", null, null, "connect_database");
                var result = _repository.TestProviderConnection(provider); audit.Stage = "completed"; _audit.Write(audit, true, "connected");
                WriteJson(http.Response, 200, new { success = true, provider, profileId = result.Id, name = result.Name, latencyMs = result.LatencyMs }); return;
            }
            if (request.HttpMethod == "GET" && path == "/api/v1/databases")
            {
                var provider = request.QueryString["provider"] ?? ""; SetAudit(audit, "list_databases", provider, "", null, null, "list_databases");
                WriteJson(http.Response, 200, new { success = true, provider, databases = _repository.ListDatabases(provider) }); return;
            }
            if (request.HttpMethod == "GET" && path == "/api/v1/tables")
            {
                var provider = request.QueryString["provider"] ?? ""; var database = request.QueryString["databaseName"] ?? ""; SetAudit(audit, "list_tables", provider, "", database, null, "list_tables");
                WriteJson(http.Response, 200, new { success = true, provider, databaseName = database, tables = _repository.ListTables(provider, database) }); return;
            }
            if (request.HttpMethod == "GET" && path == "/api/v1/columns")
            {
                var provider = request.QueryString["provider"] ?? ""; var database = request.QueryString["databaseName"] ?? ""; var table = request.QueryString["accountTable"] ?? ""; SetAudit(audit, "list_columns", provider, "", database, table, "list_columns");
                WriteJson(http.Response, 200, new { success = true, provider, databaseName = database, accountTable = table, columns = _repository.ListColumns(provider, database, table) }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/query")
            {
                var model = Deserialize<AccountLookupRequest>(body); ValidateIdentity(model); SetAudit(audit, "query_account", model.System, model.Username, model.DatabaseName, model.AccountTable, "query_account");
                var exists = _repository.Exists(model.System, model.Username, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn); audit.Stage = "completed"; _audit.Write(audit, true, exists ? "exists" : "not_found");
                WriteJson(http.Response, 200, new { success = true, system = model.System, username = model.Username, exists }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/set-password")
            {
                var model = Deserialize<SetPasswordRequest>(body); ValidateIdentity(model); ValidatePassword(model.NewPassword); SetAudit(audit, "set_password", model.System, model.Username, model.DatabaseName, model.AccountTable, "update_password");
                var updated = _repository.SetPassword(model.System, model.Username, model.NewPassword, false, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn, model.PasswordAlgorithm); audit.Stage = "completed"; _audit.Write(audit, updated, updated ? "updated" : "not_found");
                if (!updated) throw new AccountApiException("账号不存在", 404, "account_not_found"); WriteJson(http.Response, 200, new { success = true, system = model.System, username = model.Username, updated = true }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/reset-password")
            {
                var model = Deserialize<AccountLookupRequest>(body); ValidateIdentity(model); SetAudit(audit, "restore_original_password", model.System, model.Username, model.DatabaseName, model.AccountTable, "load_original_password");
                string original; var key = OriginalPasswordStore.Key(model);
                if (!string.IsNullOrWhiteSpace(model.OriginalPasswordCiphertext))
                {
                    original = model.OriginalPasswordCiphertext.Trim();
                    if (original.Length > 4096 || original.Any(char.IsControl)) throw new AccountApiException("手动原始密文格式无效", 400, "original_password_ciphertext_invalid");
                }
                else if (!_originalPasswords.TryGet(key, out original)) throw new AccountApiException("没有找到该账号提取前的原始密码备份，且竞赛卡片未配置手动原始密文。", 409, "original_password_unavailable");
                audit.Stage = "restore_original_password";
                var updated = _repository.WriteStoredPassword(model.System, model.Username, original, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn);
                if (!updated) throw new AccountApiException("账号不存在", 404, "account_not_found");
                _originalPasswords.Remove(key); audit.Stage = "completed"; _audit.Write(audit, true, "original_password_restored");
                WriteJson(http.Response, 200, new { success = true, system = model.System, username = model.Username, updated = true, restored = true }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/legacy-reset-password")
            {
                var model = Deserialize<AccountLookupRequest>(body); ValidateIdentity(model); var profile = _repository.GetProfile(model.System, model.ProfileId, model.PlatformUrl); ValidatePassword(profile.ResetPassword); SetAudit(audit, "reset_password", model.System, model.Username, model.DatabaseName, model.AccountTable, "update_password");
                if (string.IsNullOrWhiteSpace(model.PasswordAlgorithm)) throw new AccountApiException("竞赛卡片未指定密码算法", 400, "password_algorithm_required");
                var updated = _repository.SetPassword(model.System, model.Username, profile.ResetPassword, true, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn, model.PasswordAlgorithm); audit.Stage = "completed"; _audit.Write(audit, updated, updated ? "updated" : "not_found");
                if (!updated) throw new AccountApiException("账号不存在", 404, "account_not_found"); WriteJson(http.Response, 200, new { success = true, system = model.System, username = model.Username, updated = true, temporaryPassword = profile.ResetPassword }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/issue-credential")
            {
                var model = Deserialize<AccountLookupRequest>(body); ValidateIdentity(model);
                if (string.IsNullOrWhiteSpace(model.PasswordAlgorithm)) throw new AccountApiException("竞赛卡片未指定密码算法", 400, "password_algorithm_required");
                SetAudit(audit, "issue_credential", model.System, model.Username, model.DatabaseName, model.AccountTable, "generate_password"); var temporaryPassword = RandomPassword();
                var resolved = _repository.ResolveProfile(model.System, model.ProfileId, model.PlatformUrl); audit.Stage = "backup_original_password";
                var original = _repository.ReadStoredPassword(model.System, model.Username, resolved.Id, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn);
                _originalPasswords.Remember(OriginalPasswordStore.Key(model), original);
                audit.Stage = "update_password";
                var updated = _repository.SetPassword(model.System, model.Username, temporaryPassword, false, resolved.Id, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn, model.PasswordAlgorithm); audit.Stage = "completed"; _audit.Write(audit, updated, updated ? "updated" : "not_found");
                if (!updated) throw new AccountApiException("账号不存在", 404, "account_not_found"); WriteJson(http.Response, 200, new { success = true, system = model.System, profileId = resolved.Id, username = model.Username, updated = true, temporaryPassword, originalPasswordCiphertext = original, passwordDigits = 6 }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/db/delete")
            {
                var model = Deserialize<DbDeleteRequest>(body);
                SetAudit(audit, "db_delete", model.System ?? "", "", model.DatabaseName ?? "", "", model.DryRun ? "preview_delete" : "delete_rows");
                var results = _repository.DeleteRows(model.System, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.Operations, model.DryRun);
                audit.Stage = "completed"; _audit.Write(audit, true, model.DryRun ? "previewed" : "deleted");
                WriteJson(http.Response, 200, new { success = true, dryRun = model.DryRun, results }); return;
            }
            if (request.HttpMethod == "POST" && path == "/api/v1/accounts/create")
            {
                var model = Deserialize<CreateAccountRequest>(body);
                ValidateIdentity(model);
                ValidatePassword(model.Password);
                if (string.IsNullOrWhiteSpace(model.PasswordAlgorithm)) throw new AccountApiException("竞赛卡片未指定密码算法", 400, "password_algorithm_required");
                SetAudit(audit, "create_account", model.System, model.Username, model.DatabaseName, model.AccountTable, "insert_account");
                var created = _repository.CreateAccount(model.System, model.Username, model.Password, model.ProfileId, model.PlatformUrl, model.DatabaseName, model.AccountTable, model.UsernameColumn, model.PasswordColumn, model.PasswordAlgorithm, model.ExtraColumns);
                audit.Stage = "completed"; _audit.Write(audit, true, created ? "created" : "already_exists");
                WriteJson(http.Response, 200, new { success = true, system = model.System, username = model.Username, created, exists = !created }); return;
            }
            throw new AccountApiException("接口不存在", 404, "not_found");
        }

        private void Health(HttpListenerResponse response)
        {
            var systems = new Dictionary<string, object>(); var configured = 0; var healthy = true;
            foreach (var pair in _repository.Profiles.Where(x => x.Value.Enabled))
            {
                configured++; var started = DateTime.UtcNow;
                try { _repository.TestConnection(pair.Value); systems[pair.Key] = new { ok = true, name = pair.Value.DisplayName, latencyMs = (int)(DateTime.UtcNow - started).TotalMilliseconds }; }
                catch (Exception error) { healthy = false; systems[pair.Key] = new { ok = false, name = pair.Value.DisplayName, error = SafeError(error) }; }
            }
            var ready = configured > 0;
            var ok = ready && healthy;
            var status = !ready ? "setup_required" : healthy ? "ok" : "degraded";
            WriteJson(response, ok ? 200 : 503, new { ok, status, service = "MarchCenter Account API", version = ServiceVersion, deviceId = _identity.DeviceId, bindAddress = _effectiveBindAddress, systems });
        }

        private void Capabilities(HttpListenerResponse response)
        {
            var systems = _repository.Profiles.Where(x => x.Value.Enabled).Select(x => new { id=x.Key,name=x.Value.DisplayName,adapterType=x.Value.AdapterType,platformUrl=x.Value.PlatformUrl,provider=x.Value.Provider,randomPassword=!string.Equals(x.Value.PasswordAlgorithm,"static",StringComparison.OrdinalIgnoreCase) }).ToArray();
            WriteJson(response, 200, new { service = "MarchCenter Account API", capabilities = new[] { "queryAccount", "setPassword", "resetPassword", "issueCredential", "createAccount", "deleteRows", "listDatabases", "listTables", "listColumns", "testConnection" }, systems });
        }

        private static string ServiceVersion
        {
            get
            {
                // Keep the public API contract stable after moving the
                // implementation into MCPanel's process.
                return "2.0.2";
            }
        }

        private string ReadBody(HttpListenerRequest request)
        {
            var limit = _options.Server.RequestBodyLimitBytes > 0 ? _options.Server.RequestBodyLimitBytes : 65536;
            if (request.ContentLength64 > limit) throw new AccountApiException("请求体过大", 413, "body_too_large");
            if (!request.HasEntityBody) return "";
            using (var body = new MemoryStream(Math.Min(limit, 1024 * 1024)))
            {
                var buffer = new byte[Math.Min(limit, 8192)];
                var total = 0;
                while (true)
                {
                    var remaining = limit - total;
                    var toRead = remaining >= buffer.Length ? buffer.Length : remaining + 1;
                    var read = request.InputStream.Read(buffer, 0, toRead);
                    if (read <= 0) break;
                    if (read > remaining) throw new AccountApiException("请求体过大", 413, "body_too_large");
                    body.Write(buffer, 0, read);
                    total += read;
                }

                return (request.ContentEncoding ?? Encoding.UTF8).GetString(body.ToArray());
            }
        }

        private bool Authenticate(HttpListenerRequest request, string body)
        {
            var auth = _options.Authentication; var mode = (auth.Mode ?? "").Trim().ToLowerInvariant();
            // 桥梁只支持 HMAC 请求签名：不再开放 bearer/none/cloudflare_access 等认证方式。
            if (mode != "hmac" || string.IsNullOrWhiteSpace(auth.SigningSecret) || auth.SigningSecret.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase)) return false;
            long timestamp; var timeText = request.Headers["X-MarchCenter-Timestamp"] ?? ""; var nonce = request.Headers["X-MarchCenter-Nonce"] ?? ""; var signature = request.Headers["X-MarchCenter-Signature"] ?? "";
            if (!long.TryParse(timeText, out timestamp) || nonce.Length == 0 || signature.Length == 0) return false; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var skew = Math.Min(600, Math.Max(30, auth.ClockSkewSeconds)); if (Math.Abs(now - timestamp) > skew || !_nonces.TryAdd(nonce, now)) return false;
            foreach (var old in _nonces.Where(x => x.Value < now - skew * 2).Take(100).ToArray()) { long removed; _nonces.TryRemove(old.Key, out removed); }
            var payload = timeText + "\n" + nonce + "\n" + request.HttpMethod.ToUpperInvariant() + "\n" + request.RawUrl + "\n" + body;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(auth.SigningSecret))) return FixedEquals(signature, Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))));
        }

        private void EnforceRateLimit(string remoteIp)
        {
            var now = DateTime.UtcNow; var list = _requests.GetOrAdd(remoteIp ?? "unknown", _ => new List<DateTime>());
            lock (list) { list.RemoveAll(x => x < now.AddMinutes(-1)); if (list.Count >= Math.Max(10, Math.Min(1000, _options.Server.RequestsPerMinute))) throw new AccountApiException("请求过于频繁", 429, "rate_limited"); list.Add(now); }
        }

        private T Deserialize<T>(string body) where T : class { if (string.IsNullOrWhiteSpace(body)) throw new AccountApiException("请求内容为空"); try { var value = _json.Deserialize<T>(body); if (value == null) throw new Exception(); return value; } catch { throw new AccountApiException("请求 JSON 格式错误"); } }
        private static void ValidateIdentity(AccountLookupRequest value) { if (value == null || string.IsNullOrWhiteSpace(value.System) || string.IsNullOrWhiteSpace(value.Username)) throw new AccountApiException("系统类型和用户名不能为空"); if (value.Username.Length > 128) throw new AccountApiException("用户名过长"); }
        private static void ValidatePassword(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new AccountApiException("密码不能为空且不能超过 128 位"); }
        private static void SetAudit(AuditContext value, string action, string system, string username, string database, string table, string stage) { value.Action=action;value.System=system;value.Username=username;value.Database=database;value.Table=table;value.Stage=stage; }
        private void WriteJson(HttpListenerResponse response, int statusCode, object value) { if (response.OutputStream == null) return; var bytes = Encoding.UTF8.GetBytes(_json.Serialize(value)); response.StatusCode=statusCode;response.ContentType="application/json; charset=utf-8";response.ContentEncoding=Encoding.UTF8;response.ContentLength64=bytes.Length;response.OutputStream.Write(bytes,0,bytes.Length); }
        private static void ApplyResponseHeaders(HttpListenerResponse response, string requestId) { response.Headers["Cache-Control"]="no-store";response.Headers["X-Content-Type-Options"]="nosniff";response.Headers["X-Frame-Options"]="DENY";response.Headers["X-Request-Id"]=requestId; }
        private static string SafeError(Exception error) { return error is AccountApiException ? error.Message : "数据库连接失败，请查看执行端日志"; }
        private static string RandomPassword() { var bytes=new byte[4];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);var number=(BitConverter.ToUInt32(bytes,0)%900000)+100000;return number.ToString(); }
        private static string Base64Url(byte[] value) { return Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_'); }
        private static bool FixedEquals(string left, string right) { var a=Encoding.UTF8.GetBytes(left??"");var b=Encoding.UTF8.GetBytes(right??"");if(a.Length!=b.Length)return false;var different=0;for(var i=0;i<a.Length;i++)different|=a[i]^b[i];return different==0; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { if (_loop != null) _loop.Wait(2000); } catch { }
            _stop.Dispose();
        }
    }
}
