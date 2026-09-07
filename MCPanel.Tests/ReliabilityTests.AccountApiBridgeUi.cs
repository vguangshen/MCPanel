using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void AccountApiBridgeWithoutCloudProfilesRemainsOnlineAndHasNoHeaderBadge()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = CreateTemporaryDirectory();
            MainWindow? window = null;
            try
            {
                var config = Path.Combine(root, "config.ini");
                File.WriteAllText(config, "; UI fixture");
                window = CreateUiTestWindow();
                window.Show();
                ((FrameworkElement)window.FindName("HomePage")).Visibility = Visibility.Collapsed;
                var page = (AccountApiPage)window.FindName("AccountApiPageControl");
                page.Visibility = Visibility.Visible;
                page.ApplySnapshot(new AccountApiSnapshot
                {
                    Enabled = true, Reachable = true, Healthy = false, HealthStatus = "setup_required",
                    Version = "2.0.2（内置）",
                    Configuration = new AccountApiConfiguration { ConfigPath = config, RuntimeDirectory = root },
                    CheckedAt = DateTimeOffset.Now
                });
                Assert.IsNull(page.FindName("ServiceStatusBadge"));
                Assert.AreEqual("本地桥接在线", page.RuntimeStateText);
                Assert.AreEqual("云端管理", page.DatabaseSummaryText);
                Assert.AreEqual("Healthy", page.ServiceStatusKind);
                Assert.AreEqual(Visibility.Visible, page.ProviderEmptyVisibility);
                window.Width = 1280;
                window.Height = 820;
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1280, 820, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "audit"));
                Directory.CreateDirectory(output);
                using var stream = File.Create(Path.Combine(output, "account-api-bridge.png"));
                encoder.Save(stream);
                page.ApplySnapshot(new AccountApiSnapshot { Enabled = false });
                Assert.AreEqual("未启用", page.RuntimeStateText);
                page.ApplySnapshot(new AccountApiSnapshot { Enabled = true, Reachable = false });
                Assert.AreNotEqual("本地桥接在线", page.RuntimeStateText);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); DeleteTemporaryTree(root); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
