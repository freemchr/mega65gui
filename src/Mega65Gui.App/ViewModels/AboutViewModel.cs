using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;

namespace Mega65Gui.App.ViewModels;

public sealed partial class AboutViewModel() : PageViewModel("About", "")
{
    public const string RepositoryUrl = "https://github.com/freemchr/mega65gui";
    public const string IssuesUrl = RepositoryUrl + "/issues";

    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    [RelayCommand]
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
