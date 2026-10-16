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
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MEGA65 GUI", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var vm = new MainViewModel(new AppServices());
        var main = new MainWindow { DataContext = vm };
        MainWindow = main;
        main.Show();

        // Developer aid: "--render-pages <folder>" saves a PNG of every page and exits.
        var i = Array.IndexOf(e.Args, "--render-pages");
        if (i >= 0 && i + 1 < e.Args.Length) await RenderPagesAsync(main, vm, e.Args[i + 1]);
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
