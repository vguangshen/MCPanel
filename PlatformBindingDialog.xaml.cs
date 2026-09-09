using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class PlatformBindingDialog : PanelModalWindow
{
    private readonly List<PlatformBindingCandidate> _candidates;

    internal PlatformBindingDialog(IEnumerable<PlatformBindingCandidate> candidates, bool java)
    {
        _candidates = candidates.ToList();
        InitializeComponent();
        TitleText.Text = java ? "手动绑定 Java 平台" : "绑定 IIS 平台";
        CustomIisButton.Visibility = java ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = java
            ? "使用已配置的软件目录创建 Tomcat 绑定并分配独立端口。绑定后可在网站列表中管理启动和停止。"
            : "使用已配置的软件目录创建 IIS 应用及应用程序池。其他本地网站目录可通过左下角按钮绑定。";
        ApplyFilter();
    }

    internal PlatformBindingCandidate? SelectedCandidate { get; private set; }
    internal bool UseCustomIis { get; private set; }

    internal static List<PlatformBindingCandidate> FindCandidates(IEnumerable<ProductItem> products, bool java)
    {
        var result = new List<PlatformBindingCandidate>();
        foreach (var product in products.Where(product => product.CanProductAction))
        {
            try
            {
                var path = ProductInstallPathResolver.ResolveProductDirectory(product);
                if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any()) continue;
                var runtime = ProductDeploymentService.DetectRuntime(product, path);
                if (runtime != ProductDeploymentService.ProductRuntimeKind.Unknown && runtime != (java ? ProductDeploymentService.ProductRuntimeKind.Tomcat : ProductDeploymentService.ProductRuntimeKind.Iis)) continue;
                result.Add(new PlatformBindingCandidate(product, path));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return result.OrderBy(candidate => candidate.Product.DisplayName).ToList();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = ProductBox.SelectedItem;
        var keyword = SearchBox.Text.Trim();
        var matches = _candidates.Where(candidate => candidate.Label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        ProductBox.ItemsSource = matches;
        ProductBox.SelectedItem = selected is PlatformBindingCandidate previous && matches.Contains(previous) ? previous : matches.FirstOrDefault();
        StatusText.Text = matches.Count == 0
            ? "没有可绑定的平台。请检查搜索词，或先在产品管理中下载对应软件，并等待安装任务完成。"
            : $"可选 {matches.Count} 个平台；绑定会应用运行配置，可能触发管理员授权。";
        BindButton.IsEnabled = ProductBox.SelectedItem is not null;
    }

    private void Product_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PathBox is not null) PathBox.Text = (ProductBox.SelectedItem as PlatformBindingCandidate)?.Path ?? string.Empty;
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (ProductBox.SelectedItem is not PlatformBindingCandidate candidate) return;
        SelectedCandidate = candidate;
        DialogResult = true;
    }

    private void CustomIis_Click(object sender, RoutedEventArgs e)
    {
        UseCustomIis = true;
        DialogResult = true;
    }
}

internal sealed record PlatformBindingCandidate(ProductItem Product, string Path)
{
    public string Label => $"{Product.DisplayName} · {Product.ProductId}";
}
