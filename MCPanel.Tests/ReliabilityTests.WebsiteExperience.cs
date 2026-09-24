using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Specialized;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void CorruptManualRecordsCannotBeOverwrittenAndBackupCanBeRecovered()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "platforms.json");
            var store = new ManualPlatformStore(path);
            store.Save(new("old", "原平台", root, false));
            File.WriteAllText(path, "[{\"Id\":\"old\"");
            var corrupt = File.ReadAllBytes(path);
            Assert.ThrowsException<IOException>(() => store.Load());
            Assert.ThrowsException<IOException>(() => store.Save(new("new", "新平台", root, false)));
            Assert.ThrowsException<IOException>(() => store.Remove("old"));
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(path));
            store.RestoreBackup();
            Assert.AreEqual("old", store.Load().Single().Id);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(path + ".before-recovery"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidBackupLeavesManualRecordsUntouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "platforms.json");
            var store = new ManualPlatformStore(path);
            store.Save(new("old", "原平台", root, false));
            var original = File.ReadAllBytes(path);
            File.WriteAllText(path + ".bak", "null");
            Assert.ThrowsException<System.Text.Json.JsonException>(() => store.RestoreBackup());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EditingLocalPlatformPreservesIdentityAndSoftwareFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "index.html"), "first"); File.WriteAllText(Path.Combine(second, "index.html"), "second");
            var store = new ManualPlatformStore(Path.Combine(root, "state.json"));
            var original = store.Prepare("原名称", first, false); store.Save(original);
            var edited = store.Prepare("新的名称", second, false, original.Id, architecture: "32", iisRuntime: "v2.0"); store.Save(edited);
            Assert.AreEqual(original.Id, store.Load().Single().Id);
            Assert.AreEqual("新的名称", store.Load().Single().Name);
            Assert.AreEqual("32", edited.ToProduct().SysType);
            Assert.AreEqual("v2.0", edited.ToProduct().ExternalIisRuntime);
            Assert.IsTrue(File.Exists(Path.Combine(first, "index.html")));
            Assert.IsTrue(File.Exists(Path.Combine(second, "index.html")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void JavaRootTakesPrecedenceOverNestedBackupWar()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "WEB-INF")); Directory.CreateDirectory(Path.Combine(root, "backups"));
            File.WriteAllText(Path.Combine(root, "backups", "old.war"), "old");
            Assert.AreEqual(root, ProductDeploymentService.FindTomcatDocBase(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleJavaTargetsRequireExplicitSelectionAndPersistIt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(root, "a.war"); var second = Path.Combine(root, "b.war");
            File.WriteAllText(first, "a"); File.WriteAllText(second, "b");
            var store = new ManualPlatformStore(Path.Combine(root, "state.json"));
            Assert.AreEqual(2, LocalPlatformInspection.FindJavaTargets(root).Count);
            Assert.ThrowsException<InvalidDataException>(() => store.Prepare("本地 Java", root, true));
            var definition = store.Prepare("本地 Java", root, true, javaDocBase: second); store.Save(definition);
            Assert.AreEqual(second, store.Load().Single().ToProduct().ExternalJavaDocBase);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void JavaDiscoverySkipsBackupTreesAndSupportsCancellation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "backups")); Directory.CreateDirectory(Path.Combine(root, "app", "WEB-INF"));
            File.WriteAllText(Path.Combine(root, "backups", "old.war"), "old");
            Assert.AreEqual(Path.Combine(root, "app"), LocalPlatformInspection.FindJavaTargets(root).Single());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => LocalPlatformInspection.FindJavaTargets(root, cancelled.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LocalDotNetArchitectureIsDetectedOnceAndHonorsOverride()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // A minimal native PE header is sufficient for architecture detection; no code is executed.
            using (var writer = new BinaryWriter(File.Create(Path.Combine(root, "native.dll"))))
            {
                writer.Write((ushort)0x5a4d); writer.BaseStream.Position = 0x3c; writer.Write(128);
                writer.BaseStream.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664);
            }
            var store = new ManualPlatformStore(Path.Combine(root, "state.json"));
            var automatic = store.Prepare("x64 平台", root, false);
            Assert.AreEqual("64", automatic.ToProduct().SysType);
            var explicit32 = store.Prepare("x86 平台", root, false, architecture: "32");
            Assert.AreEqual("32", explicit32.ToProduct().SysType);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WebsiteRefreshSuspendsWhenHiddenOrEmptyAndNeverOverlaps()
    {
        var schedule = new WebsiteRefreshSchedule(); var now = DateTime.UtcNow;
        Assert.IsFalse(schedule.TryStart(now, false, false, true));
        Assert.IsFalse(schedule.TryStart(now, true, true, true));
        Assert.IsFalse(schedule.TryStart(now, true, false, false));
        Assert.IsTrue(schedule.TryStart(now, true, false, true));
        schedule.RequestRefresh();
        Assert.IsFalse(schedule.TryStart(now.AddSeconds(90), true, false, true));
        schedule.Complete(now);
        for (var second = 0; second < 30; second++) Assert.IsFalse(schedule.TryStart(now.AddSeconds(second), true, false, true));
        Assert.IsTrue(schedule.TryStart(now.AddSeconds(30), true, false, true));
    }

    [TestMethod]
    public void RunningSharedIisSiteDoesNotHideStoppedApplicationPool()
    {
        var probe = IisWebsiteStatusProbe.Parse("<appcmd><SITE SITE.NAME='MCPanel' state='Started'/></appcmd>",
            "<appcmd><APPPOOL APPPOOL.NAME='good' state='Started'/><APPPOOL APPPOOL.NAME='bad' state='Stopped'/></appcmd>");
        Assert.IsTrue(probe.Get("MCPanel", "good", "/good").Running);
        Assert.IsFalse(probe.Get("MCPanel", "bad", "/bad").Running);
        Assert.AreEqual("应用程序池已停止", probe.Get("MCPanel", "bad", "/bad").Text);
        Assert.IsFalse(probe.Get("MCPanel", "missing").Running);
    }

    [TestMethod]
    public void LargeWebsiteListVirtualizesAndSupportsFiltersAndScroll()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show(); var model = (MainViewModel)window.DataContext;
                model.RefreshCustomWebsites(Enumerable.Range(0, 1000).Select(index => new CustomWebsiteDefinition
                    { Name = "测试网站 " + index, PhysicalPath = @"Z:\Missing\site" + index }).ToArray());
                ((Grid)window.FindName("SitesPage")).Visibility = Visibility.Visible;
                window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var list = (ListBox)window.FindName("WebsiteListScroll");
                var realized = Enumerable.Range(0, 1000).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.IsTrue(realized > 0 && realized < 100, $"1000 个网站仅应生成屏幕附近条目，实际 {realized}。");
                model.WebsiteTypeFilter = 1; Assert.IsTrue(model.WebsiteView.IsEmpty);
                model.WebsiteTypeFilter = 0; model.WebsiteSearchKeyword = "测试网站 998";
                var narrowed = model.WebsiteView.Cast<object>().Cast<WebsiteRow>().ToArray();
                Assert.IsTrue(narrowed.Length > 0 && narrowed.Length < 100, $"关键词筛选应显著缩小列表，实际 {narrowed.Length} 项。");
                Assert.IsTrue(narrowed.Any(row => row.Name == "测试网站 998"));
                model.WebsiteSearchKeyword = ""; list.ScrollIntoView(model.WebsiteRows[900]); window.UpdateLayout();
                Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(model.WebsiteRows[900]));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DialogDropdownUsesThemeColorsWhenActuallyExpanded()
    {
        RunWebsiteUiTest(() =>
        {
            foreach (var dark in new[] { false, true })
            {
                var dialog = new CustomWebsiteDialog(null);
                try
                {
                    dialog.ApplyTheme(dark); dialog.Show(); dialog.UpdateLayout();
                    var combo = (ComboBox)dialog.FindName("RuntimeBox"); combo.IsDropDownOpen = true;
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    var border = (Border)popup.Child;
                    var expected = ((SolidColorBrush)dialog.FindResource("InputBrush")).Color;
                    Assert.AreEqual(expected, ((SolidColorBrush)border.Background).Color);
                    Assert.IsTrue(border.ActualWidth >= combo.ActualWidth - 2);
                    var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
                    Assert.AreEqual(((SolidColorBrush)dialog.FindResource("TextBrush")).Color, ((SolidColorBrush)item.Foreground).Color);
                    Assert.AreNotEqual(((SolidColorBrush)item.Foreground).Color, expected);
                }
                finally { dialog.Close(); }
            }
        });
    }

    [TestMethod]
    public void AiProviderSettingsReadConfiguredValues()
    {
        var values = new NameValueCollection
        {
            [AiProviderSettings.ProviderConfigKey] = "Local",
            [AiProviderSettings.EndpointConfigKey] = "http://localhost:8080/v1/chat/completions",
            [AiProviderSettings.ModelConfigKey] = "test-model",
            [AiProviderSettings.ApiKeyConfigKey] = "test-key"
        };

        var settings = AiProviderSettings.FromAppSettings(values);
        Assert.AreEqual("Local", settings.Provider);
        Assert.AreEqual("http://localhost:8080/v1/chat/completions", settings.Endpoint);
        Assert.AreEqual("test-model", settings.Model);
        Assert.AreEqual("test-key", settings.ApiKey);
    }

    [TestMethod]
    public async Task FailedManualBindingRestoresPreviousRecordAndRemovesNewRecord()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new ManualPlatformStore(Path.Combine(root, "manual-platforms.json"));
            var previous = new ManualPlatformDefinition("existing", "原平台", root, false);
            store.Save(previous);

            foreach (var definition in new[]
                     {
                         previous with { Name = "未完成的修改" },
                         new ManualPlatformDefinition("new", "未完成的新平台", root, false)
                     })
            {
                try
                {
                    await store.ApplyWithRollbackAsync(
                        definition,
                        () => Task.FromException<string>(new InvalidOperationException("模拟绑定失败")));
                    Assert.Fail("运行绑定失败时不得保留新平台记录。");
                }
                catch (InvalidOperationException ex)
                {
                    Assert.AreEqual("模拟绑定失败", ex.Message);
                }
            }

            var records = store.Load();
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(previous, records[0]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
