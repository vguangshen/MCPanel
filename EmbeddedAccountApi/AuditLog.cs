#nullable disable

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MarchCenter.AccountApi
{
    public sealed class AuditContext
    {
        public string Action = "request";
        public string System = "";
        public string Username = "";
        public string Database = "";
        public string Table = "";
        public string Stage = "request";
        public string RequestId = "";
        public string RemoteIp = "";
        public string Actor = "";
    }

    public sealed class AuditLog
    {
        private readonly AccountApiOptions _options;
        private readonly object _gate = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public AuditLog(AccountApiOptions options) { _options = options; }

        public void Write(AuditContext context, bool success, string detail, string errorCode = null, Exception error = null)
        {
            try
            {
                var configured = _options.AuditLogDirectory ?? "logs";
                var directory = Path.IsPathRooted(configured) ? configured : AccountApiStorage.PathFor(configured);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "audit-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
                var record = _json.Serialize(new
                {
                    time = DateTimeOffset.UtcNow.ToString("o"), action = context.Action, system = context.System, username = context.Username,
                    success, detail = Sanitize(detail), database = context.Database, table = context.Table, stage = context.Stage,
                    errorCode, exceptionType = error == null ? null : error.GetType().Name, actor = context.Actor,
                    requestId = context.RequestId, remoteIp = context.RemoteIp
                }) + Environment.NewLine;
                lock (_gate) MCPanel.RollingLogWriter.Append(path, record);
            }
            catch { }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var output = Regex.Replace(value, @"(?i)\b(password|pwd|signingsecret|bearertoken|accessclientsecret)\s*=\s*[^;,\s]+", "$1=***");
            return output.Length > 1000 ? output.Substring(0, 1000) : output;
        }
    }
}
