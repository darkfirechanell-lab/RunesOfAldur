using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace RunesOfAldur;

public class RunesOfAldurSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Debug Log")]
    public ToggleNode DebugLog { get; set; } = new(false);

    [Menu("Show price on all rows")]
    public ToggleNode ShowPriceOnAll { get; set; } = new(false);

    [Menu("Best border thickness")]
    public RangeNode<int> BestBorderThickness { get; set; } = new(3, 1, 8);

    [Menu("Other border thickness")]
    public RangeNode<int> OtherBorderThickness { get; set; } = new(1, 1, 8);

    [Menu("Divine threshold (divine)", "Show in divine when value >= this many divine")]
    public RangeNode<float> ShowDivineThreshold { get; set; } = new(1.0f, 0.1f, 100f);

    [IgnoreMenu]
    public RangeNode<float> PriceXPercent { get; set; } = new(42f, 0f, 100f);
}
