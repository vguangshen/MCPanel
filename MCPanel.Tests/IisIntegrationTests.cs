using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

[TestClass]
public sealed class IisIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("IisIntegration")]
    public void RealIisInstallationFailurePreservesCoreAndRepairIsRepeatable()
    {
        // This modifies IIS and must run only on an explicitly opted-in disposable VM.
        if (Environment.GetEnvironmentVariable("MCPANEL_IIS_INTEGRATION") != "1")
            Assert.Inconclusive("Requires a disposable elevated Windows CI runner.");

        var root = Path.Combine(Path.GetTempPath(), "MCPanel.IisIntegration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingMsi = Path.Combine(root, "missing.msi");
        try
        {
            var failed = RunScript(root, "install-without-rewrite", EnvironmentInstaller.BuildIisScript(missingMsi));
            Assert.AreNotEqual(0, failed, "A missing Rewrite installer must fail the configuration step.");
            Assert.IsFalse(EnvironmentInstaller.HasIisInstallContinuation,
                "This runner requires a reboot; it cannot verify the configuration recovery path.");
            EnvironmentRuntimeService.InvalidateIisModuleProbe();
            var partial = new EnvironmentRuntimeService().GetState(EnvironmentKind.Iis);
            Assert.IsTrue(partial.IsInstalled, partial.StatusText);
            Assert.IsTrue(partial.NeedsConfigurationRepair, partial.StatusText);
            Assert.AreEqual(false, EnvironmentRuntimeService.TryProbeIisRewriteModule());
            Assert.AreEqual(true, EnvironmentRuntimeService.TryProbeIisDefaultDocuments());

            var msi = Path.Combine(root, "rewrite.msi");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new WebClient())
                client.DownloadFile("https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi", msi);
            Assert.AreEqual(0, RunScript(root, "repair", EnvironmentInstaller.BuildIisScript(msi, repairOnly: true)));
            AssertComplete();
            // No installer is available on the second repair: the registered module must be reused.
            Assert.AreEqual(0, RunScript(root, "repeat-repair", EnvironmentInstaller.BuildIisScript(missingMsi, repairOnly: true)));
            AssertComplete();
            using var http = new WebClient();
            StringAssert.Contains(http.DownloadString("http://localhost/"), "IIS");
        }
        finally
        {
            // Preserve the generated transcript in the test results before cleaning temporary scripts.
            var transcript = Path.Combine(ComponentPaths.WorkRoot, "install-iis.log");
            if (File.Exists(transcript)) TestContext.AddResultFile(transcript);
            Directory.Delete(root, true);
        }
    }

    private static void AssertComplete()
    {
        EnvironmentRuntimeService.InvalidateIisModuleProbe();
        var state = new EnvironmentRuntimeService().GetState(EnvironmentKind.Iis);
        Assert.IsTrue(state.IsInstalled, state.StatusText);
        Assert.IsTrue(state.IsRunning, state.StatusText);
        Assert.IsFalse(state.NeedsConfigurationRepair, state.StatusText);
        Assert.AreEqual(true, EnvironmentRuntimeService.TryProbeIisRewriteModule());
        Assert.AreEqual(true, EnvironmentRuntimeService.TryProbeIisDefaultDocuments());
    }

    private int RunScript(string root, string name, string script)
    {
        var path = Path.Combine(root, name + ".ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        Assert.IsNotNull(process);
        var stdout = process!.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15 * 60 * 1000))
        {
            process.Kill();
            Assert.Fail(name + " timed out.");
        }
        TestContext.WriteLine(name + " stdout:\n" + stdout.GetAwaiter().GetResult());
        TestContext.WriteLine(name + " stderr:\n" + stderr.GetAwaiter().GetResult());
        return process.ExitCode;
    }
}
