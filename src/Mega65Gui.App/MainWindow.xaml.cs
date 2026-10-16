using System.Windows;
using Mega65Gui.App.ViewModels;

namespace Mega65Gui.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            await vm.Selected.OnShownAsync();
            await vm.Shell.TryAutoSignInAsync();
        };
    }
}
