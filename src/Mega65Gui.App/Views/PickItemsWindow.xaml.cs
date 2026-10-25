using System.Windows;

namespace Mega65Gui.App.Views;

/// <summary>Lets the user pick some of a list of items (for example the files on a disk image).</summary>
public partial class PickItemsWindow : Window
{
    private PickItemsWindow(string title, string prompt, IEnumerable<string> items, string ok)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        OkButton.Content = ok;
        var n = 0;
        foreach (var i in items) Items.Items.Add(new System.Windows.Controls.ListBoxItem { Content = i, Tag = n++ });
    }

    /// <summary>The positions of the chosen items, in list order; empty when cancelled.</summary>
    public static IReadOnlyList<int> Ask(string title, string prompt, IReadOnlyList<string> items, string ok = "OK")
    {
        var d = new PickItemsWindow(title, prompt, items, ok) { Owner = Application.Current.MainWindow };
        if (d.ShowDialog() != true) return [];
        return d.Items.SelectedItems.Cast<System.Windows.Controls.ListBoxItem>().Select(i => (int)i.Tag).Order().ToList();
    }

    private void SelectAll(object sender, RoutedEventArgs e) => Items.SelectAll();
    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
