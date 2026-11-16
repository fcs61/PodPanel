using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PodPanel;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>Boş metinli satırları gizler.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Windows ile başlat" ayarı (yalnızca geçerli kullanıcı için Run anahtarı).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PodPanel";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (enabled && Environment.ProcessPath is string path) key.SetValue(ValueName, "\"" + path + "\"");
        else key.DeleteValue(ValueName, false);
    }
}

/// <summary>Sistem temasına göre açık ya da koyu fırçaları kaynak sözlüğüne yazar.</summary>
internal static class Theme
{
    public static bool AppsUseLightTheme() => ReadPersonalize("AppsUseLightTheme");

    public static bool TaskbarUsesLightTheme() => ReadPersonalize("SystemUsesLightTheme");

    private static bool ReadPersonalize(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue(name) is int value && value != 0;
    }

    public static void Apply(ResourceDictionary resources, string mode = "system")
    {
        bool light = mode == "light" || (mode != "dark" && AppsUseLightTheme());
        Color accent = SystemParameters.WindowGlassColor;
        accent.A = 255;

        if (light)
        {
            Set(resources, "BgBrush", "#FFF3F3F3");
            Set(resources, "CardBrush", "#FFFFFFFF");
            Set(resources, "HoverBrush", "#FFE9E9E9");
            Set(resources, "StrokeBrush", "#22000000");
            Set(resources, "TextBrush", "#FF1B1B1B");
            Set(resources, "SubTextBrush", "#FF5E5E5E");
            Set(resources, "WarnBrush", "#FF9A5B00");
            Set(resources, "OkBrush", "#FF0F7B0F");
            resources["AccentBrush"] = Frozen(Blend(accent, Colors.Black, 0.15));
            Set(resources, "OnAccentBrush", "#FFFFFFFF");
        }
        else
        {
            Set(resources, "BgBrush", "#FF202020");
            Set(resources, "CardBrush", "#FF2D2D2D");
            Set(resources, "HoverBrush", "#FF383838");
            Set(resources, "StrokeBrush", "#30FFFFFF");
            Set(resources, "TextBrush", "#FFFFFFFF");
            Set(resources, "SubTextBrush", "#FFB8B8B8");
            Set(resources, "WarnBrush", "#FFFFB454");
            Set(resources, "OkBrush", "#FF6CCB5F");
            resources["AccentBrush"] = Frozen(Blend(accent, Colors.White, 0.45));
            Set(resources, "OnAccentBrush", "#FF101010");
        }
    }

    private static void Set(ResourceDictionary resources, string key, string color) =>
        resources[key] = Frozen((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
