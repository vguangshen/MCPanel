using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void EnvironmentPageRendersSqlServerVersionSelectorWithoutResourceFailure()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            ComboBox? sqlServerComboBox = null;
            Exception? dispatcherFailure = null;
            try
            {
                window = new MainWindow();
                window.Dispatcher.UnhandledException += (_, args) =>
                {
                    dispatcherFailure ??= args.Exception;
                    args.Handled = true;
                };

                window.Show();
                var homePage = (FrameworkElement)window.FindName("HomePage");
                var environmentPage = (FrameworkElement)window.FindName("EnvironmentPage");
                homePage.Visibility = Visibility.Collapsed;
                environmentPage.Visibility = Visibility.Visible;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                sqlServerComboBox = FindVisualChildren<ComboBox>(environmentPage)
                    .SingleOrDefault(candidate =>
                        candidate.DataContext is EnvironmentItem item &&
                        item.Kind == EnvironmentKind.SqlServer &&
                        candidate.Items.Count > 0 &&
                        candidate.Items[0] is SqlServerReleaseDefinition);
                Assert.IsNotNull(sqlServerComboBox, "环境页面必须成功渲染 SQL Server 版本选择器。");

                var sqlServerItem = (EnvironmentItem)sqlServerComboBox!.DataContext;
                Assert.AreEqual(sqlServerItem.SqlServerReleaseOptions.Count, sqlServerComboBox.Items.Count);

                sqlServerComboBox.IsDropDownOpen = true;
                sqlServerComboBox.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                for (var index = 0; index < sqlServerItem.SqlServerReleaseOptions.Count; index++)
                {
                    var option = sqlServerItem.SqlServerReleaseOptions[index];
                    var container = sqlServerComboBox.ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem;
                    Assert.IsNotNull(container, $"SQL Server 版本选项 {option.DisplayName} 必须生成 ComboBoxItem。");
                    Assert.AreEqual(
                        option.IsSupported,
                        container!.IsEnabled,
                        $"SQL Server 版本选项 {option.DisplayName} 的启用状态必须与 OS 兼容判断一致。");
                }

                if (dispatcherFailure is not null)
                {
                    throw new AssertFailedException($"环境页面渲染触发了未处理的 WPF 异常：{dispatcherFailure}");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (sqlServerComboBox is not null)
                {
                    sqlServerComboBox.IsDropDownOpen = false;
                }

                window?.Close();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(60)), "环境页面 SQL Server 选择器渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"环境页面无法完整渲染：{failure}");
        }
    }
}
