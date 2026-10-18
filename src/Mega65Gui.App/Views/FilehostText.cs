using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Mega65Gui.App.Views;

/// <summary>
/// Attached behaviour: shows a Filehost description in a TextBlock the way the website shows it.
/// The website's only formatting is #bs#…#be# for bold, line breaks as typed, and web addresses
/// turned into links (html/filedetail.php).
/// </summary>
public static partial class FilehostText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(FilehostText), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    [GeneratedRegex(@"#bs#|#be#", RegexOptions.IgnoreCase)]
    private static partial Regex BoldMarker();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>()]*[^\s<>().,;:!?""']", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        var text = System.Net.WebUtility.HtmlDecode((string?)e.NewValue ?? "").Replace("\r\n", "\n");
        var bold = false;
        var last = 0;
        foreach (Match m in BoldMarker().Matches(text))
        {
            AddText(block, text[last..m.Index], bold);
            bold = m.Value.Equals("#bs#", StringComparison.OrdinalIgnoreCase);
            last = m.Index + m.Length;
        }
        AddText(block, text[last..], bold);
    }

    private static void AddText(TextBlock block, string text, bool bold)
    {
        var last = 0;
        foreach (Match m in Link().Matches(text))
        {
            AddRun(block, text[last..m.Index], bold);
            var url = m.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + m.Value : m.Value;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(new Run(m.Value)) { NavigateUri = uri, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                link.RequestNavigate += (_, args) => Process.Start(new ProcessStartInfo(args.Uri.ToString()) { UseShellExecute = true });
                block.Inlines.Add(link);
            }
            else AddRun(block, m.Value, bold);
            last = m.Index + m.Length;
        }
        AddRun(block, text[last..], bold);
    }

    private static void AddRun(TextBlock block, string text, bool bold)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) block.Inlines.Add(new Run(lines[i]) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        }
    }
}
