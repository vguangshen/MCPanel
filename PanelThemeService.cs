using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MCPanel;

internal static class PanelThemeService
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string Light, string Dark)>(StringComparer.Ordinal)
        {
            ["PrimaryBrush"] = ("#1A73E8", "#2B64B2"),
            ["AccentBrush"] = ("#1A73E8", "#2B64B2"),
            ["PrimaryDarkBrush"] = ("#1765CC", "#3570C4"),
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
            // Use a light mint accent in dark mode so tonal text stays
            // legible on blue-gray controls instead of blending into them.
            ["TonalTextBrush"] = ("#174EA6", "#B8F2E6"),
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
            ["DialogPrimaryBrush"] = ("#1A73E8", "#2B64B2"),
            ["DialogPrimaryHoverBrush"] = ("#1765CC", "#3570C4"),
            ["DialogTonalBrush"] = ("#E8F0FE", "#263A5A"),
            ["DialogTonalTextBrush"] = ("#174EA6", "#B8F2E6"),
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
        ApplyTo(resources, dark, allowCreate, new HashSet<ResourceDictionary>());
    }

    private static void ApplyTo(
        ResourceDictionary resources,
        bool dark,
        bool allowCreate,
        ISet<ResourceDictionary> visited)
    {
        if (!visited.Add(resources))
        {
            return;
        }

        foreach (var entry in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? entry.Value.Dark : entry.Value.Light);
            if (ContainsLocalResource(resources, entry.Key))
            {
                SetBrush(resources, entry.Key, color);
            }
        }

        foreach (var mergedResources in resources.MergedDictionaries)
        {
            ApplyTo(mergedResources, dark, allowCreate: false, visited);
        }

        // Only create fallback resources when the palette is not supplied by
        // this dictionary or any of its merged dictionaries. This prevents a
        // local copy from shadowing the shared theme dictionary.
        if (allowCreate)
        {
            foreach (var entry in Palette)
            {
                if (!ContainsResource(resources, entry.Key, new HashSet<ResourceDictionary>()))
                {
                    var color = (Color)ColorConverter.ConvertFromString(dark ? entry.Value.Dark : entry.Value.Light);
                    resources[entry.Key] = new SolidColorBrush(color);
                }
            }
        }
    }

    private static bool ContainsLocalResource(ResourceDictionary resources, string key)
    {
        foreach (var resourceKey in resources.Keys)
        {
            if (Equals(resourceKey, key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsResource(
        ResourceDictionary resources,
        string key,
        ISet<ResourceDictionary> visited)
    {
        if (!visited.Add(resources))
        {
            return false;
        }

        if (ContainsLocalResource(resources, key))
        {
            return true;
        }

        foreach (var mergedResources in resources.MergedDictionaries)
        {
            if (ContainsResource(mergedResources, key, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen && !brush.IsSealed)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }
}
