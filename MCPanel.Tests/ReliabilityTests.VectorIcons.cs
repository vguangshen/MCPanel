using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void LocalVectorsCoverEveryApplicationIconWithoutFontGlyphTextBlocks()
    {
        RunWebsiteUiTest(() =>
        {
            var root = FindRepositoryRootForDialogUi();
            var xamlFiles = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                               !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToArray();
            var pattern = new Regex(@"&#x(?<xml>E[0-9a-f]{3});|\\u(?<cs>E[0-9a-f]{3})", RegexOptions.IgnoreCase);
            foreach (var path in Directory.GetFiles(root, "*.cs").Concat(xamlFiles))
            {
                var source = File.ReadAllText(path);
                foreach (Match match in pattern.Matches(source))
                {
                    var code = match.Groups["xml"].Success ? match.Groups["xml"].Value : match.Groups["cs"].Value;
                    Assert.IsNotNull(PanelIconCatalog.Find(((char)Convert.ToInt32(code, 16)).ToString()), path + ": " + code);
                }
                if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                Assert.IsFalse(source.Contains("Segoe MDL2 Assets"), path + " must not require the Windows icon font.");
                foreach (var node in XDocument.Load(path).Descendants().Where(node => node.Name.LocalName == "TextBlock"))
                {
                    Assert.IsFalse(((string?)node.Attribute("Text") ?? string.Empty).Any(IsPrivateIconCharacter),
                        path + " must draw icon identifiers with PanelIcon instead of text.");
                }
            }
        });
    }

    [TestMethod]
    public void EveryVectorRendersWithMissingFontsAcrossThemesAndDpi()
    {
        RunWebsiteUiTest(() =>
        {
            foreach (var glyph in PanelIconCatalog.Glyphs)
            foreach (var size in new[] { 12d, 21d, 32d })
            foreach (var dpi in new[] { 96d, 144d, 192d })
            foreach (var foreground in new[] { Brushes.Black, Brushes.White })
            {
                var icon = new PanelIcon { Text = glyph, FontSize = size, Foreground = foreground };
                TextElement.SetFontFamily(icon, new FontFamily("MCPanel-Intentionally-Unavailable-Font"));
                icon.Measure(new Size(size, size));
                icon.Arrange(new Rect(0, 0, size, size));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size * dpi / 96),
                    (int)Math.Ceiling(size * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(icon);
                var pixels = IconPixels(bitmap);
                Assert.IsTrue(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] > 0) > 2,
                    $"Icon {((int)glyph[0]):X4} must render at {size}px/{dpi}dpi without a font.");
            }
        });
    }

    [TestMethod]
    public void IconTemplatesPreserveButtonLabelsBadgesAndChangingQueueGlyphs()
    {
        RunWebsiteUiTest(() =>
        {
            var resources = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MCPanel;component/Resources/PanelTheme.xaml")
            };
            var host = new StackPanel { Width = 400 };
            host.Resources.MergedDictionaries.Add(resources);
            var styles = new[] { "TitleBarButton", "ProductToolbarIconButton", "QueueIconButton",
                "PathIconButton", "EnvironmentTonalButton", "EnvironmentPrimaryButton" };
            var buttons = styles.Select(key => new Button
            {
                Style = (Style)resources[key], Content = "\uE769", FontSize = 18, Foreground = Brushes.Red
            }).ToArray();
            foreach (var button in buttons) host.Children.Add(button);
            var badge = new TextBlock { Text = "7", FontSize = 9 };
            var composite = new Grid();
            composite.Children.Add(new PanelIcon { Text = "\uE896" });
            composite.Children.Add(badge);
            var badgeButton = new Button { Style = (Style)resources["ProductToolbarIconButton"], Content = composite };
            var labelButton = new Button { Style = (Style)resources["PrimaryButton"], Content = "下载进度" };
            host.Children.Add(badgeButton);
            host.Children.Add(labelButton);
            LayoutIconHost(host);

            foreach (var button in buttons)
            {
                var icon = FindVisualChildren<PanelIcon>(button).Single();
                Assert.AreEqual("\uE769", icon.Text);
                Assert.AreEqual(18d, icon.FontSize);
                Assert.AreSame(Brushes.Red, icon.Foreground);
                button.Content = "\uE768";
            }
            LayoutIconHost(host);
            Assert.IsTrue(buttons.All(button => FindVisualChildren<PanelIcon>(button).Single().Text == "\uE768"));
            Assert.IsTrue(FindVisualChildren<TextBlock>(badgeButton).Contains(badge), "The numeric badge must remain visible.");
            Assert.AreEqual(9d, badge.FontSize);
            Assert.IsFalse(FindVisualChildren<PanelIcon>(labelButton).Any(), "Ordinary labels must remain text.");
            Assert.IsTrue(FindVisualChildren<TextBlock>(labelButton).Any(text => text.Text == "下载进度"));
        });
    }

    [TestMethod]
    public void VectorIconsInheritSizeAndRepaintWhenThemeBrushOrGlyphChanges()
    {
        RunWebsiteUiTest(() =>
        {
            var brush = new SolidColorBrush(Colors.Red);
            var icon = new PanelIcon { Text = "\uE768" };
            var host = new Border { Child = icon };
            TextElement.SetForeground(host, brush);
            TextElement.SetFontSize(host, 24);
            host.Measure(new Size(24, 24));
            host.Arrange(new Rect(0, 0, 24, 24));
            Assert.AreSame(brush, icon.Foreground);
            Assert.AreEqual(24d, icon.FontSize);
            var before = RenderIconPixels(host);
            brush.Color = Colors.Blue;
            var recolored = RenderIconPixels(host);
            Assert.IsFalse(before.SequenceEqual(recolored), "Theme changes must repaint the vector.");
            icon.Text = "\uE769";
            var paused = RenderIconPixels(host);
            Assert.IsFalse(recolored.SequenceEqual(paused), "Pause/resume must update the actual drawing.");
        });
    }

    [TestMethod]
    public void MainWindowRendersLocalNavigationAndTitleIconsWithMissingFont()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.FontFamily = new FontFamily("MCPanel-Intentionally-Unavailable-Font");
                window.Width = 1280;
                window.Height = 820;
                window.Show();
                FindVisualChildren<RadioButton>(window).First(button => Equals(button.CommandParameter, "Sites")).IsChecked = true;
                foreach (var dark in new[] { false, true })
                {
                    PanelThemeService.Apply(dark, window.Resources);
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.AreEqual(7, FindVisualChildren<RadioButton>(window)
                        .Count(button => button.Tag is string glyph && PanelIconCatalog.Find(glyph) is not null &&
                                         FindVisualChildren<PanelIcon>(button).Any(icon => icon.Text == glyph)));
                    Assert.IsTrue(FindVisualChildren<PanelIcon>(window).Any(icon => icon.Text == "\uE921"));
                    Assert.IsTrue(FindVisualChildren<PanelIcon>(window).Any(icon => icon.Text == "\uE922"));
                    Assert.IsTrue(FindVisualChildren<PanelIcon>(window).Any(icon => icon.Text == "\uE8BB"));
                    Assert.IsFalse(FindVisualChildren<TextBlock>(window).Any(text => text.Text.Any(IsPrivateIconCharacter)));
                    SaveVectorIconPreview(window, 1280, 820, dark ? "main-dark" : "main-light");
                }

                var gallery = new UniformGrid { Columns = 6, Width = 720, Height = 400, Background = Brushes.White };
                foreach (var glyph in PanelIconCatalog.Glyphs)
                {
                    var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    cell.Children.Add(new PanelIcon { Text = glyph, FontSize = 30, Foreground = Brushes.DarkSlateGray });
                    cell.Children.Add(new TextBlock { Text = ((int)glyph[0]).ToString("X4"),
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
                    gallery.Children.Add(cell);
                }
                gallery.Measure(new Size(720, 400));
                gallery.Arrange(new Rect(0, 0, 720, 400));
                SaveVectorIconPreview(gallery, 720, 400, "icon-gallery");
            }
            finally { window.Close(); }
        });
    }

    private static bool IsPrivateIconCharacter(char character) => character >= '\uE000' && character <= '\uF8FF';
    private static void LayoutIconHost(FrameworkElement host)
    {
        host.Measure(new Size(400, 800));
        host.Arrange(new Rect(0, 0, 400, 800));
        host.UpdateLayout();
    }
    private static byte[] RenderIconPixels(Visual visual)
    {
        // Match the UI frame: changing an icon invalidates its drawing, then
        // WPF arranges and redraws it before the next frame is captured.
        if (visual is UIElement element) element.UpdateLayout();
        visual.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return IconPixels(bitmap);
    }
    private static byte[] IconPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static void SaveVectorIconPreview(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "icons"));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
