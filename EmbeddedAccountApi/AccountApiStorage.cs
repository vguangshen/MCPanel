#nullable disable

using System;
using System.Collections.Generic;
using System.IO;

namespace MarchCenter.AccountApi
{
    /// <summary>
    /// Owns the writable state of the embedded Account API.
    ///
    /// Account API is an independently managed component, so its state lives
    /// in a sibling directory beside StoreData.  This keeps API configuration,
    /// encrypted profiles and audit logs together without mixing them into
    /// MCPanel's general application state.
    /// </summary>
    public static class AccountApiStorage
    {
        private static readonly object Gate = new object();
        private static bool _initialized;

        public static string RootPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AccountApi"); }
        }

        /// <summary>
        /// Account API state used by releases before it became a sibling of
        /// StoreData.  It is read once during startup and migrated when the
        /// new directory is writable.
        /// </summary>
        public static string LegacyStoreDataRootPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StoreData", "AccountApi"); }
        }

        /// <summary>
        /// State layout used by the former standalone Account API executable.
        /// </summary>
        public static string LegacyRootPath
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        public static string SettingsPath { get { return Path.Combine(RootPath, "account-api-settings.json"); } }
        public static string LegacySettingsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StoreData", "account-api-settings.json"); } }
        public static string ConfigPath { get { return Path.Combine(RootPath, "config.ini"); } }
        public static string LogDirectory { get { return Path.Combine(RootPath, "logs"); } }

        public static string PathFor(params string[] segments)
        {
            var path = RootPath;
            foreach (var segment in segments ?? new string[0])
            {
                path = Path.Combine(path, segment);
            }
            return path;
        }

        public static void Initialize(IEnumerable<string> legacyDirectories = null)
        {
            lock (Gate)
            {
                if (_initialized) return;
                Directory.CreateDirectory(RootPath);

                var candidates = new List<string>
                {
                    LegacyStoreDataRootPath,
                    LegacyRootPath
                };
                if (legacyDirectories != null)
                {
                    foreach (var candidate in legacyDirectories)
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        try
                        {
                            var full = Path.GetFullPath(candidate.Trim()).TrimEnd(Path.DirectorySeparatorChar);
                            if (!candidates.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                                candidates.Add(full);
                        }
                        catch { }
                    }
                }

                foreach (var candidate in candidates)
                {
                    if (IsSamePath(candidate, RootPath)) continue;

                    if (IsSamePath(candidate, LegacyRootPath))
                    {
                        // The historical standalone layout shared the
                        // application directory, so only copy known API
                        // files instead of scanning the complete bin folder.
                        CopyFirstMissingFile(candidate, "config.ini", ConfigPath);
                        CopyFirstMissingFile(candidate, "device.identity", PathFor("device.identity"));
                        CopyFirstMissingFile(candidate, "database.config", PathFor("database.config"));
                        CopyFirstMissingFile(candidate, "database.config.previous", PathFor("database.config.previous"));
                        CopyFirstMissingFile(candidate, Path.Combine("data", "original-passwords.dat"), PathFor("data", "original-passwords.dat"));
                        CopyFirstMissingFile(candidate, Path.Combine("logs", "service.log"), PathFor("logs", "service.log"));
                        CopyFirstMissingFile(candidate, Path.Combine("logs", "audit.log"), PathFor("logs", "audit.log"));
                        continue;
                    }

                    // StoreData\\AccountApi and a user-selected legacy
                    // runtime directory are dedicated API roots, so migrate
                    // their complete contents, including all audit logs.
                    MigrateDirectory(candidate, removeSource: IsSamePath(candidate, LegacyStoreDataRootPath));
                }

                _initialized = true;
            }
        }

        private static void CopyFirstMissingFile(string directory, string relativeFile, string target)
        {
            if (File.Exists(target)) return;
            var source = Path.Combine(directory, relativeFile);
            if (!File.Exists(source)) return;
            try
            {
                var targetDirectory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                File.Copy(source, target, false);
            }
            catch
            {
                // A locked or inaccessible legacy file must not prevent the
                // embedded API from starting with fresh state.
            }
        }

        private static void MigrateDirectory(string sourceRoot, bool removeSource)
        {
            if (!Directory.Exists(sourceRoot)) return;

            var completed = true;
            try
            {
                foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = source.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var target = Path.Combine(RootPath, relative);
                    if (File.Exists(target)) continue;

                    try
                    {
                        var targetDirectory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrWhiteSpace(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                        File.Copy(source, target, false);
                    }
                    catch
                    {
                        completed = false;
                    }
                }
            }
            catch
            {
                completed = false;
            }

            if (!removeSource || !completed) return;

            try
            {
                Directory.Delete(sourceRoot, true);
            }
            catch
            {
                // Leaving a legacy copy is safer than deleting state after a
                // partially locked migration.  The new root remains primary.
            }
        }

        private static bool IsSamePath(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
