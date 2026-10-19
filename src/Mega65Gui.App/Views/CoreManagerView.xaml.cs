using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Mega65Gui.App.ViewModels;

namespace Mega65Gui.App.Views;

/// <summary>Drag-and-drop into the library, and keeping the newest log line in view.</summary>
public partial class CoreManagerView : UserControl
{
    public CoreManagerView()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private void LibraryDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LibraryDrop_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is CoreManagerViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await vm.ImportPathsAsync(files);
    }
}
