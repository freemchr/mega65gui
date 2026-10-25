using System.Windows;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.App.Views;

/// <summary>Asks what to do with a file that is already in the destination: replace, skip, or the same for all.</summary>
public partial class ReplaceDialog : Window
{
    private OverwriteChoice _choice = OverwriteChoice.Cancel;

    private ReplaceDialog(string what)
    {
        InitializeComponent();
        What.Text = what;
    }

    public static OverwriteChoice Ask(string what)
    {
        var d = new ReplaceDialog(what) { Owner = Application.Current.MainWindow };
        d.ShowDialog();
        return d._choice;
    }

    private void Choose(object sender, RoutedEventArgs e)
    {
        _choice = Enum.Parse<OverwriteChoice>((string)((FrameworkElement)sender).Tag);
        DialogResult = _choice != OverwriteChoice.Cancel;
    }
}
