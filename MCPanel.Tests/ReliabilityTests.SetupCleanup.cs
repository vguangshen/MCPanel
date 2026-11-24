extern alias setup;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SetupApp = setup::MCPanel.Setup.App;
using UninstallRunner = setup::MCPanel.Setup.UninstallRunner;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MaintenanceCleanupWaitsForTheSetupMutexBeforeTouchingTheFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "MCPanel-Setup.exe"), "fresh copy from a running install");

        using var setupMutex = new Mutex(false, SetupApp.InstanceMutexName);
        Assert.IsTrue(setupMutex.WaitOne(TimeSpan.FromSeconds(30)), "Another setup instance holds the mutex.");
        Process? cleanup = null;
        try
        {
            // An exited process id: the script goes straight to the mutex.
            var script = UninstallRunner.BuildMaintenanceCleanupScript(int.MaxValue - 7, directory);
            cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            Assert.IsFalse(cleanup.WaitForExit(4000), "Cleanup must block while an install holds the setup mutex.");
            Assert.IsTrue(File.Exists(Path.Combine(directory, "MCPanel-Setup.exe")));
        }
        finally
        {
            setupMutex.ReleaseMutex();
        }

        try
        {
            Assert.IsTrue(cleanup!.WaitForExit(60000), "Cleanup did not finish after the mutex was released.");
            Assert.AreEqual(0, cleanup.ExitCode);
        }
        finally
        {
            cleanup?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
