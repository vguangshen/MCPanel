using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ApplicationUpdatePreauthorizesElevationAfterExistingConfirmation()
    {
        Assert.IsTrue(ApplicationUpdateService.ShouldRequestPreauthorizedUpdater(isAdministrator: false));
        Assert.IsFalse(ApplicationUpdateService.ShouldRequestPreauthorizedUpdater(isAdministrator: true));

        var settings = ReadRepositoryFile("MainWindow.Settings.cs").Replace("\r\n", "\n");
        StringAssert.Contains(settings, "if (!ConfirmOnlineUpdate(update.Manifest)) return null;\n                if (!RequestApplicationUpdateAuthorizationAfterConfirmation()) return null;");
        StringAssert.Contains(settings, "if (!ConfirmOnlineUpdate(manifest)) return null;\n            if (!RequestApplicationUpdateAuthorizationAfterConfirmation()) return null;");

        var app = ReadRepositoryFile("App.xaml.cs");
        StringAssert.Contains(app, "--wait-update-plan");
        var updater = ReadRepositoryFile("ApplicationUpdateService.cs");
        StringAssert.Contains(updater, "elevated: true");
        StringAssert.Contains(updater, "WaitForAuthorizedUpdatePlanAsync");
    }

    [TestMethod]
    public void ApplicationUpdateCanPrepareWhileProductDownloadsRemainActive()
    {
        var settings = ReadRepositoryFile("MainWindow.Settings.cs");
        var canStartIndex = settings.IndexOf("private bool CanStartApplicationUpdate()", StringComparison.Ordinal);
        var nextMethodIndex = settings.IndexOf("private bool RequestApplicationUpdateAuthorizationAfterConfirmation()", canStartIndex, StringComparison.Ordinal);
        Assert.IsTrue(canStartIndex >= 0 && nextMethodIndex > canStartIndex);
        var canStartBody = settings.Substring(canStartIndex, nextMethodIndex - canStartIndex);
        Assert.IsFalse(canStartBody.Contains("_productInstallQueue.HasActiveItems", StringComparison.Ordinal));
        StringAssert.Contains(settings, "WaitForProductInstallQueueBeforeApplyingUpdateAsync");
        StringAssert.Contains(settings, "当前产品下载/安装继续运行");
    }
}
