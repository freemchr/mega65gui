using CommunityToolkit.Mvvm.ComponentModel;
using Mega65Gui.App.Services;

namespace Mega65Gui.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Update = new UpdateViewModel(services, new WpfApprovalGate());
        Catalogue = new CatalogueViewModel(services);
        Device = new DeviceViewModel(services);
        SdSync = new SdSyncViewModel(services);
        Tools = new ToolsViewModel(services);
        Features = new FeaturesViewModel();
        Pages = [Update, Catalogue, Device, SdSync, Tools, Features];
        Shell = new ShellStatusViewModel(services);
        _selected = Update;
    }

    public ShellStatusViewModel Shell { get; }

    public UpdateViewModel Update { get; }
    public CatalogueViewModel Catalogue { get; }
    public DeviceViewModel Device { get; }
    public SdSyncViewModel SdSync { get; }
    public ToolsViewModel Tools { get; }
    public FeaturesViewModel Features { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selected;

    partial void OnSelectedChanged(PageViewModel value)
    {
        Shell.Refresh();
        _ = value.OnShownAsync();
    }
}
