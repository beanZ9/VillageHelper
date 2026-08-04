using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Interfaces;
using SharpDX;

namespace VillageHelper;

public class VillageHelperSettings : ISettings
{
    //Mandatory setting to allow enabling/disabling your plugin
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public HotkeyNodeV2 ToggleWindowHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public ToggleNode ShowResources { get; set; } = new ToggleNode(true);
    public ToggleNode ShowActions { get; set; } = new ToggleNode(true);
    public ToggleNode ShowUpgrades { get; set; } = new ToggleNode(true);
    public ToggleNode ShowEmptyResources { get; set; } = new ToggleNode(true);
    public ToggleNode ShowEmptyResourcesInColor { get; set; } = new ToggleNode(true);
    public ToggleNode ShowStatusOverlay { get; set; } = new ToggleNode(true);
    public RangeNode<int> StatusOverlayXOffset { get; set; } = new RangeNode<int>(0, 0, 200);
    public RangeNode<int> StatusOverlayYOffset { get; set; } = new RangeNode<int>(0, 0, 200);
    public RangeNode<float> StatusOverlayUpdatePeriod { get; set; } = new(1, 0, 10);
    public ToggleNode ShowProjectedCurrentGold { get; set; } = new ToggleNode(false);

    public ToggleNode ShowWorkerUpgradeTips { get; set; } = new ToggleNode(true);
    [Menu("Show Only Cost Efficient Upgrade Tips", "Base upgrade recommendations only on a worker's highest paid skill.\n" +
                                                   "A worker who is an upgrade at some cheaper skill is still being paid full wage for" +
                                                   "their most expensive skill.\n" +
                                                   "Avoids overpaying wage but consequently can increase reroll cost initially.")]
    public ToggleNode ShowOnlyCostEfficientUpgrades { get; set; } = new ToggleNode(true);
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.Green);
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.White);
    public ColorNode BadColor { get; set; } = new ColorNode(Color.Red);
    
    public ToggleNode EnableAutoRecruiter { get; set; } = new ToggleNode(true);

    [IgnoreMenu] public AutoRecruiterSettings AutoRecruiter { get; set; } = new AutoRecruiterSettings();
    
    public bool WindowShown = true;
}

public class AutoRecruiterSettings {
    [IgnoreMenu] public ToggleNode UseHighQualityReroll { get; set; } = new ToggleNode(false);

    [IgnoreMenu] public ToggleNode OnlyCostEfficient { get; set; } = new ToggleNode(true);

    [IgnoreMenu]
    public RangeNode<int> MaxGoldToSpend { get; set; } = new RangeNode<int>(100000, 0, 2000000);

    [IgnoreMenu] public RangeNode<int> ClickDelayMs { get; set; } = new RangeNode<int>(500, 50, 2000);

    [IgnoreMenu] public ToggleNode CancelWithRightMouseButton { get; set; } = new ToggleNode(true);
    
    [IgnoreMenu] public Dictionary<string, VillageHelper.RecruiterJobTarget> Targets { get; set; } = new()
    {
        ["Mining"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 12, AlwaysHireUpgrades = false },
        ["Smelting"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 6, AlwaysHireUpgrades = false },
        ["Farming"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 18, AlwaysHireUpgrades = false },
        ["Disenchanting"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 6, AlwaysHireUpgrades = false },
        ["Mapping"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 18, AlwaysHireUpgrades = true },
        ["Shipping"] = new VillageHelper.RecruiterJobTarget { MinRank = 5, TargetQuantity = 6, AlwaysHireUpgrades = false },
        ["Fishing"] = new VillageHelper.RecruiterJobTarget { MinRank = 1, TargetQuantity = 0, AlwaysHireUpgrades = false },
    };
}