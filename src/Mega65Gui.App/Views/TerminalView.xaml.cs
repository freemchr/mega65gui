using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mega65Gui.App.ViewModels;
using Mega65Gui.Core.Device;

namespace Mega65Gui.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TerminalViewModel old) old.TranscriptAppended -= ScrollToEnd;
            if (e.NewValue is TerminalViewModel vm) vm.TranscriptAppended += ScrollToEnd;
        };
    }

    private TerminalViewModel? Vm => DataContext as TerminalViewModel;

    private void ScrollToEnd() => Output.ScrollToEnd();

    private void CommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.SendCommand.Execute(null);
    }

    private void KeyboardOn(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(() => KeyCapture.Focus());
    private void CaptureClick(object sender, MouseButtonEventArgs e) => KeyCapture.Focus();

    private async void CaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { KeyboardActive: true } vm) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl or Alt with a letter or digit: the same key with the MEGA65's CTRL or ALT held.
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        // AltGr reports as Ctrl+Alt; leave it to type its character (@, { … on many layouts).
        if ((ctrl ^ alt) && KeyChar(key) is { } c && KeyMatrix.ForChar(c) is { } plain)
        {
            e.Handled = true;
            await vm.PressAsync(KeyMatrix.With(plain, ctrl ? KeyMatrix.Ctrl : KeyMatrix.Alt));
            return;
        }

        SpecialKey? special = key switch
        {
            Key.Enter => SpecialKey.Return,
            Key.Back => SpecialKey.InstDel,
            Key.Left => SpecialKey.Left,
            Key.Right => SpecialKey.Right,
            Key.Up => SpecialKey.Up,
            Key.Down => SpecialKey.Down,
            Key.Home => shift ? SpecialKey.Clr : SpecialKey.Home,
            Key.Escape => SpecialKey.RunStop,
            Key.F1 => SpecialKey.F1, Key.F2 => SpecialKey.F2, Key.F3 => SpecialKey.F3, Key.F4 => SpecialKey.F4,
            Key.F5 => SpecialKey.F5, Key.F6 => SpecialKey.F6, Key.F7 => SpecialKey.F7, Key.F8 => SpecialKey.F8,
            Key.F9 => SpecialKey.F9, Key.F10 => SpecialKey.F10, Key.F11 => SpecialKey.F11, Key.F12 => SpecialKey.F12,
            Key.Tab => SpecialKey.Tab,
            Key.Scroll => SpecialKey.NoScroll,
            _ => null,
        };
        if (special is { } k)
        {
            e.Handled = true;
            await vm.PressAsync(KeyMatrix.For(k));
        }
    }

    private static char? KeyChar(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        _ => null,
    };

    private async void CaptureTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Vm is not { KeyboardActive: true } vm) return;
        e.Handled = true;
        foreach (var c in e.Text)
            if (KeyMatrix.ForChar(c) is { } key) await vm.PressAsync(key);
    }
}
