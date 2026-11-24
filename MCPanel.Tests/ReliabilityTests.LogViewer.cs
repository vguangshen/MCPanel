using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void PanelLogsAreListedNewestFirstAndReadFromTheirEnd()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var work = Path.Combine(root, "StoreData", "Work");
            var logs = Path.Combine(root, "StoreData", "Logs", "2026");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(logs);
            var older = Path.Combine(work, "environment-install.log");
            var newer = Path.Combine(logs, "panel.log");
            File.WriteAllText(older, "older entry");
            File.WriteAllText(Path.Combine(work, "notes.json"), "not a log");
            var large = new StringBuilder();
            large.Append("FIRST-LINE\n");
            while (large.Length < LogBrowser.TailBytes + 4096) large.Append("中文日志 filler line\n");
            large.Append("LAST-LINE");
            File.WriteAllText(newer, large.ToString(), new UTF8Encoding(false));
            File.SetLastWriteTime(older, DateTime.Now.AddMinutes(-5));

            var files = new LogBrowser(root).ListFiles(LogModule.Panel);
            CollectionAssert.AreEqual(new[] { newer, older }, files.Select(file => file.Path).ToArray(),
                "Only log files are listed, the most recently written first.");
            var tail = LogBrowser.ReadLatest(newer);
            Assert.IsTrue(tail.EndsWith("LAST-LINE", StringComparison.Ordinal), "The newest entries must be shown.");
            Assert.IsFalse(tail.Contains("FIRST-LINE"), "Only the end of a large log is read.");
            StringAssert.Contains(tail, "中文日志", "UTF-8 logs must decode without mojibake.");
            Assert.AreEqual(string.Empty, LogBrowser.ReadLatest(Path.Combine(work, "missing.log")));
            Assert.AreEqual(0, new LogBrowser(root).ListFiles(LogModule.Frp).Count);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void ChoosingAModuleShowsItsNewestLogAndKeepsAPickedFileOnRefresh()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var work = Path.Combine(root, "StoreData", "Work");
            Directory.CreateDirectory(work);
            var older = Path.Combine(work, "older.log");
            var newest = Path.Combine(work, "newest.log");
            File.WriteAllText(older, "older content");
            File.WriteAllText(newest, "newest content");
            File.SetLastWriteTime(older, DateTime.Now.AddMinutes(-5));

            RunWebsiteUiTest(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var page = new LogViewerPage(new LogBrowser(root));
                try
                {
                    WaitOnDispatcher(page.ShowModuleAsync(LogModule.Panel));
                    Assert.AreEqual(newest, page.SelectedFile?.Path, "Choosing a module opens its newest log.");
                    Assert.AreEqual("newest content", page.LogBox.Text);
                    Assert.IsTrue(page.HasFile);

                    page.FileComboBox.SelectedIndex = 1;
                    WaitOnDispatcher(Task.Delay(50));
                    WaitUntil(() => page.SelectedFile?.Path == older);
                    Assert.AreEqual("older content", page.LogBox.Text);

                    File.AppendAllText(older, "\nappended line");
                    WaitOnDispatcher(page.RefreshAsync(keepSelection: true));
                    Assert.AreEqual(older, page.SelectedFile?.Path, "Refreshing keeps the file the user picked.");
                    StringAssert.EndsWith(page.LogBox.Text, "appended line");

                    WaitOnDispatcher(page.ShowModuleAsync(LogModule.Frp));
                    Assert.IsNull(page.SelectedFile);
                    Assert.IsFalse(page.HasFile);
                    StringAssert.Contains(page.EmptyText, "未找到 FRP 的日志文件");
                }
                finally
                {
                    page.Dispose();
                }
            });
        }
        finally { DeleteTemporaryTree(root); }
    }

    private static void WaitOnDispatcher(Task task)
    {
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "Timed out waiting for the log page.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
