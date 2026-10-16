using System.Windows;
using Mega65Gui.App.ViewModels;

namespace Mega65Gui.App.Views;

/// <summary>Attached behaviour: files dropped on the element are sent through DeviceViewModel.SendFileAsync.</summary>
public static class DropTarget
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(DeviceViewModel), typeof(DropTarget), new PropertyMetadata(null, OnChanged));

    public static DeviceViewModel? GetCommand(DependencyObject d) => (DeviceViewModel?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, DeviceViewModel? value) => d.SetValue(CommandProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.Drop -= OnDrop;
        el.DragOver -= OnDragOver;
        if (e.NewValue is null) return;
        el.Drop += OnDrop;
        el.DragOver += OnDragOver;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static async void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject d || GetCommand(d) is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files) await vm.SendFileAsync(f);
    }
}
