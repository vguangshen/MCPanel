extern alias setup;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SetupConfig = setup::MCPanel.Setup.SetupConfig;
using SetupLog = setup::MCPanel.Setup.SetupLog;
using SetupMode = setup::MCPanel.Setup.SetupMode;
using SetupOptions = setup::MCPanel.Setup.SetupOptions;
using InstallEngine = setup::MCPanel.Setup.InstallEngine;
using SystemRegistration = setup::MCPanel.Setup.SystemRegistration;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private const string SetupConfigJson = """
        {
          "version": "9.8.7",
          "packageUrl": "https://example.test/MCPanel-9.8.7.zip",
          "packageSha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "requiredSpaceBytes": 104857600,
          "maximumRelativeLength": 40,
          "dotNet": { "name": ".NET Framework 4.8", "minimumRelease": 394802, "url": "https://example.test/dotnet.exe", "sha256": "00", "arguments": "/q" },
          "visualCpp": { "name": "VC++", "minimumVersion": "10.0.40219.325", "url": "https://example.test/vc.exe", "sha256": "00", "arguments": "/q" },
          "programFiles": [ "MCPanel.exe.config", "MCPanel.exe" ],
          "preservedTopLevelNames": [ "StoreData" ],
          "preservedAppSettingKeys": [ "Ai.Model", "Ai.ApiKey", "Environment.SqlServer.Sql2017ExpressUrl" ],
          "urlMigrations": [ { "Key": "Environment.SqlServer.Sql2017ExpressUrl", "PreviousUrl": "https://old.example.test/sql.exe", "CurrentUrl": "https://new.example.test/sql.exe" } ]
        }
        """;

    private static SetupConfig LoadSetupConfig()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SetupConfigJson));
        return SetupConfig.Load(stream);
    }

    [TestMethod]
    public void SetupOptionsKeepInnoSwitchesAndParseUninstall()
    {
        var install = SetupOptions.Parse(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=D:\\中文 安装", "/PACKAGEFILE=C:\\a.zip", "/LOG=C:\\s.log", "/RESTARTEXITCODE=3010" });
        Assert.AreEqual(SetupMode.Install, install.Mode);
        Assert.IsTrue(install.VerySilent && install.NoRestart);
        Assert.AreEqual("D:\\中文 安装", install.Directory);
        Assert.AreEqual("C:\\a.zip", install.PackageFile);
        Assert.AreEqual(3010, install.RestartExitCode);

        // Exactly what Windows Settings automation runs: UninstallString + " -Silent".
        var uninstall = SetupOptions.Parse(new[] { "/UNINSTALL", "D:\\中文 安装", "-Silent" });
        Assert.AreEqual(SetupMode.Uninstall, uninstall.Mode);
        Assert.AreEqual("D:\\中文 安装", uninstall.UninstallRoot);
        Assert.IsTrue(uninstall.VerySilent);

        var interactive = SetupOptions.Parse(new[] { "/UNINSTALL", "D:\\MCPanel" });
        Assert.IsFalse(interactive.Unattended);

        var preview = SetupOptions.Parse(new[] { "--render-preview", "C:\\out" });
        Assert.AreEqual(SetupMode.RenderPreview, preview.Mode);
        Assert.AreEqual("C:\\out", preview.PreviewDirectory);
    }

    [TestMethod]
    public void SetupUninstallCommandMatchesPanelRegistration()
    {
        // The panel refreshes the uninstall entry on startup; both sides must agree.
        const string executable = @"C:\ProgramData\MCPanel.Setup\MCPanel-Setup.exe";
        Assert.AreEqual(
            SystemRegistration.BuildUninstallCommand(executable, @"D:\中文 安装\", silent: false),
            InstallerRegistration.BuildSetupCommand(executable, @"D:\中文 安装\", silent: false));
        Assert.AreEqual(
            SystemRegistration.BuildUninstallCommand(executable, @"D:\MCPanel", silent: true),
            InstallerRegistration.BuildSetupCommand(executable, @"D:\MCPanel", silent: true));
    }

    [TestMethod]
    public void SetupMergesPreservedSettingsAndMigratesRetiredUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(root, "MCPanel.exe.config"),
                "<configuration><appSettings>" +
                "<add key=\"Ai.Model\" value=\"保留原配置\" />" +
                "<add key=\"Ai.ApiKey\" value=\"\" />" +
                "<add key=\"Environment.SqlServer.Sql2017ExpressUrl\" value=\"https://old.example.test/sql.exe\" />" +
                "<add key=\"NotPreserved\" value=\"old\" />" +
                "</appSettings></configuration>", new UTF8Encoding(false));
            var incoming = Path.Combine(root, "incoming.config");
            File.WriteAllText(incoming,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><appSettings>" +
                "<add key=\"Ai.Model\" value=\"default\" />" +
                "<add key=\"Ai.ApiKey\" value=\"default-key\" />" +
                "<add key=\"NotPreserved\" value=\"new\" />" +
                "</appSettings></configuration>", new UTF8Encoding(false));

            var engine = new InstallEngine(LoadSetupConfig(), new SetupLog(Path.Combine(root, "setup.log")), null);
            engine.MergeOriginalConfiguration(root, incoming);

            var settings = XDocument.Load(incoming).Descendants("add")
                .ToDictionary(element => (string)element.Attribute("key"), element => (string)element.Attribute("value"));
            Assert.AreEqual("保留原配置", settings["Ai.Model"]);
            Assert.AreEqual(string.Empty, settings["Ai.ApiKey"], "Explicitly empty settings stay empty.");
            Assert.AreEqual("https://new.example.test/sql.exe", settings["Environment.SqlServer.Sql2017ExpressUrl"]);
            Assert.AreEqual("new", settings["NotPreserved"]);

            // A missing executable means an interrupted update: prefer the rollback copy.
            File.Delete(Path.Combine(root, "MCPanel.exe"));
            var rollback = Path.Combine(root, "StoreData", "Updates", "Rollback", "Current");
            Directory.CreateDirectory(rollback);
            File.WriteAllText(Path.Combine(rollback, "MCPanel.exe.config"),
                "<configuration><appSettings><add key=\"Ai.Model\" value=\"from-rollback\" /></appSettings></configuration>");
            File.WriteAllText(Path.Combine(root, "MCPanel.exe.config"), "<configuration />");
            engine.MergeOriginalConfiguration(root, incoming);
            Assert.AreEqual("from-rollback", XDocument.Load(incoming).Descendants("add")
                .First(element => (string)element.Attribute("key") == "Ai.Model").Attribute("value")!.Value);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void SetupRejectsUnsafeInstallDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new InstallEngine(LoadSetupConfig(), new SetupLog(Path.Combine(root, "setup.log")), null);
            Assert.ThrowsException<InvalidOperationException>(() => engine.ValidateInstallDirectory(Path.GetPathRoot(root)!));
            Assert.ThrowsException<InvalidOperationException>(() =>
                engine.ValidateInstallDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MCPanel")));

            var foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "other.txt"), "x");
            Assert.ThrowsException<InvalidOperationException>(() => engine.ValidateInstallDirectory(foreign));

            var existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "MCPanel.exe"), "x");
            Assert.AreEqual(existing, engine.ValidateInstallDirectory(existing + "\\"));
            Assert.AreEqual(Path.Combine(root, "fresh"), engine.ValidateInstallDirectory(Path.Combine(root, "fresh")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
