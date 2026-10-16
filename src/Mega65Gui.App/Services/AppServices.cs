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
        var anonymousHttp = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(2) };
        anonymousHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mega65Gui/0.1 (+https://github.com/freemchr/mega65gui)");
        Catalogue = new CatalogueService(Filehost, Paths, session: Session, anonymousClient: new FilehostClient(anonymousHttp));
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

    /// <summary>The current device connection, shared by the Device page, Update My MEGA65 and the sidebar.</summary>
    public ConnectionTarget? Connection { get; private set; }
    public DeviceState Device { get; private set; } = DeviceState.Unknown;

    /// <summary>Serial port of a connection that dropped (MEGA65 off or unplugged); null otherwise.</summary>
    public string? LostPort { get; private set; }

    /// <summary>Raised on the UI thread whenever the connection or device state changes.</summary>
    public event Action? ConnectionChanged;

    // ---- mega65_ftp session (SD card browser). It holds the serial port, so anything else that
    // needs the port closes it first via CloseFtpSessionAsync.
    private FtpSession? _ftp;
    private readonly SemaphoreSlim _ftpLock = new(1, 1);

    public bool HasFtpSession => _ftp is { IsOpen: true };
    public FtpSession? CurrentFtpSession => _ftp;
    public event Action? FtpSessionChanged;

    public async Task<FtpSession> GetFtpSessionAsync(CancellationToken ct = default)
    {
        await _ftpLock.WaitAsync(ct);
        try
        {
            if (_ftp is { IsOpen: true }) return _ftp;
            ReleasePortRequested?.Invoke();
            var target = Connection ?? throw new InvalidOperationException("Connect to the MEGA65 first.");
            var commands = Commands ?? throw new InvalidOperationException("Install the device tools first.");
            _ftp = await commands.OpenFtpSessionAsync(target, ct);
            FtpSessionChanged?.Invoke();
            return _ftp;
        }
        finally { _ftpLock.Release(); }
    }

    /// <summary>Raised when something needs the serial port; the terminal closes its connection.</summary>
    public event Action? ReleasePortRequested;

    /// <summary>Frees the serial port: closes the terminal and the card browser session.</summary>
    public async Task ReleasePortAsync()
    {
        ReleasePortRequested?.Invoke();
        await CloseFtpSessionAsync();
    }

    /// <summary>Ends the browser session (the MEGA65 resets as the helper unloads).</summary>
    public async Task CloseFtpSessionAsync()
    {
        await _ftpLock.WaitAsync();
        try
        {
            if (_ftp is null) return;
            var s = _ftp;
            _ftp = null;
            await s.DisposeAsync();
            FtpSessionChanged?.Invoke();
        }
        finally { _ftpLock.Release(); }
    }

    /// <summary>Connects over USB/JTAG and reads model, core and ROM (read-only monitor commands).</summary>
    public async Task<DeviceState> ConnectSerialAsync(string port, CancellationToken ct = default)
    {
        await ReleasePortAsync();
        var target = ConnectionTarget.Serial(port);
        var state = await Task.Run(() => new DeviceProbe().ProbeAsync(target, ct), ct);
        SetConnection(target, state);
        return Device;
    }

    public void SetConnection(ConnectionTarget? target, DeviceState state, string? lostPort = null)
    {
        if (target is null && _ftp is not null) _ = CloseFtpSessionAsync();
        Connection = target;
        Device = target is null ? DeviceState.Unknown : state with { Connection = target };
        LostPort = target is null ? lostPort : null;
        ConnectionChanged?.Invoke();
    }

    public Mega65Commands? Commands => Tools.Current is { } t ? new Mega65Commands(t, Runner) : null;

    /// <summary>Raised on the UI thread's caller when tools are installed, replaced or an install starts/ends.</summary>
    public event Action? ToolsChanged;
    public bool IsInstallingTools { get; private set; }

    /// <summary>
    /// Installs the official device tools from the Filehost. The development build is the default
    /// because it includes etherload, which Ethernet features need.
    /// </summary>
    public async Task<ToolInstallation> InstallDeviceToolsAsync(ToolChannel channel = ToolChannel.Development, IProgress<string>? progress = null)
    {
        if (IsInstallingTools) throw new InvalidOperationException("The device tools are already being installed.");
        IsInstallingTools = true;
        ToolsChanged?.Invoke();
        try
        {
            return await Tools.InstallFromFilehostAsync(Catalogue, Downloads, channel, progress);
        }
        finally
        {
            IsInstallingTools = false;
            ToolsChanged?.Invoke();
        }
    }

    public void NotifyToolsChanged() => ToolsChanged?.Invoke();
}
