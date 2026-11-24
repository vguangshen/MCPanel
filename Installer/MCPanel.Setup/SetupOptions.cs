using System;
using System.Collections.Generic;

namespace MCPanel.Setup
{
    internal enum SetupMode
    {
        Install,
        Uninstall,
        RenderPreview
    }

    /// <summary>
    /// Command line. Install switches keep the Inno Setup spelling used by
    /// release automation, CI and existing documentation:
    ///   /SILENT /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=path
    ///   /PACKAGEFILE=zip /LOG=file /RESTARTEXITCODE=n
    /// Uninstall: /UNINSTALL "install root" [/VERYSILENT | -Silent]
    /// Design preview: --render-preview "output directory"
    /// </summary>
    internal sealed class SetupOptions
    {
        public SetupMode Mode { get; private set; }
        public bool VerySilent { get; private set; }
        public bool Silent { get; private set; }
        public bool NoRestart { get; private set; }
        public string Directory { get; private set; }
        public string PackageFile { get; private set; }
        public string LogFile { get; private set; }
        public int RestartExitCode { get; private set; }
        public string UninstallRoot { get; private set; }
        public string PreviewDirectory { get; private set; }

        /// <summary>Set when an uninstaller copied itself out of the install folder.</summary>
        public bool Relocated { get; private set; }

        public bool Unattended => VerySilent || Silent;

        public static SetupOptions Parse(IList<string> arguments)
        {
            var options = new SetupOptions { Mode = SetupMode.Install, RestartExitCode = 0 };
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index] ?? string.Empty;
                var name = argument;
                string value = null;
                var equals = argument.IndexOf('=');
                if (equals > 0)
                {
                    name = argument.Substring(0, equals);
                    value = argument.Substring(equals + 1).Trim().Trim('"');
                }

                switch (name.TrimStart('/', '-').ToUpperInvariant())
                {
                    case "VERYSILENT":
                        options.VerySilent = true;
                        break;
                    case "SILENT":
                        // "-Silent" is what Windows Settings automation and the
                        // previous PowerShell uninstaller used.
                        if (options.Mode == SetupMode.Uninstall || argument.StartsWith("-", StringComparison.Ordinal))
                        {
                            options.VerySilent = true;
                        }
                        else
                        {
                            options.Silent = true;
                        }
                        break;
                    case "NORESTART":
                        options.NoRestart = true;
                        break;
                    case "SUPPRESSMSGBOXES":
                    case "SP-":
                        break;
                    case "DIR":
                        options.Directory = value;
                        break;
                    case "PACKAGEFILE":
                        options.PackageFile = value;
                        break;
                    case "LOG":
                        options.LogFile = value;
                        break;
                    case "RESTARTEXITCODE":
                        int code;
                        if (int.TryParse(value, out code) && code > 0)
                        {
                            options.RestartExitCode = code;
                        }
                        break;
                    case "UNINSTALL":
                        options.Mode = SetupMode.Uninstall;
                        if (!string.IsNullOrEmpty(value))
                        {
                            options.UninstallRoot = value;
                        }
                        else if (index + 1 < arguments.Count && !LooksLikeSwitch(arguments[index + 1]))
                        {
                            options.UninstallRoot = arguments[++index];
                        }
                        break;
                    case "RELOCATED":
                        options.Relocated = true;
                        break;
                    case "RENDER-PREVIEW":
                        options.Mode = SetupMode.RenderPreview;
                        options.PreviewDirectory = value ??
                            (index + 1 < arguments.Count ? arguments[++index] : null);
                        break;
                }
            }

            return options;
        }

        private static bool LooksLikeSwitch(string value) =>
            !string.IsNullOrEmpty(value) && (value[0] == '/' || value[0] == '-');
    }
}
