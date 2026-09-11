using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ProductTomcatStartAndCatalinaSuccessStayNonModal()
    {
        var source = ReadRepositoryFile("MainWindow.Products.cs");
        var start = source.IndexOf("internal async void InstalledProductTomcatAction_Click", StringComparison.Ordinal);
        var end = source.IndexOf("internal void InstalledProductIis_Click", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var handler = source.Substring(start, end - start);

        StringAssert.Contains(handler, "catalinaMode: false");
        StringAssert.Contains(handler, "catalinaMode: true");
        StringAssert.Contains(handler, "界面操作完成：{message}");
        Assert.IsFalse(handler.Contains("MessageBox.Show(message, \"Tomcat 应用\"", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("MessageBoxImage.Information", StringComparison.Ordinal));
        StringAssert.Contains(handler, "MessageBoxImage.Warning");
    }

    [TestMethod]
    public void EnvironmentRuntimeStartStopRestartAndCatalinaDoNotShowSuccessDialog()
    {
        var source = ReadRepositoryFile("MainWindow.Environment.cs");
        var start = source.IndexOf("internal async void EnvironmentRuntime_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<string> ExecuteFrpRuntimeActionAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var handler = source.Substring(start, end - start);

        StringAssert.Contains(handler, "\"Start\" => await _runtimeService.StartAsync(item.Kind)");
        StringAssert.Contains(handler, "\"Stop\" => await _runtimeService.StopAsync(item.Kind)");
        StringAssert.Contains(handler, "\"Restart\" => await _runtimeService.RestartAsync(item.Kind)");
        StringAssert.Contains(handler, "\"CatalinaRun\" => await _runtimeService.StartTomcatInCatalinaConsoleAsync()");

        const string successPopup = "MessageBox.Show(message, \"环境\", MessageBoxButton.OK, MessageBoxImage.Information);";
        Assert.AreEqual(1, CountOccurrences(handler, successPopup));
        var uninstallGuard = handler.IndexOf("if (action == \"Uninstall\")", StringComparison.Ordinal);
        var popup = handler.IndexOf(successPopup, StringComparison.Ordinal);
        Assert.IsTrue(uninstallGuard >= 0 && popup > uninstallGuard,
            "环境运行操作的成功弹窗只能保留给卸载结果，启动/停止/重启/Catalina 不应弹窗。");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
