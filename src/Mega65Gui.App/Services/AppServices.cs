using System.Net.Http;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Tools;

namespace Mega65Gui.App.Services;

/// <summary>Composition root shared by all pages.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        Paths = AppPaths.Default();
        // One cookie container so a Filehost sign-in applies to catalogue reads and downloads alike.
        Http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mega65Gui/0.1 (+https://github.com/freemchr/mega65gui)");
        Filehost = new FilehostClient(Http);
        Session = new FilehostSession(Http);
        Credentials = new CredentialStore(Path.Combine(Paths.Root, "filehost-account.json"));
        Catalogue = new CatalogueService(Filehost, Paths, session: Session);
        Downloads = new DownloadManager(Http, Paths);
        Runner = new ProcessRunner();
        Tools = new ToolManager(Paths, Runner);
        Tools.Load();
        Contiguity = new WindowsContiguityChecker();
    }

    public AppPaths Paths { get; }
    public HttpClient Http { get; }
    public FilehostClient Filehost { get; }
    public FilehostSession Session { get; }
    public CredentialStore Credentials { get; }
    public CatalogueService Catalogue { get; }
    public DownloadManager Downloads { get; }
    public IProcessRunner Runner { get; }
    public ToolManager Tools { get; }
    public IContiguityChecker Contiguity { get; }

    /// <summary>The connection chosen on the Device page, shared with Update My MEGA65.</summary>
    public ConnectionTarget? Connection { get; set; }
    public DeviceState Device { get; set; } = DeviceState.Unknown;

    public Mega65Commands? Commands => Tools.Current is { } t ? new Mega65Commands(t, Runner) : null;
}
