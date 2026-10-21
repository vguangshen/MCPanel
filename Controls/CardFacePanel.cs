using System;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

/// <summary>
/// Overlays card faces while deriving the card's layout footprint from the
/// first (front) face. Keep that face Hidden, not Collapsed, when flipped.
/// Back-face lists must use a ScrollViewer to fit this bounded viewport.
/// </summary>
public sealed class CardFacePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return new Size();

        var front = InternalChildren[0];
        front.Measure(availableSize);
        var desired = front.DesiredSize;
        // The environment page scrolls vertically, so availableSize.Height
        // can be infinite. Never pass that infinity to a back-face list:
        // UniformGrid would otherwise copy its full height to every card.
        var backViewport = new Size(
            double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width,
            desired.Height);
        for (var index = 1; index < InternalChildren.Count; index++)
            InternalChildren[index].Measure(backViewport);

        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(finalSize);
        foreach (UIElement face in InternalChildren)
            face.Arrange(bounds);
        return finalSize;
    }
}
