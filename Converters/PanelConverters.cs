using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MCPanel;

public sealed class MeterHeightConverter : IMultiValueConverter
{
    public static MeterHeightConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double height || values[1] is not double value)
        {
            return 0d;
        }

        return Math.Max(0, height * Compat.Clamp(value, 0, 100) / 100d);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Red;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "是";
    public string FalseText { get; set; } = "否";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double number ? number : 0d;
        var subtraction = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
        return Math.Max(0, width - subtraction);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IConvertible convertible)
        {
            return Compat.Clamp(convertible.ToDouble(CultureInfo.InvariantCulture) / 100d, 0, 1);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentToArcPointConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = ReadPercent(value);
        var (center, radius) = ReadArc(parameter);
        var angle = (-90d + percent * 3.599d) * Math.PI / 180d;
        return new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double ReadPercent(object value)
    {
        if (value is IConvertible convertible)
        {
            return Compat.Clamp(convertible.ToDouble(CultureInfo.InvariantCulture), 0, 100);
        }

        return 0;
    }

    private static (double Center, double Radius) ReadArc(object parameter)
    {
        if (parameter is string text)
        {
            var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var center) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius))
            {
                return (center, radius);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
            {
                return (radius + 8, radius);
            }
        }

        return (59, 51);
    }
}

public sealed class PercentToLargeArcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IConvertible convertible)
        {
            return convertible.ToDouble(CultureInfo.InvariantCulture) >= 50;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ServiceActionEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
