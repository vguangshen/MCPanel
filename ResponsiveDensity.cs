using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace MCPanel;

/// <summary>
/// Window-wide layout density, inherited by every element (templates
/// included). MainWindow sets it from the window size; XAML reacts through
/// style triggers or <see cref="DensityExtension"/>.
/// </summary>
public static class ResponsiveDensity
{
    /// <summary>Short or narrow window: tighter padding, smaller controls.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(ResponsiveDensity),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Content too narrow for side-by-side home cards: stack them.</summary>
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.RegisterAttached(
        "IsNarrow", typeof(bool), typeof(ResponsiveDensity),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);
    public static bool GetIsNarrow(DependencyObject element) => (bool)element.GetValue(IsNarrowProperty);
    public static void SetIsNarrow(DependencyObject element, bool value) => element.SetValue(IsNarrowProperty, value);
}

/// <summary>
/// Picks a property value by density:
/// <c>Margin="{local:Density Normal='0,10,0,0', Compact='0,8,0,0'}"</c>.
/// <see cref="Narrow"/>, when given, wins over the other two while the
/// content is narrow. Values are strings converted to the target property type.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class DensityExtension : MarkupExtension
{
    public string? Normal { get; set; }
    public string? Compact { get; set; }
    public string? Narrow { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        if (target?.TargetProperty is not DependencyProperty property)
        {
            // Inside a template definition: WPF calls back per instance.
            return this;
        }

        var converter = new DensityValueConverter(property.PropertyType, Normal, Compact ?? Normal, Narrow);
        var binding = new MultiBinding { Converter = converter, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding { Path = new PropertyPath(ResponsiveDensity.IsCompactProperty), RelativeSource = RelativeSource.Self });
        binding.Bindings.Add(new Binding { Path = new PropertyPath(ResponsiveDensity.IsNarrowProperty), RelativeSource = RelativeSource.Self });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class DensityValueConverter : IMultiValueConverter
    {
        private readonly object? _normal;
        private readonly object? _compact;
        private readonly object? _narrow;
        private readonly bool _hasNarrow;

        public DensityValueConverter(Type type, string? normal, string? compact, string? narrow)
        {
            var converter = TypeDescriptor.GetConverter(type);
            _normal = Parse(converter, normal);
            _compact = Parse(converter, compact);
            _hasNarrow = narrow is not null;
            _narrow = Parse(converter, narrow);
        }

        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var compact = values.Length > 0 && values[0] is true;
            var narrow = values.Length > 1 && values[1] is true;
            return narrow && _hasNarrow ? _narrow : compact ? _compact : _normal;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static object? Parse(TypeConverter converter, string? text) =>
            text is null ? null : converter.ConvertFromInvariantString(text);
    }
}
