using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mega65Gui.App.ViewModels;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.Features;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.App.Views;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var hasValue = value is not (null or string { Length: 0 });
        if (p is "invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) != (p is "invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>"HardwareUnverified" becomes "Hardware unverified".</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not Enum e) return value ?? "";
        var name = e.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Status colours; mid-tones chosen to read on both light and dark backgrounds.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Green = Freeze("#2E9D5B"), Amber = Freeze("#D08A00"), Red = Freeze("#D13438"),
        Blue = Freeze("#2B7CD3"), Grey = Freeze("#8A8A8A");

    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        StepStatus.Done or FeatureStatus.Implemented or SyncAction.Add or CompatibilityResult.Compatible or FilehostCoreState.InLibrary => Green,
        StepStatus.Running or FeatureStatus.Partial or SyncAction.Replace or FilehostCoreState.UpdateAvailable => Blue,
        StepStatus.NeedsUser or FeatureStatus.HardwareUnverified or CompatibilityResult.Unverified => Amber,
        StepStatus.Failed or FeatureStatus.NotOffered or CompatibilityResult.Incompatible => Red,
        _ => Grey,
    };
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Loads an image file fully into memory (so the file is not kept open and can be replaced or
/// deleted) and scales it down for display. Unreadable images show nothing.
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelWidth = p is string w && int.TryParse(w, out var px) ? px : 320;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { return null; }
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Icon for the connection notification: a tick for success, a warning sign otherwise.</summary>
public sealed class ToastGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is ShellStatusViewModel.ToastKind.Success ? "\uE73E" : "\uE7BA";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
