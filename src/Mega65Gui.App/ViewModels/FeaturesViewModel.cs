using System.ComponentModel;
using System.Windows.Data;
using Mega65Gui.Core.Features;

namespace Mega65Gui.App.ViewModels;

public sealed class FeaturesViewModel : PageViewModel
{
    public FeaturesViewModel() : base("Features", "\uE9D5")
    {
        View = CollectionViewSource.GetDefaultView(FeatureCatalog.All);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Feature.Area)));
        var label = new Views.EnumLabelConverter();
        Counts = string.Join(" · ", FeatureCatalog.All.GroupBy(f => f.Status).OrderBy(g => g.Key)
            .Select(g => $"{label.Convert(g.Key, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)}: {g.Count()}"));
    }

    public ICollectionView View { get; }
    public string Counts { get; }
}
