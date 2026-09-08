using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void NginxGridRendersReadableThemesAndNativeScrollbars()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            NginxProxyDialog? dialog = null;
            try
            {
                dialog = new NginxProxyDialog(new EnvironmentRuntimeService());
                var grid = (DataGrid)dialog.FindName("RulesGrid");
                grid.ItemsSource = Enumerable.Range(1, 44).Select(i => new NginxProxyDialog.NginxProxyRuleRow
                {
                    Name = $"产品 DS{i:0000}", Enabled = true, ListenPort = "72", ServerName = "localhost",
                    LocationPath = $"/DS{i:0000}", ProxyTarget = $"http://127.0.0.1:8088/DS{i:0000}", WebSocket = true
                }).ToList();
                ((TextBlock)dialog.FindName("RuleCountText")).Text = "44 条规则";
                dialog.Show();
                foreach (var dark in new[] { true, false })
                {
                    dialog.ApplyTheme(dark);
                    grid.SelectedIndex = 0;
                    dialog.UpdateLayout();
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var firstRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                    var secondRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
                    Assert.IsNotNull(firstRow);
                    Assert.IsNotNull(secondRow);
                    var foreground = ((SolidColorBrush)secondRow.Foreground).Color;
                    var background = ((SolidColorBrush)secondRow.Background).Color;
                    Assert.IsTrue(Math.Abs(foreground.R - background.R) > 100, "Row text must contrast with its background.");
                    var vertical = FindVisualChildren<ScrollBar>(grid).First(bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);
                    Assert.AreSame(((Style)dialog.FindResource("ModernVerticalScrollBar")).Setters.OfType<Setter>()
                        .First(s => s.Property == Control.TemplateProperty).Value, vertical.Template);
                    var viewer = FindVisualChildren<ScrollViewer>(grid).First();
                    viewer.ScrollToEnd();
                    dialog.UpdateLayout();
                    Assert.IsTrue(viewer.VerticalOffset > 0, "Rules should remain scrollable.");
                    viewer.ScrollToHome();
                    dialog.UpdateLayout();
                    SaveNginxPreview(dialog, dark ? "dark" : "light");
                    grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[1]);
                    grid.Focus();
                    Assert.IsTrue(grid.BeginEdit());
                    dialog.UpdateLayout();
                    var editor = FindVisualChildren<TextBox>(grid).First();
                    Assert.AreEqual(((SolidColorBrush)dialog.FindResource("InputBrush")).Color, ((SolidColorBrush)editor.Background).Color);
                    SaveNginxPreview(dialog, dark ? "dark-edit" : "light-edit");
                    editor.Text = "编辑后的规则";
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                    Assert.AreEqual("编辑后的规则", ((NginxProxyDialog.NginxProxyRuleRow)grid.Items[0]).Name);
                    grid.Columns[5].Width = 1000;
                    dialog.UpdateLayout();
                    var horizontal = FindVisualChildren<ScrollBar>(grid).First(bar => bar.Orientation == Orientation.Horizontal && bar.IsVisible);
                    Assert.AreSame(dialog.FindResource("ModernHorizontalScrollBarTemplate"), horizontal.Template);
                    viewer.ScrollToRightEnd();
                    dialog.UpdateLayout();
                    Assert.IsTrue(viewer.HorizontalOffset > 0);
                    SaveNginxPreview(dialog, dark ? "dark-horizontal" : "light-horizontal");
                    grid.Columns[5].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                    viewer.ScrollToHome();
                }
                dialog.Width = 820;
                dialog.Height = 620;
                dialog.UpdateLayout();
                Assert.IsTrue(((Border)dialog.Content).ActualWidth <= 780, "Card must fit inside compact window margins.");
                SaveNginxPreview(dialog, "compact");
            }
            catch (Exception error) { failure = error; }
            finally { dialog?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Nginx UI rendering timed out.");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    private static void SaveNginxPreview(Window window, string theme)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "nginx-ui"));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"nginx-{theme}.png"));
        encoder.Save(stream);
    }
}
