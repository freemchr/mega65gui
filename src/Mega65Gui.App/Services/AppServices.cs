using System.Net.Http;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Cores;
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
        Settings = new AppSettings(Paths.SettingsFile);
        Settings.Load();
        // One cookie container so a Filehost sign-in applies to catalogue reads and downloads alike.
        Http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Filehost = new FilehostClient(Http);
        Session = new FilehostSession(Http);
        Credentials = new CredentialStore(Path.Combine(Paths.Root, "filehost-account.json"));
        var anonymousHttp = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(2) };
        anonymousHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Catalogue = new CatalogueService(Filehost, Paths, session: Session, anonymousClient: new FilehostClient(anonymousHttp));
        Downloads = new DownloadManager(Http, Paths);
        Uploads = new FilehostUploads(Http, Session);
        GitHubHttp = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
        GitHubHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        GitHubToken = new SecretStore(Path.Combine(Paths.Root, "github-token.json"), "Mega65Gui.GitHub");
        GitHub = new GitHubReleases(GitHubHttp, Path.Combine(Paths.CatalogueCache, "github")) { Token = GitHubToken.Load() };
        UploadLinks = new UploadLinkStore(Path.Combine(Paths.Root, "upload-links.json"));
        UploadLinks.Load();
        Runner = new ProcessRunner();
        Tools = new ToolManager(Paths, Runner);
        Tools.Load();
        Contiguity = new WindowsContiguityChecker();
        CoreLibrary = new CoreLibrary(Paths.CoreLibrary, Paths.Extracted);
        CoreLibrary.Load();
    }

    /// <summary>This build's version, e.g. 1.2.0.</summary>
    public static Version AppVersion { get; } = AppReleaseCheck.Normalise(typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 1));

    private static string UserAgent => $"Mega65Gui/{AppReleaseCheck.Display(AppVersion)} (+https://github.com/freemchr/mega65gui)";

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public HttpClient Http { get; }
    public FilehostClient Filehost { get; }
    public FilehostSession Session { get; }
    public FilehostUploads Uploads { get; }
    /// <summary>Without the Filehost cookies, for GitHub's API and release downloads.</summary>
    public HttpClient GitHubHttp { get; }
    public GitHubReleases GitHub { get; }
    /// <summary>The optional GitHub token, DPAPI-encrypted like the Filehost password.</summary>
    public SecretStore GitHubToken { get; }
    public UploadLinkStore UploadLinks { get; }
    public CredentialStore Credentials { get; }
    public CatalogueService Catalogue { get; }
    public DownloadManager Downloads { get; }
    public IProcessRunner Runner { get; }
    public ToolManager Tools { get; }
    public IContiguityChecker Contiguity { get; }
    public CoreLibrary CoreLibrary { get; }

    /// <summary>Only one operation may use the MEGA65 at a time (see DeviceOperationGate).</summary>
    public DeviceOperationGate DeviceGate { get; } = new();

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
        await StopServingDiskAsync();
        ReleasePortRequested?.Invoke();
        await CloseFtpSessionAsync();
    }

    // ---- a D81 on this PC served as the MEGA65's drive 8 (m65 -d holds the port) ----

    private CancellationTokenSource? _serve;
    private Task? _serving;

    /// <summary>The disk image being served, or null.</summary>
    public string? ServedDisk { get; private set; }
    /// <summary>True when serving ended but the MEGA65 could not be told to use its own drive again (a reset fixes it).</summary>
    public bool VirtualDiskLeftOn { get; private set; }
    public event Action? ServedDiskChanged;

    /// <summary>
    /// Serves <paramref name="d81"/> until <see cref="StopServingDiskAsync"/>, or until another
    /// action needs the port. Returns m65's result if it ended by itself, null when stopped.
    /// </summary>
    public Task<ProcessResult?> ServeDiskAsync(string d81, IProgress<string>? output)
    {
        if (Connection is not { Kind: ConnectionKind.Serial } c) throw new InvalidOperationException("Serving a disk image needs the USB/JTAG connection.");
        var commands = Commands ?? throw new InvalidOperationException("Install the device tools first.");
        if (_serving is { IsCompleted: false }) throw new InvalidOperationException("A disk image is already being served. Stop it first.");
        var run = ServeAsync(commands, c, d81, output);
        _serving = run;
        return run;
    }

    private async Task<ProcessResult?> ServeAsync(Mega65Commands commands, ConnectionTarget c, string d81, IProgress<string>? output)
    {
        await ReleasePortAsync();
        using var claim = DeviceGate.Enter("Serving a disk image from this PC");
        _serve = new CancellationTokenSource();
        ServedDisk = d81;
        ServedDiskChanged?.Invoke();
        try
        {
            return await commands.ServeD81Async(c, d81, output, _serve.Token);
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            try
            {
                // m65 leaves the MEGA65's disk requests pointed at the PC; point them back at its own drive.
                VirtualDiskLeftOn = !await Task.Run(() => TurnOffVirtualDisk(c.Address));
            }
            finally
            {
                _serve.Dispose();
                _serve = null;
                ServedDisk = null;
                ServedDiskChanged?.Invoke();
            }
        }
    }

    private static bool TurnOffVirtualDisk(string port)
    {
        for (var i = 0; i < 6; i++)
        {
            try
            {
                using var monitor = MonitorConnection.Open(port);
                monitor.SendCommandAsync(Mega65Commands.VirtualD81Off).GetAwaiter().GetResult();
                Thread.Sleep(200);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(400); // the port is freed a moment after m65 ends
            }
        }
        return false;
    }

    /// <summary>Stops serving the disk image (if any) and waits until the port is free again.</summary>
    public async Task StopServingDiskAsync()
    {
        // Waits for the whole serve, including turning the virtual disk off, even when m65 already ended by itself.
        if (_serving is not { IsCompleted: false } serving) return;
        _serve?.Cancel();
        try { await serving; } catch (Exception e) when (e is IOException or InvalidOperationException) { }
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
        OtherCoreRunning = false;
        Connection = target;
        Device = target is null ? DeviceState.Unknown : state with { Connection = target };
        LostPort = target is null ? lostPort : null;
        ConnectionChanged?.Invoke();
    }

    /// <summary>
    /// Records that the FPGA now runs something else (a core loaded over JTAG). The model is
    /// unchanged; the core text says what was loaded so no page shows the previous core as running.
    /// </summary>
    public void NoteCoreReplaced(string description)
    {
        if (Connection is null) return;
        OtherCoreRunning = true;
        Device = Device with { CoreBuild = description, CoreGitHash = null };
        ConnectionChanged?.Invoke();
    }

    /// <summary>
    /// True after a core was loaded over JTAG, until the device is probed again. Such a core has no
    /// serial monitor or hypervisor, so mega65_ftp and m65 -Z cannot work until the MEGA65 core runs.
    /// </summary>
    public bool OtherCoreRunning { get; private set; }

    /// <summary>The MEGA65 core is running and answering over USB/JTAG, so card and slot tools can work.</summary>
    public bool Mega65CoreAnswering => Connection is { Kind: ConnectionKind.Serial } && !Device.NoResponse && !OtherCoreRunning;

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

    // ---- New MEGA65 GUI versions on GitHub ----

    /// <summary>A newer release than this build, once a check found one.</summary>
    public GitHubRelease? NewRelease { get; private set; }
    /// <summary>When the last check finished, and what it said ("You have the newest version." or the problem).</summary>
    public string? NewReleaseCheckText { get; private set; }
    public event Action? NewReleaseChanged;

    /// <summary>Looks for a newer release. Problems (offline, GitHub's limit) only go into the check text.</summary>
    public async Task CheckForNewVersionAsync(CancellationToken ct = default)
    {
        try
        {
            NewRelease = await AppReleaseCheck.CheckAsync(GitHub, AppVersion, ct);
            NewReleaseCheckText = NewRelease is { } r
                ? $"Version {AppReleaseCheck.Display(AppReleaseCheck.ParseTag(r.Tag)!)} is available."
                : $"You have the newest version ({AppReleaseCheck.Display(AppVersion)}).";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FilehostUploadException or System.Text.Json.JsonException)
        {
            NewReleaseCheckText = $"Could not check for a new version: {e.Message}";
        }
        NewReleaseChanged?.Invoke();
    }
}
