using System.Windows;
using Mega65Gui.App.ViewModels;

namespace Mega65Gui.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.Selected.OnShownAsync();
    }
}
