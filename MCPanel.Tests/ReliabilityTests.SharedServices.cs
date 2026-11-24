using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ServiceHostArgumentsAcceptOnlyAnExistingRootWithItsRequiredFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string argument = "--nginx-service";
            Assert.IsTrue(ServiceHostArguments.IsRequest(new[] { "--NGINX-SERVICE", root }, argument));
            Assert.IsFalse(ServiceHostArguments.IsRequest(new[] { "--tray" }, argument));

            Assert.IsFalse(ServiceHostArguments.TryGetRoot(new[] { argument, root }, argument, "nginx.exe", out _),
                "A root without nginx.exe must not start the Nginx service host.");
            File.WriteAllText(Path.Combine(root, "nginx.exe"), string.Empty);
            Assert.IsTrue(ServiceHostArguments.TryGetRoot(new[] { argument, root + "\\" }, argument, "nginx.exe", out var resolved));
            Assert.AreEqual(Path.GetFullPath(root).TrimEnd('\\'), resolved, "The trailing separator is trimmed.");

            Assert.IsFalse(ServiceHostArguments.TryGetRoot(new[] { argument }, argument, null, out _));
            Assert.IsFalse(ServiceHostArguments.TryGetRoot(new[] { argument, Path.Combine(root, "missing") }, argument, null, out _));
            Assert.IsFalse(ServiceHostArguments.TryGetRoot(new[] { argument, "bad|path" }, argument, null, out _));
            Assert.AreEqual(2, ServiceHostArguments.Run(new[] { argument }, argument, _ => throw new AssertFailedException("must not start")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void WatchdogRecoveryBudgetForgetsRecoveriesOlderThanTheWindow()
    {
        var budget = new WatchdogRecoveryBudget();
        var start = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < WatchdogRecoveryBudget.MaxRecoveriesPerWindow; index++)
        {
            Assert.AreEqual(index, budget.AttemptsInWindow(start.AddMinutes(index)));
            budget.Record(start.AddMinutes(index));
        }

        Assert.IsTrue(WatchdogRecoveryBudget.ShouldEscalate(budget.AttemptsInWindow(start.AddMinutes(5))));
        Assert.AreEqual(3, budget.AttemptsInWindow(start.AddMinutes(12)), "Recoveries older than 10 minutes no longer count.");
        Assert.AreEqual(0, budget.AttemptsInWindow(start.AddHours(1)));

        // FRP and Nginx keep exposing the same policy through their own names.
        Assert.AreEqual(FrpWindowsService.GetRecoveryDelay(2), NginxWindowsService.GetRecoveryDelay(2));
        Assert.AreEqual(FrpWindowsService.MaxRecoveriesPerWindow, NginxWindowsService.MaxWatchdogRecoveriesPerWindow);
    }

    [TestMethod]
    public void ServiceCommandFailuresCarryTheExitCodeForAccessDeniedChecks()
    {
        Assert.IsTrue(ManagedWindowsServiceController.IsAccessDenied(new ServiceCommandException(5, "启动服务失败")));
        Assert.IsTrue(NginxWindowsServiceManager.IsAccessDenied(new InvalidOperationException("[SC] OpenService 失败 5: 拒绝访问。")));
        Assert.IsFalse(ManagedWindowsServiceController.IsAccessDenied(new ServiceCommandException(1060, "服务不存在")));
        Assert.IsInstanceOfType(new ServiceCommandException(1, "x"), typeof(InvalidOperationException),
            "Existing catch (InvalidOperationException) handlers must keep working.");
    }

    [TestMethod]
    public void PanelLogWritesOneTimestampedFormatAndNeverThrows()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var log = Path.Combine(root, "nested", "panel.log");
            PanelLog.Write(log, "第一条");
            PanelLog.Write(log, "第二条", new InvalidOperationException("boom"));
            var lines = File.ReadAllLines(log);
            StringAssert.Matches(lines[0], new System.Text.RegularExpressions.Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] 第一条$"));
            StringAssert.EndsWith(lines[1], "] 第二条");
            Assert.IsTrue(lines.Any(line => line.Contains("InvalidOperationException: boom")));
            Assert.AreEqual(string.Empty, lines.Last(), "An entry with an exception ends with a blank line.");

            PanelLog.Write("bad|path\\x.log", "ignored");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
