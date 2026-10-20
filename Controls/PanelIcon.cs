using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MCPanel;

/// <summary>Application-owned vector icons, independent of installed icon fonts.</summary>
public sealed class PanelIcon : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PanelIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(PanelIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(PanelIcon), new FrameworkPropertyMetadata(12d,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(FontSize, FontSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var geometry = PanelIconCatalog.Find(Text);
        var size = Math.Min(FontSize, Math.Min(RenderSize.Width, RenderSize.Height));
        if (geometry is null || size <= 0 || Foreground is null) return;

        var pen = new Pen(Foreground, 1.7)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        // All paths share a 24-unit viewport; inherited font size controls only their size.
        drawingContext.PushTransform(new MatrixTransform(new Matrix(
            size / 24, 0, 0, size / 24, (RenderSize.Width - size) / 2, (RenderSize.Height - size) / 2)));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }
}

/// <summary>Keep ordinary labels and composite controls such as the download badge intact.</summary>
public sealed class PanelIconTemplateSelector : DataTemplateSelector
{
    public DataTemplate? IconTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string text && PanelIconCatalog.Find(text) is not null
            ? IconTemplate : base.SelectTemplate(item, container);
}

internal static class PanelIconCatalog
{
    // Original outline artwork. Legacy identifiers keep queue/dialog bindings compatible.
    private static readonly IReadOnlyDictionary<string, Geometry> Icons = CreateIcons();
    internal static IEnumerable<string> Glyphs => Icons.Keys;
    internal static Geometry? Find(string? glyph) => glyph is not null && Icons.TryGetValue(glyph, out var icon) ? icon : null;

    private static IReadOnlyDictionary<string, Geometry> CreateIcons()
    {
        var paths = new Dictionary<string, string>
        {
            ["\uE80F"] = "M3,10 L12,3 21,10 M5,9 V21 H10 V15 H14 V21 H19 V9", // Home
            ["\uE7F4"] = "M3,4 H21 V20 H3 Z M3,8 H21 M6,6 H6.1 M9,6 H9.1 M7,12 H17 M7,16 H13", // Sites
            ["\uE8B7"] = "M3,7 V4 H10 L13,7 H21 V20 H3 Z M3,9 H21", // Folder
            ["\uE7F8"] = "M6,6 H18 V18 H6 Z M9,9 H15 V15 H9 Z M9,3 V6 M15,3 V6 M9,18 V21 M15,18 V21 M3,9 H6 M3,15 H6 M18,9 H21 M18,15 H21", // Environment
            ["\uE8D4"] = "M5,3 V7 M11,3 V7 M3,7 H13 V11 A5,5 0 0 1 8,16 V20 H18 V16 M15,16 H21 M18,13 V16", // API connection
            ["\uE99A"] = "M5,7 H19 V20 H5 Z M9,11 H9.1 M15,11 H15.1 M9,16 H15 M12,3 V7 M10,3 H14 M2,11 V16 M22,11 V16", // AI
            ["\uE713"] = "M9,3 H15 L16,6 19,6 21,10 19,12 21,15 19,19 16,18 15,21 H9 L8,18 5,19 3,15 5,12 3,10 5,6 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12", // Settings
            ["\uE721"] = "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L21,21", // Search
            ["\uE896"] = "M12,3 V15 M7,10 L12,15 17,10 M4,16 V21 H20 V16", // Download
            ["\uE895"] = "M12,15 V3 M7,8 L12,3 17,8 M4,16 V21 H20 V16", // Upload
            ["\uE774"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C6,8 6,16 12,21 C18,16 18,8 12,3", // Globe
            ["\uE70D"] = "M5,9 L12,16 19,9", // Chevron
            ["\uE7AD"] = "M10,5 L3,12 10,19 M3,12 H21", // Back
            ["\uE70F"] = "M4,16 L16,4 20,8 8,20 3,21 Z M13,7 L17,11", // Edit
            ["\uE8C8"] = "M8,8 H21 V21 H8 Z M16,8 V3 H3 V16 H8", // Copy
            ["\uE74E"] = "M3,3 H18 L21,6 V21 H3 Z M7,3 V9 H17 V3 M7,21 V14 H17 V21", // Save
            ["\uE921"] = "M5,12 H19", // Minimize
            ["\uE922"] = "M5,5 H19 V19 H5 Z", // Maximize
            ["\uE923"] = "M4,8 H16 V20 H4 Z M8,8 V4 H20 V16 H16", // Restore
            ["\uE8BB"] = "M5,5 L19,19 M19,5 L5,19", // Close
            ["\uE8D7"] = "M4,3 H15 L20,8 V21 H4 Z M15,3 V8 H20 M8,12 H16 M8,16 H16", // Input/document
            ["\uE72E"] = "M5,10 H19 V21 H5 Z M8,10 V6 A4,4 0 0 1 16,6 V10 M12,14 V17", // Lock
            ["\uE7BA"] = "M12,3 L22,21 H2 Z M12,9 V14 M12,17 V17.1", // Warning
            ["\uEA39"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M8,8 L16,16 M16,8 L8,16", // Error
            ["\uE9CE"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M9,8 A3,3 0 0 1 15,8 C15,10 12,10 12,13 M12,17 V17.1", // Question
            ["\uE946"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,11 V17 M12,7 V7.1", // Info
            ["\uE968"] = "M9,3 H15 V9 H9 Z M3,16 H9 V22 H3 Z M15,16 H21 V22 H15 Z M12,9 V12 M6,16 V12 H18 V16", // Network
            ["\uE768"] = "M7,3 L21,12 7,21 Z", // Resume
            ["\uE769"] = "M5,4 H9 V20 H5 Z M15,4 H19 V20 H15 Z", // Pause
            ["\uE74D"] = "M3,6 H21 M9,6 V3 H15 V6 M5,6 L6,21 H18 L19,6 M10,10 V17 M14,10 V17" // Delete
        };
        return paths.ToDictionary(item => item.Key, item =>
        {
            var geometry = Geometry.Parse(item.Value);
            geometry.Freeze();
            return geometry;
        });
    }
}
