using System.Windows;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.App.Services;

public sealed class WpfApprovalGate : IApprovalGate
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes).Task;

    public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() => ChangeApprovalWindow.Ask(title, plan)).Task;
}
