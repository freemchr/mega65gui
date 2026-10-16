using Mega65Gui.Core.Features;

namespace Mega65Gui.App.ViewModels;

/// <summary>Features grouped by how far along they are: working, built but untested on a MEGA65, and still to do.</summary>
public sealed class FeaturesViewModel : PageViewModel
{
    public sealed record FeatureGroup(FeatureStatus Status, string Title, string Description, string Glyph, IReadOnlyList<Feature> Items)
    {
        public int Count => Items.Count;
    }

    private static readonly (FeatureStatus Status, string Title, string Description, string Glyph)[] Order =
    [
        (FeatureStatus.Implemented, "Working", "Built and tested. Features that use a MEGA65 have been checked on a real machine.", ""),
        (FeatureStatus.HardwareUnverified, "Built, not yet tested on a MEGA65", "These should work but have not been tried on real hardware yet. Please report any problems.", ""),
        (FeatureStatus.Partial, "Partly done", "Some of this works; the rest is still to come.", ""),
        (FeatureStatus.Planned, "Planned", "Not built yet.", ""),
    ];

    public FeaturesViewModel() : base("Features", "")
    {
        Groups = Order
            .Select(o => new FeatureGroup(o.Status, o.Title, o.Description, o.Glyph,
                FeatureCatalog.All.Where(f => f.Status == o.Status).ToList()))
            .Where(g => g.Count > 0)
            .ToList();
    }

    public IReadOnlyList<FeatureGroup> Groups { get; }
    public int Total => FeatureCatalog.All.Count;
}
