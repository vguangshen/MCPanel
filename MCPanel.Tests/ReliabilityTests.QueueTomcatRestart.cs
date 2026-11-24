using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatRestartsOnlyWhenTheQueueEmptiesAfterATomcatProductSucceeded()
    {
        var tracker = new QueueTomcatRestartTracker();

        // A batch of three: a Java product succeeds while others are still queued.
        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Completed, isTomcatProduct: true, queueHasActiveItems: true),
            "Never restart while items are still queued.");
        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Failed, isTomcatProduct: true, queueHasActiveItems: true));
        Assert.AreEqual(1, tracker.ItemFinished(ProductInstallQueueStatus.Completed, isTomcatProduct: false, queueHasActiveItems: false),
            "The last item empties the queue: restart once for the successful Tomcat product.");

        // The batch was consumed; a later batch starts from zero.
        Assert.AreEqual(0, tracker.PendingTomcatProducts);
        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Completed, isTomcatProduct: false, queueHasActiveItems: false),
            "A batch with only IIS/.NET products must not restart Tomcat.");
        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Failed, isTomcatProduct: true, queueHasActiveItems: false),
            "Failed Tomcat products must not restart Tomcat.");
        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Cancelled, isTomcatProduct: true, queueHasActiveItems: false),
            "Cancelled Tomcat products must not restart Tomcat.");

        Assert.AreEqual(0, tracker.ItemFinished(ProductInstallQueueStatus.Completed, isTomcatProduct: true, queueHasActiveItems: true));
        Assert.AreEqual(2, tracker.ItemFinished(ProductInstallQueueStatus.Completed, isTomcatProduct: true, queueHasActiveItems: false));
    }

    [DataTestMethod]
    [DataRow("Tomcat 8.5", null, true)]
    [DataRow(null, "Java", true)]
    [DataRow("tomcat", "java", true)]
    [DataRow("IIS", ".NET", false)]
    [DataRow(null, null, false)]
    public void TomcatProductsAreRecognisedByRuntimeOrLanguage(string? runEnvironment, string? devLanguage, bool expected) =>
        Assert.AreEqual(expected, QueueTomcatRestartTracker.IsTomcatProduct(runEnvironment, devLanguage));

    [TestMethod]
    public void QueueHeaderSwitchFollowsTheSettingAndShowsTheRestartOutcome()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show();
                window.Width = 1280d;
                window.Height = 800d;
                window.UpdateLayout();
                var progress = ((MainViewModel)window.DataContext).InstallationProgress;
                var toggle = (ToggleButton)window.FindName("TomcatAutoRestartToggle");
                var status = (TextBlock)window.FindName("TomcatAutoRestartStatus");
                Assert.IsNotNull(toggle);
                Assert.IsNotNull(status);

                var raised = 0;
                progress.RestartTomcatAfterQueueChanged += _ => raised++;
                progress.LoadRestartTomcatAfterQueue(true);
                Assert.AreEqual(true, toggle.IsChecked, "The switch shows the saved setting.");
                Assert.AreEqual(0, raised, "Loading the saved value must not save it again.");

                progress.LoadRestartTomcatAfterQueue(false);
                Assert.AreEqual(false, toggle.IsChecked);

                Assert.AreEqual(Visibility.Collapsed, progress.TomcatRestartStatusVisibility);
                progress.TomcatRestartStatusText = "队列完成后已自动重启 Tomcat。";
                Assert.AreEqual(Visibility.Visible, progress.TomcatRestartStatusVisibility);
                Assert.AreEqual("队列完成后已自动重启 Tomcat。", status.Text);
                Assert.IsTrue(toggle.ToolTip is string tip && tip.Contains("Tomcat 未运行时不会自动启动"),
                    "The switch explains exactly when it restarts Tomcat.");
            }
            finally { window.Close(); }
        });
    }
}
