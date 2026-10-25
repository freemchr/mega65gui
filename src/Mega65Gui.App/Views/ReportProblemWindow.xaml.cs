using System.Runtime.InteropServices;
using System.Windows;
using Mega65Gui.App.ViewModels;
using Mega65Gui.Core.Common;

namespace Mega65Gui.App.Views;

public partial class ReportProblemWindow : Window
{
    private ReportProblemWindow(string details)
    {
        InitializeComponent();
        Details.Text = details;
    }

    public static void Show(string details) =>
        new ReportProblemWindow(details) { Owner = Application.Current.MainWindow }.ShowDialog();

    private bool CopyToClipboard()
    {
        try { Clipboard.SetText(Details.Text); return true; }
        catch (COMException) { return false; } // another program is holding the clipboard
    }

    private void Copy(object sender, RoutedEventArgs e) =>
        Copied.Text = CopyToClipboard() ? "Copied." : "The clipboard is busy; try again.";

    private void OpenIssue(object sender, RoutedEventArgs e)
    {
        // Also on the clipboard, in case the link had to be shortened.
        CopyToClipboard();
        AboutViewModel.OpenUrl(ProblemReport.IssueUrl(AboutViewModel.RepositoryUrl, "Problem: ", Details.Text));
        Copied.Text = "Opened in your browser; the details are also on the clipboard.";
    }
}
