using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Interfaces;
using Newtonsoft.Json;
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
    [Menu("Recommend Only Cost Efficient Recruits", "In the recruitment panel only show the green recommendation badge\n" +
                                                    "on workers, where their highest paid job type is an upgrade to an\n" +
                                                    "existing worker")]
    public ToggleNode RecommendOnlyCostEfficientRecruits { get; set; } = new ToggleNode(false);
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.Green);
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.White);
    public ColorNode BadColor { get; set; } = new ColorNode(Color.Red);

    [JsonIgnore]
    public ButtonNode ExportWorkerStats { get; set; } = new ButtonNode();

    public bool WindowShown = true;
}