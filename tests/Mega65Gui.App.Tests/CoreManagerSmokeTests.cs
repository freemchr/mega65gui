using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mega65Gui.App.Services;
using Mega65Gui.App.ViewModels;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Tests;
using Mega65Gui.Core.Update;

namespace Mega65Gui.App.Tests;

/// <summary>
/// UI smoke tests: the Core Manager page is built with the app's real resources, laid out at the
/// main window's size in every tab and view, and must produce no WPF binding errors. Each state
/// is also saved as a PNG in the test output folder (CoreManagerRender) for a visual check.
/// Nothing touches the network or a MEGA65: the profile is an empty temporary folder.
/// </summary>
public class CoreManagerSmokeTests
{
    private sealed class NoGate : IApprovalGate
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) Lines.Add(message); }
    }

    [Fact]
    public void Core_manager_renders_every_tab_without_binding_errors()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run()
    {
        // As in the running app, awaits resume on the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var home = new TempDir();
        Environment.SetEnvironmentVariable("MEGA65GUI_HOME", home.Path);
        // A plain Application with the app's resource dictionaries. Creating the real App would also
        // run its start-up (main window, error dialogs) inside the test process.
        var app = new Application { ThemeMode = ThemeMode.System };
        foreach (var dict in new[] { "Views/Converters.xaml", "Views/Pages.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Mega65Gui;component/{dict}") });

        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        var services = new AppServices();
        var vm = new CoreManagerViewModel(services, new NoGate());
        var outDir = Path.Combine(AppContext.BaseDirectory, "CoreManagerRender");
        Directory.CreateDirectory(outDir);

        var window = new Window
        {
            Width = 900, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, Content = new CoreManagerView { DataContext = vm, Margin = new Thickness(16) },
        };
        window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        window.Show();

        // Empty library first: the empty states must show.
        Render(window, Path.Combine(outDir, "1-library-empty.png"));
        Assert.True(vm.LibraryEmpty);

        // Add cores through the same path as drag-and-drop.
        var r6 = home.File("galaga_r6.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 1)));
        var r3 = home.File("galaga_r3.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 2), "Galaga", target: "mega65r3", modelId: 3));
        var bit = home.File("my-dev-build.bit", CoreFixtures.MakeBit(padding: 3));
        var bad = home.File("broken.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 4), damage: true));
        Pump(vm.ImportPathsAsync([r6, r3, bit, bad]));
        Assert.Equal(3, vm.LibraryRows.Count);
        Assert.Contains(vm.Log, l => l.Contains("CRC32"));

        vm.CompareAgainst = vm.CompareChoices.First(c => c.Model == Core.Device.HardwareModel.R6);
        vm.Selected = vm.LibraryRows.First(r => r.Entry.FileName == "galaga_r6.cor");
        Assert.False(vm.CanRun); // not connected
        Assert.True(vm.CanStage);
        vm.ShowFlashStepsCommand.Execute(null);
        Render(window, Path.Combine(outDir, "2-library-list.png"));

        vm.CardView = true;
        vm.Selected = vm.LibraryRows.First(r => r.Entry.Kind == Core.Cores.CoreFileKind.Bit);
        Assert.False(vm.CanStage);
        Render(window, Path.Combine(outDir, "3-library-cards.png"));

        for (var tab = 1; tab <= 3; tab++)
        {
            vm.SelectedTab = tab;
            Render(window, Path.Combine(outDir, $"{tab + 3}-tab{tab}.png"));
        }
        Assert.Equal(8, vm.SlotRows.Count);

        window.Close();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        Assert.True(errors.Lines.Count == 0, "Binding errors:\n" + string.Join("\n", errors.Lines));
    }

    /// <summary>Runs the dispatcher until the task finishes, checking on a dispatcher timer (no thread-pool hand-off).</summary>
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        var deadline = DateTime.Now.AddSeconds(60);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.Now > deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("The operation did not finish within 60 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static void Wait(int ms) => Pump(Task.Delay(ms));

    private static void Render(Window window, string file)
    {
        Wait(150);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)content.ActualWidth + 32, (int)content.ActualHeight + 32, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, bmp.Width, bmp.Height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(16, 16, content.ActualWidth, content.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
