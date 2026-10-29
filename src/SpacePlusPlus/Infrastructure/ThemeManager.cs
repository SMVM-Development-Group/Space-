using System.Windows;

namespace SpacePlusPlus.Infrastructure;

public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    /// <summary>Swaps the color dictionary. Merged dictionary 0 is reserved for theme colors.</summary>
    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null) return;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"/SpacePlusPlus;component/Resources/Colors.{theme}.xaml", UriKind.Relative)
        };
        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count > 0) dictionaries[0] = dict;
        else dictionaries.Insert(0, dict);
    }
}
