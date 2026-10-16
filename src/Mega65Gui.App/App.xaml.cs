using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mega65Gui.App.Services;
using Mega65Gui.App.ViewModels;

namespace Mega65Gui.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // WPF bindings format dates as en-US unless told otherwise; follow the user's regional settings.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MEGA65 GUI", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // "--self-test COM3": run the hardware self-test without a window and write a report.
        var st = Array.IndexOf(e.Args, "--self-test");
        if (st >= 0)
        {
            await RunSelfTestAsync(st + 1 < e.Args.Length ? e.Args[st + 1] : "COM3");
            return;
        }

        var vm = new MainViewModel(new AppServices());
        var main = new MainWindow { DataContext = vm };
        MainWindow = main;
        main.Show();

        // Developer aid: "--render-pages <folder>" saves a PNG of every page and exits.
        var i = Array.IndexOf(e.Args, "--render-pages");
        if (i >= 0 && i + 1 < e.Args.Length) await RenderPagesAsync(main, vm, e.Args[i + 1]);
    }

    private async Task RunSelfTestAsync(string port)
    {
        var services = new AppServices();
        var report = new System.Text.StringBuilder($"MEGA65 GUI hardware self-test on {port}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");
        var exitCode = 1;
        try
        {
            var tools = services.Tools.Current ?? throw new InvalidOperationException("Device tools are not installed.");
            var test = new Mega65Gui.Core.Device.HardwareSelfTest(tools, services.Runner, port);
            var results = Environment.GetCommandLineArgs().Contains("--extended")
                ? await RunExtendedAsync(test, services)
                : await test.RunAsync();
            foreach (var r in results) report.AppendLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}  ({r.Detail})");
            report.AppendLine($"\n{results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed)} failed");
            exitCode = results.All(r => r.Passed) ? 0 : 1;
        }
        catch (Exception ex) { report.AppendLine("ERROR  " + ex.Message); }
        var file = Path.Combine(services.Paths.Reports, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(file, report.ToString());
        await File.WriteAllTextAsync(Path.Combine(services.Paths.Reports, "selftest-latest.txt"), report.ToString());
        Shutdown(exitCode);
    }

    /// <summary>Extended checks use the ROM and HICKUP from the newest extracted release package.</summary>
    private static async Task<IReadOnlyList<Mega65Gui.Core.Device.SelfTestResult>> RunExtendedAsync(Mega65Gui.Core.Device.HardwareSelfTest test, AppServices services)
    {
        var rom = Directory.EnumerateFiles(services.Paths.Extracted, "MEGA65.ROM", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault() ?? throw new FileNotFoundException("No release package with MEGA65.ROM has been downloaded yet.");
        var pkg = Path.GetDirectoryName(Path.GetDirectoryName(rom))!;
        var hickup = Path.Combine(pkg, "extra", "HICKUP.M65");
        var shot = Path.Combine(services.Paths.Reports, "selftest-screenshot.png");
        return await test.RunExtendedAsync(rom, hickup, shot);
    }

    private async Task RenderPagesAsync(Window window, MainViewModel vm, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var page in vm.Pages)
        {
            vm.Selected = page;
            await page.OnShownAsync();
            if (page is CatalogueViewModel cat)
                cat.SelectedFile = cat.Results.FirstOrDefault(f => f.Title.Contains("Release Package (mega65r6)"));
            await Task.Delay(300);
            for (var n = 0; n < 50 && page.IsBusy; n++) await Task.Delay(200);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var dpi = VisualTreeHelper.GetDpi(window);
            var bmp = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(window);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(folder, $"{vm.Pages.ToList().IndexOf(page) + 1}-{page.Title.Replace(' ', '-')}.png"));
            enc.Save(fs);
        }
        Shutdown();
    }
}
