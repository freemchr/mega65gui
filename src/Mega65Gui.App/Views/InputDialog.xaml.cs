using System.Windows;

namespace Mega65Gui.App.Views;

public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Answer.Text = initial;
        Loaded += (_, _) => { Answer.Focus(); Answer.SelectAll(); };
    }

    /// <summary>Returns the trimmed answer, or null when cancelled or empty.</summary>
    public static string? Ask(string title, string prompt, string initial = "")
    {
        var d = new InputDialog(title, prompt, initial) { Owner = Application.Current.MainWindow };
        return d.ShowDialog() == true && d.Answer.Text.Trim() is { Length: > 0 } a ? a : null;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
