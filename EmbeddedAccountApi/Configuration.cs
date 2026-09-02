#nullable disable

using System;
using System.Collections.Generic;

namespace MarchCenter.AccountApi
{
    public sealed class AccountApiOptions
    {
        public ServerOptions Server { get; set; } = new ServerOptions();
        public AuthenticationOptions Authentication { get; set; } = new AuthenticationOptions();
        public Dictionary<string, SystemProfile> Systems { get; set; } = new Dictionary<string, SystemProfile>(StringComparer.OrdinalIgnoreCase);
        public string AuditLogDirectory { get; set; } = "logs";
    }

    public sealed class ServerOptions
    {
        public string BindAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8088;
        public int RequestBodyLimitBytes { get; set; } = 65536;
        public int RequestsPerMinute { get; set; } = 120;
    }

    public sealed class AuthenticationOptions
    {
        public string Mode { get; set; } = "Hmac";
        public string SigningSecret { get; set; } = "";
        public int ClockSkewSeconds { get; set; } = 300;
    }

    public sealed class SystemProfile
    {
        public bool Enabled { get; set; }
        public string DisplayName { get; set; } = "";
        public string AdapterType { get; set; } = "";
        public string PlatformUrl { get; set; } = "";
        public string Provider { get; set; } = "SqlServer";
        public string ConnectionString { get; set; } = "";
        public string AccountTable { get; set; } = "";
        public string UsernameColumn { get; set; } = "";
        public string PasswordColumn { get; set; } = "";
        public string PasswordAlgorithm { get; set; } = "md5-lower";
        public string SaltPrefix { get; set; } = "";
        public string SaltSuffix { get; set; } = "";
        public string ResetPassword { get; set; } = "123";
        public string StaticResetHash { get; set; } = "";
        public int CommandTimeoutSeconds { get; set; } = 10;
    }

    public class AccountLookupRequest
    {
        public string System { get; set; }
        public string Username { get; set; }
        public string ProfileId { get; set; }
        public string PlatformUrl { get; set; }
        public string DatabaseName { get; set; }
        public string AccountTable { get; set; }
        public string UsernameColumn { get; set; }
        public string PasswordColumn { get; set; }
        public string PasswordAlgorithm { get; set; }
        public string OriginalPasswordCiphertext { get; set; }
    }

    public sealed class SetPasswordRequest : AccountLookupRequest { public string NewPassword { get; set; } }

    public sealed class CreateAccountRequest : AccountLookupRequest
    {
        public string Password { get; set; }
        public List<ExtraColumnValue> ExtraColumns { get; set; }
    }

    public sealed class ExtraColumnValue
    {
        public string Column { get; set; }
        public string Value { get; set; }
    }

    public sealed class DbDeleteRequest
    {
        public string System { get; set; }
        public string ProfileId { get; set; }
        public string PlatformUrl { get; set; }
        public string DatabaseName { get; set; }
        public List<DbDeleteOperation> Operations { get; set; }
        public bool DryRun { get; set; }
    }

    public sealed class DbDeleteOperation
    {
        public string Table { get; set; }
        public List<DbDeleteCondition> Where { get; set; }
    }

    public sealed class DbDeleteCondition
    {
        public string Column { get; set; }
        public string Value { get; set; }
        public DbDeleteSelect InSelect { get; set; }
    }

    public sealed class DbDeleteSelect
    {
        public string Table { get; set; }
        public string SelectColumn { get; set; }
        public List<DbDeleteCondition> Where { get; set; }
    }

    public sealed class AccountApiException : Exception
    {
        public int StatusCode { get; private set; }
        public string Code { get; private set; }
        public AccountApiException(string message, int statusCode = 400, string code = "invalid_request") : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }
    }
}
