using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MCPanel;

internal static class PanelThemeService
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string Light, string Dark)>(StringComparer.Ordinal)
        {
            ["PrimaryBrush"] = ("#1A73E8", "#4C8DFF"),
            ["AccentBrush"] = ("#1A73E8", "#4C8DFF"),
            ["PrimaryDarkBrush"] = ("#1765CC", "#6EA3FF"),
            ["SurfaceBrush"] = ("#FFFFFF", "#1B2027"),
            ["PageBrush"] = ("#F5F7FB", "#101419"),
            ["SurfaceAltBrush"] = ("#F8FAFD", "#242B33"),
            ["FooterBrush"] = ("#EEF2F7", "#171C22"),
            ["TextBrush"] = ("#202124", "#F8FAFC"),
            ["MutedBrush"] = ("#5F6368", "#D0D6DD"),
            ["LineBrush"] = ("#DADCE0", "#46505B"),
            ["CardLineBrush"] = ("#E3E7EE", "#4B5561"),
            ["InputBrush"] = ("#FFFFFF", "#151A20"),
            ["NavSelectedBrush"] = ("#E8F0FE", "#183A63"),
            ["NavHoverBrush"] = ("#F1F3F4", "#202B36"),
            ["HeaderLogoBrush"] = ("#2B7DE9", "#2B7DE9"),
            ["HeaderMutedBrush"] = ("#E8F0FE", "#D2E3FC"),
            ["TonalBrush"] = ("#E8F0FE", "#243D63"),
            ["TonalTextBrush"] = ("#174EA6", "#AECBFA"),
            ["DisabledBrush"] = ("#E8EAED", "#343B44"),
            ["DisabledTextBrush"] = ("#9AA0A6", "#A7B0BA"),
            ["WarningBrush"] = ("#FFF3E0", "#4A310E"),
            ["WarningHoverBrush"] = ("#FFE0B2", "#5B3C12"),
            ["WarningTextBrush"] = ("#C15C00", "#FFC36B"),
            ["DangerBrush"] = ("#FDECEC", "#4A2526"),
            ["DangerHoverBrush"] = ("#FAD2D0", "#653032"),
            ["DangerTextBrush"] = ("#B3261E", "#FFB4AB"),
            ["MemoryBrush"] = ("#34A853", "#57C878"),
            ["ScrollThumbBrush"] = ("#B7C0CC", "#46515E"),
            ["ScrollThumbHoverBrush"] = ("#7E8A99", "#748092"),
            ["LogBackgroundBrush"] = ("#101820", "#101820"),
            ["LogTextBrush"] = ("#CBD5E1", "#CBD5E1"),
            ["DialogSurfaceBrush"] = ("#FFFFFF", "#20262F"),
            ["DialogLineBrush"] = ("#DADCE0", "#3A4655"),
            ["DialogTextBrush"] = ("#202124", "#F3F6FA"),
            ["DialogMutedBrush"] = ("#5F6368", "#B7C1CF"),
            ["DialogPrimaryBrush"] = ("#1A73E8", "#6EA3FF"),
            ["DialogPrimaryHoverBrush"] = ("#1765CC", "#8AB5FF"),
            ["DialogTonalBrush"] = ("#E8F0FE", "#263A5A"),
            ["DialogTonalTextBrush"] = ("#174EA6", "#A9C7FF"),
            ["DialogDangerBrush"] = ("#D93025", "#FF8A80")
        };

    internal static void Apply(bool dark, ResourceDictionary? fallbackResources = null)
    {
        var application = Application.Current;
        var applicationResources = application?.Resources;
        if (application is not null && applicationResources is not null)
        {
            ApplyTo(applicationResources, dark, allowCreate: true);

            // MainWindow keeps the shared dictionary in its local scope for
            // designer/test-host compatibility. Keep that instance in sync
            // while dialogs continue to resolve the application resources.
            foreach (Window window in application.Windows)
            {
                if (!ReferenceEquals(window.Resources, applicationResources))
                {
                    ApplyTo(window.Resources, dark, allowCreate: false);
                }
            }

            if (fallbackResources is not null && !ReferenceEquals(fallbackResources, applicationResources))
            {
                ApplyTo(fallbackResources, dark, allowCreate: false);
            }
        }
        else if (fallbackResources is not null)
        {
            ApplyTo(fallbackResources, dark, allowCreate: true);
        }
    }

    private static void ApplyTo(ResourceDictionary resources, bool dark, bool allowCreate)
    {
        foreach (var entry in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? entry.Value.Dark : entry.Value.Light);
            if (resources[entry.Key] is SolidColorBrush brush && !brush.IsFrozen && !brush.IsSealed)
            {
                brush.Color = color;
            }
            else if (allowCreate)
            {
                resources[entry.Key] = new SolidColorBrush(color);
            }
        }
    }
}
