using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void PathUtilComparesPathsCaseInsensitivelyIgnoringTrailingSeparators()
    {
        Assert.IsTrue(PathUtil.PathsEqual(@"C:\MCPanel\Data", @"c:\mcpanel\data\"));
        Assert.IsTrue(PathUtil.PathsEqual(@"C:\MCPanel\.\Data", @"C:\MCPanel\Data"));
        Assert.IsTrue(PathUtil.PathsEqual(@"C:\", @"C:\"));
        Assert.IsFalse(PathUtil.PathsEqual(@"C:\MCPanel", @"C:\MCPanel2"));
        Assert.IsFalse(PathUtil.PathsEqual(null, @"C:\MCPanel"));
        Assert.IsFalse(PathUtil.PathsEqual(string.Empty, string.Empty));
        Assert.IsFalse(PathUtil.PathsEqual(@"C:\bad|name", @"C:\bad|name"));
    }

    [TestMethod]
    public void PathUtilIsPathInsideRequiresAStrictChildOnASeparatorBoundary()
    {
        Assert.IsTrue(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel\Downloads\a.zip"));
        Assert.IsTrue(PathUtil.IsPathInside(@"C:\MCPanel\", @"c:\mcpanel\x"));
        Assert.IsTrue(PathUtil.IsPathInside(@"C:\", @"C:\MCPanel"));
        Assert.IsFalse(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel"));
        Assert.IsFalse(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel\"));
        Assert.IsFalse(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel2\x"));
        Assert.IsFalse(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel\..\Other"));
        Assert.IsFalse(PathUtil.IsPathInside(null, @"C:\MCPanel\x"));
        Assert.IsFalse(PathUtil.IsPathInside(@"C:\MCPanel", @"C:\MCPanel\bad|name"));
    }

    [TestMethod]
    public void PathUtilSafeProductNameMatchesTheHistoricalServiceAndFolderNames()
    {
        Assert.AreEqual("erp-web_1.0", PathUtil.SafeProductName("erp-web_1.0"));
        Assert.AreEqual("a_b", PathUtil.SafeProductName("a b"));
        Assert.AreEqual("a__b", PathUtil.SafeProductName("a/:b"));
        Assert.AreEqual("库存", PathUtil.SafeProductName("库存"));
        Assert.AreEqual("product", PathUtil.SafeProductName("///"));
        Assert.AreEqual("product", PathUtil.SafeProductName(null));
        Assert.AreEqual(PathUtil.SafeProductName("x y"), ProductInstallPathResolver.SafeName("x y"));
    }

    [TestMethod]
    public void PathUtilEscapesPowerShellLiteralsAndDeletesBestEffort()
    {
        Assert.AreEqual(@"C:\O''Brien\x", PathUtil.EscapePowerShellLiteral(@"C:\O'Brien\x"));

        var file = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "delete-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        PathUtil.TryDeleteFile(file);
        Assert.IsFalse(File.Exists(file));
        PathUtil.TryDeleteFile(file);
        PathUtil.TryDeleteFile(null);
        PathUtil.TryDeleteFile(@"C:\bad|name");
    }

    [TestMethod]
    public void SystemProbeRegistryViewsAreDistinctAndNativeFirst()
    {
        var views = SystemProbe.RegistryViewsWithDefault().ToArray();
        CollectionAssert.AllItemsAreUnique(views);
        Assert.AreEqual(Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Registry32, views[0]);
        Assert.IsNotNull(SystemProbe.GetActiveTcpPorts());
    }
}
