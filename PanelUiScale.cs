using System.Windows;
using System.Windows.Media;

namespace MCPanel;

/// <summary>
/// The interface scale shared by the main window, its pop-ups and the
/// dialogs. Pop-ups and tool tips are separate windows that a scaled parent
/// does not scale, so their templates use
/// <c>LayoutTransform="{DynamicResource UiScaleTransform}"</c>; the main
/// window and dialogs apply <see cref="Transform"/> in code.
/// </summary>
public static class PanelUiScale
{
    public const string TransformResourceKey = "UiScaleTransform";

    private static double _current = 1d;
    private static Transform _transform = Transform.Identity;

    /// <summary>The current scale (1 = designed size).</summary>
    public static double Current => _current;

    /// <summary>A frozen transform for <see cref="Current"/>, usable on any thread.</summary>
    public static Transform Transform => _transform;

    /// <summary>
    /// Changes the scale and publishes it to application resources, so
    /// open pop-ups, tool tips and dialogs pick it up too.
    /// </summary>
    public static Transform Set(double scale)
    {
        _current = scale;
        _transform = CreateTransform(scale);
        if (Application.Current is { } application && application.Dispatcher.CheckAccess())
        {
            application.Resources[TransformResourceKey] = _transform;
        }

        return _transform;
    }

    internal static Transform CreateTransform(double scale)
    {
        if (Math.Abs(scale - 1d) < 0.001d)
        {
            return Transform.Identity;
        }

        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        return transform;
    }
}
