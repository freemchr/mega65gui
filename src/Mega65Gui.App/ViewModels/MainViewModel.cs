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
        CardBrowser = new CardBrowserViewModel(services);
        DiskRomTools = new ToolsPageViewModel(services);
        SdSync = new SdSyncViewModel(services);
        Tools = new ToolsViewModel(services);
        Features = new FeaturesViewModel();
        Pages = [Update, Catalogue, Device, CardBrowser, SdSync, DiskRomTools, Tools, Features];
        Shell = new ShellStatusViewModel(services)
        {
            Navigate = title => Selected = Pages.First(p => p.Title == title),
        };
        _selected = Update;
    }

    public ShellStatusViewModel Shell { get; }

    public UpdateViewModel Update { get; }
    public CatalogueViewModel Catalogue { get; }
    public DeviceViewModel Device { get; }
    public CardBrowserViewModel CardBrowser { get; }
    public ToolsPageViewModel DiskRomTools { get; }
    public SdSyncViewModel SdSync { get; }
    public ToolsViewModel Tools { get; }
    public FeaturesViewModel Features { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selected;

    partial void OnSelectedChanging(PageViewModel value)
    {
        // Leaving the card browser releases the serial port for everything else.
        if (_selected == CardBrowser && value != CardBrowser) _ = CardBrowser.OnHiddenAsync();
    }

    partial void OnSelectedChanged(PageViewModel value)
    {
        Shell.Refresh();
        _ = value.OnShownAsync();
    }
}
