#nullable disable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace MarchCenter.AccountApi
{
    public sealed class ProfileResolution { public string Id; public SystemProfile Profile; }
    public sealed class ConnectionTestResult { public string Id; public string Name; public int LatencyMs; }
    public sealed class DbDeleteResult { public string Table; public long DeletedRows; }

    public sealed class AccountRepository
    {
        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly AccountApiOptions _options;
        public AccountRepository(AccountApiOptions options) { _options = options; }
        public IDictionary<string, SystemProfile> Profiles { get { return _options.Systems; } }

        public ProfileResolution ResolveProfile(string system, string profileId = null, string platformUrl = null)
        {
            SystemProfile exact;
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                if (!_options.Systems.TryGetValue(profileId, out exact) || !exact.Enabled) throw new AccountApiException("指定的数据库连接不存在或未启用", 404, "profile_not_configured");
                ValidateConnection(exact); return new ProfileResolution { Id = profileId, Profile = exact };
            }
            var usesSql = Same(system, "net") || Same(system, "marketing") || Same(system, "commerce");
            var candidates = _options.Systems.Where(delegate(KeyValuePair<string, SystemProfile> entry)
            {
                var p = entry.Value;
                return p.Enabled && (Same(p.AdapterType, system) || (usesSql && Same(p.AdapterType, "sqlserver")) || (Same(system, "java") && Same(p.Provider, "MySql")) || (usesSql && Same(p.Provider, "SqlServer")));
            }).ToList();
            if (!string.IsNullOrWhiteSpace(platformUrl))
            {
                var target = NormalizePlatformAddress(platformUrl);
                var matched = candidates.Where(x => PlatformMatches(x.Value.PlatformUrl, target)).ToList();
                if (matched.Count == 1) { ValidateConnection(matched[0].Value); return new ProfileResolution { Id = matched[0].Key, Profile = matched[0].Value }; }
                if (matched.Count > 1) throw new AccountApiException("有多个数据库连接匹配该软件地址", 409, "profile_ambiguous");
            }
            if (candidates.Count == 1) { ValidateConnection(candidates[0].Value); return new ProfileResolution { Id = candidates[0].Key, Profile = candidates[0].Value }; }
            if (candidates.Count > 1) throw new AccountApiException("该软件类型配置了多个数据库", 409, "profile_required");
            throw new AccountApiException("指定系统未启用或不存在", 404, "system_not_configured");
        }

        public SystemProfile GetProfile(string system, string profileId = null, string platformUrl = null) { return ResolveProfile(system, profileId, platformUrl).Profile; }

        public bool TestConnection(SystemProfile profile)
        {
            ValidateConnection(profile);
            using (var connection = CreateConnection(profile))
            {
                connection.Open();
                using (var command = connection.CreateCommand()) { command.CommandText = "SELECT 1"; command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30); command.ExecuteScalar(); }
            }
            return true;
        }

        public ConnectionTestResult TestProviderConnection(string provider)
        {
            var normalized = NormalizeProvider(provider);
            var matches = Profiles.Where(x => x.Value.Enabled && Same(x.Value.Provider, normalized)).ToList();
            if (matches.Count != 1) throw new AccountApiException(matches.Count == 0 ? "该数据库连接尚未启用" : "该数据库类型存在多条连接配置", 409, "database_connection_ambiguous");
            var watch = Stopwatch.StartNew(); TestConnection(matches[0].Value); watch.Stop();
            return new ConnectionTestResult { Id = matches[0].Key, Name = matches[0].Value.DisplayName, LatencyMs = (int)watch.ElapsedMilliseconds };
        }

        public bool Exists(string system, string username, string profileId, string platformUrl, string databaseName, string accountTable, string usernameColumn, string passwordColumn)
        {
            var profile = WithDatabaseTableAndColumns(GetProfile(system, profileId, platformUrl), databaseName, accountTable, usernameColumn, passwordColumn);
            using (var connection = CreateConnection(profile)) using (var command = connection.CreateCommand())
            {
                connection.Open(); command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                command.CommandText = "SELECT COUNT(1) FROM " + QuotePath(profile.AccountTable, profile) + " WHERE " + QuoteIdentifier(profile.UsernameColumn, profile) + " = @username";
                AddParameter(command, "@username", username); return Convert.ToInt64(command.ExecuteScalar() ?? 0) > 0;
            }
        }

        public bool SetPassword(string system, string username, string password, bool isReset, string profileId, string platformUrl, string databaseName, string accountTable, string usernameColumn, string passwordColumn, string passwordAlgorithm = null)
        {
            var profile = WithDatabaseTableAndColumns(GetProfile(system, profileId, platformUrl), databaseName, accountTable, usernameColumn, passwordColumn);
            if (!string.IsNullOrWhiteSpace(passwordAlgorithm))
            {
                if (!PasswordHasher.IsSupported(passwordAlgorithm)) throw new AccountApiException("不支持的密码算法", 400, "invalid_password_algorithm");
                profile = Clone(profile);
                profile.PasswordAlgorithm = passwordAlgorithm.Trim().ToLowerInvariant();
            }
            var stored = isReset ? PasswordHasher.HashForReset(password, profile) : PasswordHasher.HashForSet(password, profile);
            using (var connection = CreateConnection(profile))
            {
                connection.Open(); using (var transaction = connection.BeginTransaction()) using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction; command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                    command.CommandText = "UPDATE " + QuotePath(profile.AccountTable, profile) + " SET " + QuoteIdentifier(profile.PasswordColumn, profile) + " = @password WHERE " + QuoteIdentifier(profile.UsernameColumn, profile) + " = @username";
                    AddParameter(command, "@password", stored); AddParameter(command, "@username", username); var affected = command.ExecuteNonQuery();
                    if (affected > 1) { transaction.Rollback(); throw new AccountApiException("账号字段不是唯一值，本次修改已回滚", 409, "username_not_unique"); }
                    transaction.Commit(); return affected == 1;
                }
            }
        }

        public string ReadStoredPassword(string system, string username, string profileId, string platformUrl, string databaseName, string accountTable, string usernameColumn, string passwordColumn)
        {
            var profile = WithDatabaseTableAndColumns(GetProfile(system, profileId, platformUrl), databaseName, accountTable, usernameColumn, passwordColumn);
            using (var connection = CreateConnection(profile)) using (var command = connection.CreateCommand())
            {
                connection.Open(); command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                command.CommandText = "SELECT " + QuoteIdentifier(profile.PasswordColumn, profile) + " FROM " + QuotePath(profile.AccountTable, profile) + " WHERE " + QuoteIdentifier(profile.UsernameColumn, profile) + " = @username";
                AddParameter(command, "@username", username);
                var values = new List<string>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0)));
                if (values.Count == 0) throw new AccountApiException("账号不存在", 404, "account_not_found");
                if (values.Count > 1) throw new AccountApiException("账号字段不是唯一值，无法安全修改", 409, "username_not_unique");
                return values[0];
            }
        }

        public bool WriteStoredPassword(string system, string username, string storedPassword, string profileId, string platformUrl, string databaseName, string accountTable, string usernameColumn, string passwordColumn)
        {
            var profile = WithDatabaseTableAndColumns(GetProfile(system, profileId, platformUrl), databaseName, accountTable, usernameColumn, passwordColumn);
            using (var connection = CreateConnection(profile))
            {
                connection.Open(); using (var transaction = connection.BeginTransaction()) using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction; command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                    command.CommandText = "UPDATE " + QuotePath(profile.AccountTable, profile) + " SET " + QuoteIdentifier(profile.PasswordColumn, profile) + " = @password WHERE " + QuoteIdentifier(profile.UsernameColumn, profile) + " = @username";
                    AddParameter(command, "@password", storedPassword ?? ""); AddParameter(command, "@username", username); var affected = command.ExecuteNonQuery();
                    if (affected > 1) { transaction.Rollback(); throw new AccountApiException("账号字段不是唯一值，本次修改已回滚", 409, "username_not_unique"); }
                    transaction.Commit(); return affected == 1;
                }
            }
        }

        public IList<DbDeleteResult> DeleteRows(string system, string profileId, string platformUrl, string databaseName, IList<DbDeleteOperation> operations, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(system)) throw new AccountApiException("系统类型不能为空", 400, "system_required");
            if (operations == null || operations.Count == 0) throw new AccountApiException("未指定任何删除操作", 400, "operations_required");
            if (operations.Count > 100) throw new AccountApiException("删除操作过多（最多 100 条）", 400, "too_many_operations");
            var profile = WithDatabase(GetProfile(system, profileId, platformUrl), databaseName);
            var results = new List<DbDeleteResult>();
            using (var connection = CreateConnection(profile))
            {
                connection.Open();
                var transaction = dryRun ? null : connection.BeginTransaction();
                try
                {
                    foreach (var operation in operations)
                    {
                        if (operation == null || string.IsNullOrWhiteSpace(operation.Table)) throw new AccountApiException("删除操作缺少数据表", 400, "table_required");
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                            var sql = new StringBuilder();
                            var parameterIndex = 0;
                            if (operation.Where != null && operation.Where.Count > 0)
                            {
                                sql.Append(" WHERE ");
                                for (var i = 0; i < operation.Where.Count; i++)
                                {
                                    if (i > 0) sql.Append(" AND ");
                                    BuildCondition(operation.Where[i], profile, command, sql, ref parameterIndex);
                                }
                            }
                            var count = dryRun
                                ? ExecuteDeleteCount(command, profile, operation.Table, sql.ToString())
                                : ExecuteDeleteRows(command, profile, operation.Table, sql.ToString());
                            results.Add(new DbDeleteResult { Table = operation.Table, DeletedRows = count });
                        }
                    }
                    if (!dryRun) transaction.Commit();
                    return results;
                }
                catch
                {
                    if (transaction != null) { try { transaction.Rollback(); } catch { } }
                    throw;
                }
            }
        }

        public bool CreateAccount(string system, string username, string password, string profileId, string platformUrl, string databaseName, string accountTable, string usernameColumn, string passwordColumn, string passwordAlgorithm, IList<ExtraColumnValue> extraColumns)
        {
            var profile = WithDatabaseTableAndColumns(GetProfile(system, profileId, platformUrl), databaseName, accountTable, usernameColumn, passwordColumn);
            if (string.IsNullOrWhiteSpace(username)) throw new AccountApiException("用户名不能为空", 400, "username_required");
            if (string.IsNullOrWhiteSpace(password)) throw new AccountApiException("初始密码不能为空", 400, "password_required");
            if (!string.IsNullOrWhiteSpace(passwordAlgorithm))
            {
                if (!PasswordHasher.IsSupported(passwordAlgorithm)) throw new AccountApiException("不支持的密码算法", 400, "invalid_password_algorithm");
                profile = Clone(profile);
                profile.PasswordAlgorithm = passwordAlgorithm.Trim().ToLowerInvariant();
            }
            var stored = PasswordHasher.HashForSet(password, profile);
            using (var connection = CreateConnection(profile))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    bool exists;
                    using (var checkCommand = connection.CreateCommand())
                    {
                        checkCommand.Transaction = transaction;
                        checkCommand.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                        checkCommand.CommandText = "SELECT COUNT(1) FROM " + QuotePath(profile.AccountTable, profile) + " WHERE " + QuoteIdentifier(profile.UsernameColumn, profile) + " = @username";
                        AddParameter(checkCommand, "@username", username);
                        exists = Convert.ToInt64(checkCommand.ExecuteScalar() ?? 0) > 0;
                    }
                    if (exists) return false; // 事务随 using 释放自动回滚（未写任何数据）
                    using (var insertCommand = connection.CreateCommand())
                    {
                        insertCommand.Transaction = transaction;
                        insertCommand.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                        var columns = new List<string> { QuoteIdentifier(profile.UsernameColumn, profile), QuoteIdentifier(profile.PasswordColumn, profile) };
                        var values = new List<string> { "@username", "@password" };
                        AddParameter(insertCommand, "@username", username);
                        AddParameter(insertCommand, "@password", stored);
                        var extraIndex = 0;
                        foreach (var extra in extraColumns ?? new List<ExtraColumnValue>())
                        {
                            if (extra == null || string.IsNullOrWhiteSpace(extra.Column)) continue;
                            columns.Add(QuoteIdentifier(extra.Column, profile));
                            var parameterName = "@x" + (extraIndex++);
                            values.Add(parameterName);
                            AddParameter(insertCommand, parameterName, extra.Value ?? "");
                        }
                        insertCommand.CommandText = "INSERT INTO " + QuotePath(profile.AccountTable, profile)
                            + " (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", values) + ")";
                        insertCommand.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return true;
                }
            }
        }

        private static void BuildCondition(DbDeleteCondition condition, SystemProfile profile, DbCommand command, StringBuilder sql, ref int parameterIndex)
        {
            if (condition == null || string.IsNullOrWhiteSpace(condition.Column)) throw new AccountApiException("删除条件缺少列名", 400, "condition_column_required");
            var column = QuoteIdentifier(condition.Column, profile);
            if (condition.InSelect != null)
            {
                var select = condition.InSelect;
                if (string.IsNullOrWhiteSpace(select.Table) || string.IsNullOrWhiteSpace(select.SelectColumn)) throw new AccountApiException("子查询条件不完整", 400, "condition_in_select_incomplete");
                sql.Append(column).Append(" IN (SELECT ").Append(QuoteIdentifier(select.SelectColumn, profile)).Append(" FROM ").Append(QuotePath(select.Table, profile));
                if (select.Where != null && select.Where.Count > 0)
                {
                    sql.Append(" WHERE ");
                    for (var i = 0; i < select.Where.Count; i++)
                    {
                        if (i > 0) sql.Append(" AND ");
                        BuildCondition(select.Where[i], profile, command, sql, ref parameterIndex);
                    }
                }
                sql.Append(")");
            }
            else
            {
                var parameterName = "@p" + (parameterIndex++);
                sql.Append(column).Append(" = ").Append(parameterName);
                AddParameter(command, parameterName, condition.Value ?? "");
            }
        }

        private static long ExecuteDeleteRows(DbCommand command, SystemProfile profile, string table, string whereSql)
        {
            command.CommandText = "DELETE FROM " + QuotePath(table, profile) + whereSql;
            return command.ExecuteNonQuery();
        }

        private static long ExecuteDeleteCount(DbCommand command, SystemProfile profile, string table, string whereSql)
        {
            command.CommandText = "SELECT COUNT(1) FROM " + QuotePath(table, profile) + whereSql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0);
        }

        public IList<string> ListDatabases(string provider)
        {
            var normalized = NormalizeProvider(provider);
            var profile = ProviderProfile(normalized);
            using (var connection = CreateConnection(profile)) using (var command = connection.CreateCommand())
            {
                connection.Open(); command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                command.CommandText = Same(normalized, "MySql") ? "SHOW DATABASES" : "SELECT [name] FROM sys.databases WHERE [state] = 0 AND database_id > 4 ORDER BY [name]";
                var names = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    var name = reader.GetString(0); if (Same(normalized, "MySql") && new[] { "information_schema", "mysql", "performance_schema", "sys" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue; names.Add(name);
                }
                return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public IList<string> ListTables(string provider, string databaseName)
        {
            var normalized = NormalizeProvider(provider); if (string.IsNullOrWhiteSpace(databaseName)) throw new AccountApiException("请先选择数据库", 400, "database_required");
            var profile = WithDatabase(ProviderProfile(normalized), databaseName);
            using (var connection = CreateConnection(profile)) using (var command = connection.CreateCommand())
            {
                connection.Open(); command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30);
                if (Same(normalized, "MySql")) { command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@database AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME"; AddParameter(command, "@database", databaseName); }
                else command.CommandText = "SELECT s.[name]+'.'+t.[name] FROM sys.tables t INNER JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.[name],t.[name]";
                var values = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(reader.GetString(0)); return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public IList<string> ListColumns(string provider, string databaseName, string accountTable)
        {
            var normalized = NormalizeProvider(provider); if (string.IsNullOrWhiteSpace(databaseName)) throw new AccountApiException("请先选择数据库", 400, "database_required"); if (string.IsNullOrWhiteSpace(accountTable)) throw new AccountApiException("请先选择数据表", 400, "table_required");
            var profile = WithDatabase(ProviderProfile(normalized), databaseName); QuotePath(accountTable, profile);
            using (var connection = CreateConnection(profile)) using (var command = connection.CreateCommand())
            {
                connection.Open(); command.CommandTimeout = Clamp(profile.CommandTimeoutSeconds, 2, 30); var parts = SplitPath(accountTable); var table = parts[parts.Length - 1];
                if (Same(normalized, "MySql")) { command.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@database AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION"; AddParameter(command, "@database", databaseName); AddParameter(command, "@table", table); }
                else { command.CommandText = "SELECT c.[name] FROM sys.columns c INNER JOIN sys.tables t ON t.object_id=c.object_id INNER JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.[name]=@schema AND t.[name]=@table ORDER BY c.column_id"; AddParameter(command, "@schema", parts.Length == 2 ? parts[0] : "dbo"); AddParameter(command, "@table", table); }
                var values = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(reader.GetString(0)); return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        private SystemProfile ProviderProfile(string provider)
        {
            var matches = Profiles.Where(x => x.Value.Enabled && Same(x.Value.Provider, provider)).ToList();
            if (matches.Count != 1) throw new AccountApiException(matches.Count == 0 ? "该数据库连接尚未启用" : "该数据库类型存在多条连接配置", 409, "database_connection_ambiguous");
            return matches[0].Value;
        }

        private static SystemProfile WithDatabaseTableAndColumns(SystemProfile source, string databaseName, string accountTable, string usernameColumn, string passwordColumn)
        {
            var profile = WithDatabase(source, databaseName); profile = Clone(profile); profile.AccountTable = (accountTable ?? profile.AccountTable ?? "").Trim(); profile.UsernameColumn = (usernameColumn ?? profile.UsernameColumn ?? "").Trim(); profile.PasswordColumn = (passwordColumn ?? profile.PasswordColumn ?? "").Trim();
            if (profile.AccountTable.Length == 0) throw new AccountApiException("请在竞赛卡片中选择账号数据表", 400, "account_table_required");
            if (profile.UsernameColumn.Length == 0 || profile.PasswordColumn.Length == 0) throw new AccountApiException("请在竞赛卡片中同时选择用户名字段和密码字段", 400, "account_columns_required");
            QuotePath(profile.AccountTable, profile); QuoteIdentifier(profile.UsernameColumn, profile); QuoteIdentifier(profile.PasswordColumn, profile); return profile;
        }

        private static SystemProfile WithDatabase(SystemProfile source, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName)) return source; if (databaseName.Length > 128 || databaseName.Any(char.IsControl)) throw new AccountApiException("数据库名称无效"); var copy = Clone(source);
            if (Same(copy.Provider, "MySql")) { var builder = new MySqlConnectionStringBuilder(copy.ConnectionString); builder.Database = databaseName; copy.ConnectionString = builder.ConnectionString; }
            else { var builder = new SqlConnectionStringBuilder(copy.ConnectionString); builder.InitialCatalog = databaseName; copy.ConnectionString = builder.ConnectionString; }
            return copy;
        }

        private static SystemProfile Clone(SystemProfile p) { return new SystemProfile { Enabled=p.Enabled,DisplayName=p.DisplayName,AdapterType=p.AdapterType,PlatformUrl=p.PlatformUrl,Provider=p.Provider,ConnectionString=p.ConnectionString,AccountTable=p.AccountTable,UsernameColumn=p.UsernameColumn,PasswordColumn=p.PasswordColumn,PasswordAlgorithm=p.PasswordAlgorithm,SaltPrefix=p.SaltPrefix,SaltSuffix=p.SaltSuffix,ResetPassword=p.ResetPassword,StaticResetHash=p.StaticResetHash,CommandTimeoutSeconds=p.CommandTimeoutSeconds }; }
        private static DbConnection CreateConnection(SystemProfile p) { ValidateConnection(p); return Same(p.Provider, "MySql") ? (DbConnection)new MySqlConnection(p.ConnectionString) : new SqlConnection(p.ConnectionString); }
        private static void ValidateConnection(SystemProfile p) { if (string.IsNullOrWhiteSpace(p.ConnectionString)) throw new AccountApiException("数据库连接字符串尚未配置", 500, "profile_invalid"); }
        private static string NormalizeProvider(string provider) { if (Same(provider, "mysql")) return "MySql"; if (Same(provider, "sqlserver") || Same(provider, "sql_server") || Same(provider, "mssql")) return "SqlServer"; throw new AccountApiException("数据库类型仅支持 MySQL 或 SQL Server"); }
        private static string QuotePath(string value, SystemProfile p) { var parts = SplitPath(value); if (parts.Length < 1 || parts.Length > 2) throw new AccountApiException("账号表名配置无效", 500, "profile_invalid"); return string.Join(".", parts.Select(x => QuoteIdentifier(x, p)).ToArray()); }
        private static string[] SplitPath(string value) { return (value ?? "").Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(); }
        private static string QuoteIdentifier(string value, SystemProfile p) { if (string.IsNullOrWhiteSpace(value) || !Identifier.IsMatch(value)) throw new AccountApiException("数据库表名或列名只能包含字母、数字、下划线和 $", 500, "profile_invalid"); return Same(p.Provider, "MySql") ? "`" + value + "`" : "[" + value + "]"; }
        private static void AddParameter(DbCommand command, string name, object value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
        private static int Clamp(int value, int min, int max) { return Math.Min(max, Math.Max(min, value)); }
        private static bool Same(string a, string b) { return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }
        private static string NormalizePlatformAddress(string value) { Uri uri; if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return ""; return uri.Scheme.ToLowerInvariant() + "://" + uri.Authority.ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant(); }
        private static bool PlatformMatches(string configured, string target) { var source = NormalizePlatformAddress(configured); return source.Length > 0 && target.Length > 0 && (Same(source, target) || target.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase) || source.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)); }
    }
}
