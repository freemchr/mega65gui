using CommunityToolkit.Mvvm.ComponentModel;
using Mega65Gui.App.Services;

namespace Mega65Gui.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Update = new UpdateViewModel(services, new WpfApprovalGate());
        Catalogue = new CatalogueViewModel(services);
        MyUploads = new MyUploadsViewModel(services, new WpfApprovalGate());
        Device = new DeviceViewModel(services);
        CoreManager = new CoreManagerViewModel(services, new WpfApprovalGate());
        CardBrowser = new CardBrowserViewModel(services);
        DiskRomTools = new ToolsPageViewModel(services);
        Terminal = new TerminalViewModel(services);
        About = new AboutViewModel(services);
        SdSync = new SdSyncViewModel(services);
        Tools = new ToolsViewModel(services);
        Features = new FeaturesViewModel();
        Pages = [Update, Catalogue, MyUploads, Device, CoreManager, CardBrowser, Terminal, SdSync, DiskRomTools, Tools, Features, About];
        Shell = new ShellStatusViewModel(services)
        {
            Navigate = title => Selected = Pages.First(p => p.Title == title),
        };
        CoreManager.Navigate = Shell.Navigate;
        CardBrowser.Navigate = Shell.Navigate;
        Shell.ReportProblem = About.ReportProblem = () => Views.ReportProblemWindow.Show(ProblemDetails.Collect(services, Selected));
        _selected = Update;
    }

    public ShellStatusViewModel Shell { get; }

    public UpdateViewModel Update { get; }
    public CatalogueViewModel Catalogue { get; }
    public MyUploadsViewModel MyUploads { get; }
    public DeviceViewModel Device { get; }
    public CoreManagerViewModel CoreManager { get; }
    public CardBrowserViewModel CardBrowser { get; }
    public ToolsPageViewModel DiskRomTools { get; }
    public TerminalViewModel Terminal { get; }
    public AboutViewModel About { get; }
    public SdSyncViewModel SdSync { get; }
    public ToolsViewModel Tools { get; }
    public FeaturesViewModel Features { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selected;

    partial void OnSelectedChanging(PageViewModel value)
    {
        // Leaving the card browser releases the serial port for everything else.
        if (_selected == CardBrowser && value != CardBrowser) _ = CardBrowser.OnHiddenAsync();
        if (_selected == Terminal && value != Terminal) _ = Terminal.OnHiddenAsync();
    }

    partial void OnSelectedChanged(PageViewModel value)
    {
        Shell.Refresh();
        _ = value.OnShownAsync();
    }
}
