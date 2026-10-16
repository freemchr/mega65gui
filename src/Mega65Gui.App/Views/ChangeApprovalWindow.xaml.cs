using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.App.Views;

public partial class ChangeRow(SyncItem item) : ObservableObject
{
    public SyncItem Item { get; } = item;
    [ObservableProperty] private bool _selected = true;
}

public partial class ChangeApprovalWindow : Window
{
    private readonly List<ChangeRow> _rows;
    private IReadOnlySet<string>? _result;

    private ChangeApprovalWindow(string title, SyncPlan plan)
    {
        InitializeComponent();
        Heading.Text = title;
        _rows = plan.Changes.Select(c => new ChangeRow(c)).ToList();
        Items.ItemsSource = _rows;
        WarningList.ItemsSource = plan.Warnings;
        Totals.Text = $"{_rows.Count} change(s), {plan.BytesToWrite:N0} bytes";
    }

    public static IReadOnlySet<string>? Ask(string title, SyncPlan plan)
    {
        var w = new ChangeApprovalWindow(title, plan) { Owner = Application.Current.MainWindow };
        w.ShowDialog();
        return w._result;
    }

    private void SelectAll(object s, RoutedEventArgs e) => _rows.ForEach(r => r.Selected = true);
    private void SelectNone(object s, RoutedEventArgs e) => _rows.ForEach(r => r.Selected = false);
    private void Cancel(object s, RoutedEventArgs e) { _result = null; Close(); }

    private void Apply(object s, RoutedEventArgs e)
    {
        _result = _rows.Where(r => r.Selected).Select(r => r.Item.RelativePath).ToHashSet();
        Close();
    }
}
