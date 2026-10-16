using CommunityToolkit.Mvvm.ComponentModel;

namespace Mega65Gui.App.ViewModels;

public abstract partial class PageViewModel(string title, string glyph) : ObservableObject
{
    public string Title { get; } = title;
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; } = glyph;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    /// <summary>Runs an operation with busy state and turns failures into a status message instead of a crash.</summary>
    protected async Task GuardAsync(Func<Task> action, string? busyText = null)
    {
        IsBusy = true;
        if (busyText is not null) Status = busyText;
        try { await action(); }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception e) { Status = $"Error: {e.Message}"; }
        finally { IsBusy = false; }
    }

    public virtual Task OnShownAsync() => Task.CompletedTask;
}
