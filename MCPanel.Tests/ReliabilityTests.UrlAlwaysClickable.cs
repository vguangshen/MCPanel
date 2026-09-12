using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void InstalledWebsiteUrlOpeningDoesNotDependOnRuntimeStatus()
    {
        var source = ReadRepositoryFile("MainWindow.Products.cs");
        var start = source.IndexOf("private void InstalledProductBrowse_Click", StringComparison.Ordinal);
        var end = source.IndexOf("internal void InstalledProductManageToggle_Click", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var block = source.Substring(start, end - start);

        StringAssert.Contains(block, "!string.IsNullOrWhiteSpace(item.Url)");
        StringAssert.Contains(block, "Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true })");
        Assert.IsFalse(block.Contains("item.CanBrowse", StringComparison.Ordinal));
    }
}
