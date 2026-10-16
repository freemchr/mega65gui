using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mega65Gui.App.ViewModels;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.App.Views;

/// <summary>Drag-and-drop and double-click plumbing for the two file lists.</summary>
public partial class CardBrowserView : UserControl
{
    private const string LocalFormat = "Mega65Gui.LocalItems";
    private const string RemoteFormat = "Mega65Gui.RemoteItems";
    private Point _dragStart;

    public CardBrowserView() => InitializeComponent();

    private CardBrowserViewModel? Vm => DataContext as CardBrowserViewModel;

    private void LocalDoubleClick(object s, MouseButtonEventArgs e) => Vm?.OpenLocalCommand.Execute(LocalList.SelectedItem);
    private void RemoteDoubleClick(object s, MouseButtonEventArgs e) => Vm?.OpenRemoteCommand.Execute(RemoteList.SelectedItem);

    private void DragStart(object s, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private bool DragThresholdPassed(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var d = _dragStart - e.GetPosition(null);
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void LocalDragMove(object s, MouseEventArgs e)
    {
        if (!DragThresholdPassed(e) || LocalList.SelectedItems.Count == 0) return;
        var items = LocalList.SelectedItems.OfType<LocalItem>().ToList();
        var data = new DataObject(LocalFormat, items);
        // Also offer real file paths so files can be dropped into Explorer or other apps.
        data.SetData(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());
        DragDrop.DoDragDrop(LocalList, data, DragDropEffects.Copy);
    }

    private void RemoteDragMove(object s, MouseEventArgs e)
    {
        if (!DragThresholdPassed(e) || RemoteList.SelectedItems.Count == 0) return;
        DragDrop.DoDragDrop(RemoteList, new DataObject(RemoteFormat, RemoteList.SelectedItems.OfType<RemoteEntry>().ToList()), DragDropEffects.Copy);
    }

    private void RemoteDragOver(object s, DragEventArgs e)
    {
        e.Effects = Vm?.IsOpen == true && (e.Data.GetDataPresent(LocalFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void RemoteDrop(object s, DragEventArgs e)
    {
        if (Vm is not { IsOpen: true } vm) return;
        var paths = e.Data.GetData(LocalFormat) is List<LocalItem> local
            ? local.Where(i => !i.IsDirectory).Select(i => i.FullPath).ToList()
            : (e.Data.GetData(DataFormats.FileDrop) as string[] ?? []).ToList();
        await vm.UploadFilesAsync(paths);
    }

    private void LocalDragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RemoteFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LocalDrop(object s, DragEventArgs e)
    {
        if (Vm is { } vm && e.Data.GetData(RemoteFormat) is List<RemoteEntry> items) await vm.DownloadAsync(items);
    }
}
