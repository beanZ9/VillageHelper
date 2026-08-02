using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.FilesInMemory.Village;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace VillageHelper;

public class VillageHelper : BaseSettingsPlugin<VillageHelperSettings>
{
    private TimeCache<List<DrawItem>> _overlayCache;
    private Dictionary<int, TimeSpan> _previousShipmentTimes;
    private readonly Dictionary<int, DateTime> _shipmentResetTimes = new ();
    private readonly Dictionary<int, TimeSpan> _shipmentResetValues = new ();
    
    public class WorkerWageSample
    {
        public int WagePerHour { get; set; }
        public Dictionary<string, int> JobRanks { get; set; } = new();
    }
    
    public Dictionary<string, WorkerWageSample> WorkerWageSamples { get; set; } = new();
    private bool _averageWorkerWagesDirty = true;
    private Dictionary<string, float> _averageWorkerWagesCache;

    private Dictionary<string, float> AverageWorkerWages
    {
        get
        {
            if (_averageWorkerWagesDirty)
            {
                _averageWorkerWagesCache = ComputeAverageWorkerWages();
                _averageWorkerWagesDirty = false;
            }

            return _averageWorkerWagesCache;
        }
    }

    private Dictionary<string, float> ComputeAverageWorkerWages()
    {
        var samples = WorkerWageSamples.Values.Where(s => s.JobRanks.Count > 0).ToList();

        // Seed: use only single-skill-workers to establish a predictive average wage estimate for each job type
        var k = new Dictionary<string, float>();
        foreach (var group in samples.Where(s => s.JobRanks.Count == 1)
                     .Select(s => (jobId: s.JobRanks.Keys.Single(),
                         speed: (float)GameController.Game.Files.VillageJobSkillLevels.GetByLevel(s.JobRanks.Values.Single()).Speed,
                         wage: (float)s.WagePerHour))
                     .GroupBy(x => x.jobId))
        {
            k[group.Key] = group.Sum(x => x.speed * x.wage) / group.Sum(x => x.speed * x.speed);
        }

        // Refine: classify every sample and resample until it settles. usually 1-2 runs. 5 max should be plenty here. job types are (at least rn) distinctive enough and if they were not, I guess it doesn't really matter, right? 
        for (var pass = 0; pass < 5; pass++)
        {
            var sumSw = new Dictionary<string, float>();
            var sumSs = new Dictionary<string, float>();

            foreach (var sample in samples)
            {
                var (jobId, speed, _) = sample.JobRanks
                    .Select(r => (jobId: r.Key, speed: (float)GameController.Game.Files.VillageJobSkillLevels.GetByLevel(r.Value).Speed))
                    .Select(x => (x.jobId, x.speed, predicted: k.GetValueOrDefault(x.jobId, 0f) * x.speed))
                    .MaxBy(x => x.predicted);

                sumSw[jobId] = sumSw.GetValueOrDefault(jobId) + speed * sample.WagePerHour;
                sumSs[jobId] = sumSs.GetValueOrDefault(jobId) + speed * speed;
            }

            foreach (var jobId in sumSs.Keys)
            {
                if (sumSs[jobId] > 0)
                {
                    k[jobId] = sumSw[jobId] / sumSs[jobId];
                }
            }
        }

        return k;
    }

    private void RecordWorker(BaseVillageWorker worker)
    {
        if (WorkerWageSamples.ContainsKey(worker.WorkerName))
        {
            return;
        }

        WorkerWageSamples[worker.WorkerName] = new WorkerWageSample
        {
            WagePerHour = worker.WagePerHour,
            JobRanks = worker.JobRanks.ToDictionary(r => r.Key.Id, r => r.Value),
        };
        _averageWorkerWagesDirty = true;
        _workerWageSamplesDirty = true;
    }
    
    private List<(TElement element, TWorker worker)> SafeJoinWorkersByName<TElement, TWorker>(
        IEnumerable<TElement> elements, IEnumerable<TWorker> workers,
        Func<TElement, bool> isVisible, Func<TElement, string> elementWorkerName, Func<TWorker, string> workerWorkerName)
    {
        try
        {
            var visibleElements = elements.Where(isVisible).ToList();
            var workerList = workers.ToList();

            var matches = visibleElements
                .Join(workerList, elementWorkerName, workerWorkerName, (el, worker) => (element: el, worker))
                .ToList();
            
            // Some workers display a WorkerName in the UI element that doesn't match the worker record
            // (e.g. element says "Esile Waldegrave" but backing worker is "Esile Norene")
            // attempt to match leftovers by position
            var matchedElements = matches.Select(m => m.element).ToHashSet();
            var matchedWorkers = matches.Select(m => m.worker).ToHashSet();
            var leftoverElements = visibleElements.Where(el => !matchedElements.Contains(el));
            var leftoverWorkers = workerList.Where(w => !matchedWorkers.Contains(w));
            matches.AddRange(leftoverElements.Zip(leftoverWorkers));

            return matches;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to join worker elements: {ex}");
            return [];
        }
    }

    private string WorkerWageSamplesPath => Path.Combine(ConfigDirectory, "WorkerWageSamples.json");
    private bool _workerWageSamplesDirty;

    private void SaveWorkerWageSamples()
    {
        try
        {
            File.WriteAllText(WorkerWageSamplesPath, JsonConvert.SerializeObject(WorkerWageSamples, Formatting.Indented));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to save worker wage samples: {ex}");
        }
    }


    public override bool Initialise()
    {
        GameController.LeftPanel.WantUse(() => true);

        try
        {
            if (File.Exists(WorkerWageSamplesPath))
            {
                WorkerWageSamples = JsonConvert.DeserializeObject<Dictionary<string, WorkerWageSample>>(File.ReadAllText(WorkerWageSamplesPath)) ?? new();
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to load worker wage samples: {ex}");
        }

        Settings.ExportWorkerStats.OnPressed += () =>
        {
            var samplesByJob = new Dictionary<string, List<string>>();

            foreach (var seen in WorkerWageSamples.Values)
            {
                if (seen.JobRanks.Count == 0 || !seen.JobRanks.Keys.All(AverageWorkerWages.ContainsKey))
                {
                    continue;
                }

                var (dominantJobId, _) = seen.JobRanks
                    .Select(r => (jobId: r.Key, predicted: AverageWorkerWages[r.Key] * GameController.Game.Files.VillageJobSkillLevels.GetByLevel(r.Value).Speed))
                    .MaxBy(x => x.predicted);

                if (!samplesByJob.TryGetValue(dominantJobId, out var samples))
                {
                    samplesByJob[dominantJobId] = samples = [];
                }

                var speed = GameController.Game.Files.VillageJobSkillLevels.GetByLevel(seen.JobRanks[dominantJobId]).Speed;
                samples.Add($"{{{speed},{seen.WagePerHour}}}");
            }

            var text = $"# {WorkerWageSamples.Count} unique workers captured so far\n";
            foreach (var jobType in GameController.Game.Files.VillageJobTypes.EntriesList)
            {
                var samples = samplesByJob.GetValueOrDefault(jobType.Id, []);
                text += $"{jobType.Id} {{{string.Join(",", samples)}}}\n";
            }

            ImGui.SetClipboardText(text);
        };

        _overlayCache = new TimeCache<List<DrawItem>>(() =>
        {
            var villageScreen = GameController.IngameState.IngameUi.VillageScreen;
            try {
                return ComputeStatusOverlayDrawItems(villageScreen,
                    Settings.ShowProjectedCurrentGold ? villageScreen.CurrentGold : villageScreen.ZoneLoadResources.Gold);
            } catch (Exception ex) { return new List<DrawItem>(); }
        }, (long)(Settings.StatusOverlayUpdatePeriod.Value * 1000));
        Settings.StatusOverlayUpdatePeriod.OnValueChanged += (sender, f) => _overlayCache.NewTime((long)(f * 1000));
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
    }

    public override Job Tick()
    {
        if (_workerWageSamplesDirty)
        {
            SaveWorkerWageSamples();
            _workerWageSamplesDirty = false;
        }

        return null;
    }

    public override void DrawSettings()
    {
        base.DrawSettings();

        if (ImGui.TreeNode("Worker Wage Calibration"))
        {
            ImGui.Text($"{WorkerWageSamples.Count} unique workers recorded");
            if (ImGui.Button("Reset calibration data"))
            {
                WorkerWageSamples.Clear();
                _averageWorkerWagesDirty = true;
            }

            var averages = AverageWorkerWages;
            if (ImGui.BeginTable("AverageWorkerWagesTable", 2, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Job");
                ImGui.TableSetupColumn("Avg GPH/work unit");
                ImGui.TableHeadersRow();
                foreach (var jobType in GameController.Game.Files.VillageJobTypes.EntriesList)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(jobType.Id);
                    ImGui.TableNextColumn();
                    ImGui.Text(averages.TryGetValue(jobType.Id, out var avg) ? avg.ToString("F2") : "-");
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }
    }

    public override void Render()
    {
        if (Settings.ToggleWindowHotkey.PressedOnce())
        {
            Settings.WindowShown = !Settings.WindowShown;
        }

        var villageScreen = GameController.IngameState.IngameUi.VillageScreen;

        foreach (var worker in villageScreen.Workers)
        {
            RecordWorker(worker);
        }

        if (GameController.IngameState.IngameUi.VillageRecruitmentPanel is { IsVisible: true })
        {
            foreach (var worker in villageScreen.WorkersForSale)
            {
                RecordWorker(worker);
            }
        }

        var zoneLoadResources = villageScreen.ZoneLoadResources;
        if (Settings.WindowShown &&
            zoneLoadResources.Resources is { Count: > 0 } resources &&
            ImGui.Begin("Village stats"))
        {
            var villageGold = Settings.ShowProjectedCurrentGold ? villageScreen.CurrentGold : zoneLoadResources.Gold;
            if (Settings.ShowUpgrades &&
                ImGui.TreeNodeEx("Upgrades", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (GameController.Player?.GetComponent<Player>() is { } player)
                {
                    var nextUpgradeTiers = player.VillageUpgrades.Where(x => !x.Value).GroupBy(x => x.Key.Category.Id).Select(x => x.MinBy(y => y.Key.Tier)).ToList();
                    foreach (var nextUpgradeTier in nextUpgradeTiers)
                    {
                        ImGui.Text($"{nextUpgradeTier.Key}");
                        ImGui.SameLine();
                        foreach (var requiredResource in nextUpgradeTier.Key.RequiredResources)
                        {
                            var enoughResource = requiredResource.Item2 <= resources.GetValueOrDefault(requiredResource.Item1);
                            ImGui.TextColored(enoughResource ? Settings.GoodColor.Value.ToImguiVec4() : Settings.BadColor.Value.ToImguiVec4(),
                                $"{requiredResource.Item2}x {requiredResource.Item1.Export?.Name ?? requiredResource.Item1.Id},");
                            ImGui.SameLine();
                        }

                        var enoughGold = nextUpgradeTier.Key.RequiredGold <= villageGold;
                        ImGui.TextColored(enoughGold ? Settings.GoodColor.Value.ToImguiVec4() : Settings.BadColor.Value.ToImguiVec4(),
                            $"{nextUpgradeTier.Key.RequiredGold} gold");
                    }
                }

                ImGui.TreePop();
            }

            if (Settings.ShowResources &&
                ImGui.TreeNodeEx("Resources", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var resource in resources)
                {
                    if (resource.Value == 0 && !Settings.ShowEmptyResources)
                    {
                        continue;
                    }

                    var colorSet = resource.Value == 0 && (Settings.ShowEmptyResourcesInColor);
                    if (colorSet)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Settings.BadColor.Value.ToImguiVec4());
                    }

                    ImGui.Text($"{resource.Value}x {resource.Key.Export?.Name ?? resource.Key.Id}");
                    if (colorSet)
                    {
                        ImGui.PopStyleColor();
                    }
                }

                {
                    var colorSet = villageGold <= 1 && (Settings.ShowEmptyResourcesInColor);
                    if (colorSet)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Settings.BadColor.Value.ToImguiVec4());
                    }

                    ImGui.Text($"{villageGold} gold (-{villageScreen.TotalWagePerHour}/hr)");
                    if (colorSet)
                    {
                        ImGui.PopStyleColor();
                    }
                }
                ImGui.TreePop();
            }

            if (Settings.ShowActions &&
                ImGui.TreeNodeEx("Actions", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (villageScreen.RemainingShipmentTimes is { Count: > 0 })
                {
                    foreach (var remainingShipmentTime in villageScreen.RemainingShipmentTimes)
                    {
                        if (remainingShipmentTime <= TimeSpan.Zero)
                        {
                            ImGui.TextColored(Settings.GoodColor.Value.ToImguiVec4(), "Shipment arrived!");
                        }
                        else
                        {
                            ImGui.Text($"Shipment arrives in {remainingShipmentTime:hh\\:mm\\:ss}");
                        }
                    }
                }

                if (villageScreen.RemainingDisenchantmentTime <= TimeSpan.Zero)
                {
                    ImGui.TextColored(Settings.BadColor.Value.ToImguiVec4(), "Nothing to disenchant");
                }
                else
                {
                    ImGui.Text($"Disenchantment completes in {villageScreen.RemainingDisenchantmentTime:hh\\:mm\\:ss}");
                }

                ImGui.TreePop();
            }
        }

        if (Settings.ShowStatusOverlay)
        {
            ShowStatusOverlay();
        }

        if (Settings.ShowWorkerUpgradeTips)
        {
            HashSet<string> workersToReplace = [];
            if (GameController.IngameState.IngameUi.VillageRecruitmentPanel is
                {
                    IsVisible: true,
                    OfferedWorkers: { } offeredWorkers,
                    CurrentWorkers: { } currentWorkers,
                })
            {
                var existingWorkersByAssignedSkill = villageScreen.Workers.GroupBy(x => x.Job.Type).ToDictionary(x => x.Key, x => x.ToList());
                foreach (var (workerElement, worker) in SafeJoinWorkersByName(offeredWorkers, villageScreen.WorkersForSale, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName))
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, null, workersToReplace);
                }

                foreach (var (workerElement, worker) in SafeJoinWorkersByName(currentWorkers, villageScreen.Workers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName))
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, workersToReplace);
                }
            }

            if (GameController.IngameState.IngameUi.VillageWorkerManagementPanel is
                {
                    IsVisible: true,
                    AvailableWorkers: { } availableWorkers,
                    AssignedWorkers: { } assignedWorkers,
                })
            {
                var existingWorkersByAssignedSkill = villageScreen.Workers.GroupBy(x => x.Job.Type).ToDictionary(x => x.Key, x => x.ToList());
                foreach (var (workerElement, worker) in SafeJoinWorkersByName(availableWorkers, villageScreen.Workers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName))
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, workersToReplace);
                }

                var visibleAssignedWorkers = SafeJoinWorkersByName(assignedWorkers, villageScreen.Workers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName);
                foreach (var (workerElement, worker) in visibleAssignedWorkers)
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, []);
                }

                if (workersToReplace.Any())
                {
                    foreach (var (workerElement, worker) in visibleAssignedWorkers.Where(x => workersToReplace.Contains(x.worker.WorkerName)))
                    {
                        Graphics.DrawFrame(workerElement.GetClientRectCache, Settings.BadColor, 3);
                    }
                }
            }
        }

        if (GameController.IngameState.IngameUi.VillageShipmentScreen is { IsVisible: true } villageShipmentScreen)
        {
            var portRequestList = villageScreen.PortRequests;
            if (villageShipmentScreen.ShipIsOpened)
            {
                var shipIndex = villageShipmentScreen.OpenedShipIndex;
                if (shipIndex < 0 || shipIndex >= villageScreen.ShipInfo.Count) {
                    DebugWindow.LogMsg("Invalid opened ship index.");
                    return;
                }
                var shipInfo = villageScreen.ShipInfo[shipIndex];
                var targetPort = shipInfo.TargetPort;
                var targetPortIndex = GameController.Files.VillageShippingPorts.EntriesList.IndexOf(targetPort);
                var configurationTextPlacement = villageShipmentScreen.ShipmentConfigurationElement.GetClientRectCache.TopRight.ToVector2Num();
                if (targetPortIndex > 0 && targetPortIndex <= portRequestList.Count)
                {
                    var portRequests = portRequestList[targetPortIndex - 1].Requests;
                    var offset = new Vector2(0, 0);
                    var ports = portRequests.Where(x => x.DeliveredAmount < x.RequestedAmount).ToList();
                    var maxNameLength = ports.Max(x => x.ResourceType.Name.Length);
                    foreach (var portRequest in ports)
                    {
                        var effectiveness = 100 + portRequest.RequestMarkup * (100 + villageScreen.VillageStats.GetValueOrDefault(GameStat.VillagePortQuotaEffectivenessPct)) / 10;
                        var remainingResources = portRequest.ResourceType.UpgradedExport != null
                            ? Math.Ceiling((portRequest.RequestedAmount - portRequest.DeliveredAmount) / 5.0)
                            : portRequest.RequestedAmount - portRequest.DeliveredAmount;
                        var exportText = $"{portRequest.ResourceType.Name.PadRight(maxNameLength)}: {remainingResources,6} at {effectiveness,3:F0}%";
                        var textSize = Graphics.DrawTextWithBackground(
                            exportText,
                            configurationTextPlacement + offset,
                            Color.Black);
                        offset.Y += textSize.Y;
                    }
                }
                else if (targetPortIndex == 0)
                {
                    Graphics.DrawTextWithBackground(
                        "No port selected...",
                        configurationTextPlacement,
                        Color.Black);
                }
            }

            foreach (var (portIndex, portElement) in villageShipmentScreen.PortElementsByIndex)
            {
                var portRequests = portRequestList[portIndex - 1].Requests;
                var offset = new Vector2(0, 0);
                var portTextPlacement = portElement.GetClientRectCache.TopRight.ToVector2Num();
                var ports = portRequests.Where(x => x.DeliveredAmount < x.RequestedAmount).ToList();
                var maxNameLength = ports.Max(x => x.ResourceType.Name.Length);
                foreach (var portRequest in ports)
                {
                    var effectiveness = 100 + portRequest.RequestMarkup * (100 + villageScreen.VillageStats.GetValueOrDefault(GameStat.VillagePortQuotaEffectivenessPct)) / 10;
                    var remainingResources = portRequest.ResourceType.UpgradedExport != null
                        ? Math.Ceiling((portRequest.RequestedAmount - portRequest.DeliveredAmount) / 5.0)
                        : portRequest.RequestedAmount - portRequest.DeliveredAmount;
                    var exportText = $"{portRequest.ResourceType.Name.PadRight(maxNameLength)}: {remainingResources,6} at {effectiveness,3:F0}%";
                    var textSize = Graphics.DrawTextWithBackground(
                        exportText,
                        portTextPlacement + offset,
                        Color.Black);
                    offset.Y += textSize.Y;
                }
            }
        }
    }

    private record DrawItem(string Text, Color Color, Color BackgroundColor);

    private List<DrawItem> ComputeStatusOverlayDrawItems(VillageScreen villageScreen, int villageGold)
    {
        var result = new List<DrawItem>();

        if (_previousShipmentTimes == null) {
            _previousShipmentTimes = new Dictionary<int, TimeSpan>();
        }

        if (Settings.ShowActions) {
            if (villageScreen.RemainingShipmentTimes is { Count: > 0 }) {
                foreach (var (remainingShipmentTime, timer) in villageScreen.RemainingShipmentTimes.Select((x, i) => (x, i))) {
                    bool hasReset = false;
                    TimeSpan resetValue = TimeSpan.Zero;
                    DateTime resetTime = DateTime.UtcNow;

                    if (_previousShipmentTimes.TryGetValue(timer, out var previousTime)) {
                        if (remainingShipmentTime > previousTime && previousTime > TimeSpan.Zero) {
                            hasReset = true;
                            resetValue = remainingShipmentTime;
                            resetTime = DateTime.UtcNow;

                            _shipmentResetTimes[timer] = resetTime;
                            _shipmentResetValues[timer] = resetValue;
                        }
                    }

                    _previousShipmentTimes[timer] = remainingShipmentTime;

                    string displayText;
                    Color textColor;

                    if (remainingShipmentTime <= TimeSpan.Zero) {
                        displayText = "SHIPMENT HERE!";
                        textColor = Settings.GoodColor;

                        _shipmentResetTimes.Remove(timer);
                        _shipmentResetValues.Remove(timer);
                    } else {
                        bool showResetValue = hasReset;

                        if (!showResetValue && _shipmentResetTimes.TryGetValue(timer, out var storedResetTime)) {
                            if (DateTime.UtcNow - storedResetTime < TimeSpan.FromSeconds(60) &&
                                _shipmentResetValues.TryGetValue(timer, out var storedResetValue)) {
                                showResetValue = true;
                                resetValue = storedResetValue;
                            }
                        }

                        if (showResetValue) {
                            displayText = $"Shipment {resetValue:hh\\:mm} - Not moving?";
                            textColor = Settings.BadColor;
                        } else {
                            displayText = $"Shipment {remainingShipmentTime:hh\\:mm}";
                            textColor = Settings.NeutralColor;
                        }
                    }

                    result.Add(new DrawItem(displayText, textColor, Color.Black));
                }
            }

            result.Add(
                villageScreen.RemainingDisenchantmentTime <= TimeSpan.Zero
                    ? new DrawItem("DISENCHANTMENT EMPTY!", Settings.BadColor, Color.Black)
                    : new DrawItem($"Disenchantment {villageScreen.RemainingDisenchantmentTime:hh\\:mm}", Settings.NeutralColor, Color.Black)
            );
        }

        if (Settings.ShowResources)
        {
            var colorSet = villageGold <= 1 && Settings.ShowEmptyResourcesInColor;
            result.Add(
                new DrawItem(
                    $"{villageGold} gold (-{villageScreen.TotalWagePerHour}/hr) {TimeSpan.FromHours(villageGold / (float)villageScreen.TotalWagePerHour):hh\\:mm}",
                    colorSet ? Settings.BadColor : Settings.NeutralColor, Color.Black)
            );
        }

        return result;
    }

    private void ShowStatusOverlay()
    {
        var position = GameController.LeftPanel.StartDrawPointNum;
        var shipmentTextSize = new[]
        {
            Graphics.MeasureText("SHIPMENT 00:00!"),
            Graphics.MeasureText("DISENCHANTMENT EMPTY!"),
            Graphics.MeasureText("99999 gold (-99999/hr) 00:00"),
        }.MaxBy(x => x.X);
        var positionLeft = position - shipmentTextSize with { Y = 0 };
        positionLeft.X += Settings.StatusOverlayXOffset;
        positionLeft.Y += Settings.StatusOverlayYOffset;
        foreach (var drawItem in _overlayCache.Value)
        {
            var drawText = Graphics.DrawTextWithBackground(drawItem.Text, positionLeft, drawItem.Color, drawItem.BackgroundColor);
            position.Y += drawText.Y;
            positionLeft.Y += drawText.Y;
        }

        GameController.LeftPanel.StartDrawPointNum = position;
    }

    private void ShowUpgradeTooltips(Dictionary<VillageJobType, List<VillageWorker>> existingWorkersByAssignedSkill, BaseVillageWorker worker, Element workerElement,
        VillageJobType assignedJob, HashSet<string> workersToReplace)
    {
        List<Action> tooltipActions = [];
        var indicatorType = 0;
        var moreLeftText = string.Empty;
        var rankedRanks = worker.JobRanks.ToList();
        var expectedWage = rankedRanks.Where(x => AverageWorkerWages.ContainsKey(x.Key.Id))
            .ToDictionary(r => r.Key, r => AverageWorkerWages[r.Key.Id] * GameController.Files.VillageJobSkillLevels.GetByLevel(r.Value).Speed);
        if (expectedWage.Any())
        {
            tooltipActions.Add(() =>
            {
                ImGui.Text("Expected wages:");
                if (ImGui.BeginTable("expected", 2, ImGuiTableFlags.Borders))
                {
                    var longestSkill = expectedWage.Max(x => x.Key.Id?.Length ?? 0);
                    ImGui.TableSetupColumn("Skill");
                    ImGui.TableSetupColumn("GPH");
                    ImGui.TableHeadersRow();
                    foreach (var skill in expectedWage.OrderByDescending(x => x.Value))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(skill.Key.Id?.PadLeft(longestSkill) ?? "");
                        ImGui.TableNextColumn();
                        ImGui.Text(skill.Value.ToString("F0").PadLeft(4));
                    }

                    ImGui.EndTable();
                }
            });
        }

        if (rankedRanks.All(x => AverageWorkerWages.ContainsKey(x.Key.Id)))
        {
            var maxWageSkill = expectedWage.MaxBy(x => x.Value);
            if (!Equals(assignedJob, null) && !Equals(assignedJob.Id, "Idling") && !Equals(maxWageSkill.Key, assignedJob) &&
                worker.WagePerHour > expectedWage.GetValueOrDefault(assignedJob, 0))
            {
                tooltipActions.Add(() =>
                {
                    var overpayFactor = expectedWage[maxWageSkill.Key] / expectedWage.GetValueOrDefault(assignedJob, AverageWorkerWages[assignedJob.Id]);
                    ImGui.TextColored(Settings.BadColor.Value.ToImguiVec4(),
                        $"Bad assignment: assigned to {assignedJob.Id}, but paid ~{(overpayFactor - 1) * 100:F0}%% more due to their {maxWageSkill.Key.Id} level");
                });
                moreLeftText += "\nBAD ASSIGN";
            }


            moreLeftText += $"\nWage: {(worker.WagePerHour / maxWageSkill.Value - 1) * 100:+#;-#;0}%";
        }

        foreach (var (candidateRankType, candidateRank) in worker.JobRanks.Where(x => !Equals(x.Key, assignedJob)))
        {
            var costPerPoint = worker.WagePerHour / (float)GameController.Files.VillageJobSkillLevels.GetByLevel(candidateRank).Speed;
            if (existingWorkersByAssignedSkill.GetValueOrDefault(candidateRankType, [])
                    .Where(x => x.JobRanks.GetValueOrDefault(candidateRankType, 0) <= candidateRank)
                    .Select(w => (worker: w,
                        costPerPoint: w.WagePerHour / (float)GameController.Files.VillageJobSkillLevels.GetByLevel(w.JobRanks.GetValueOrDefault(candidateRankType, 0)).Speed))
                    .OrderByDescending(x => x.costPerPoint)
                    .ToList() is { Count: > 0 } upgradeList)
            {
                var gphPerWorkUnitString = "GPH/work unit";
                if (upgradeList.Where(x => x.costPerPoint > costPerPoint).ToList() is { Count: > 0 } uList2)
                {
                    workersToReplace.Add(uList2[0].worker.WorkerName);
                    // recruitment panel only:
                    // show highest recommendation only on those worker offers whose highset paid job type is an upgrade to an existing worker of that job type
                    if (candidateRankType == expectedWage.MaxBy(x => x.Value).Key || assignedJob != null || !Settings.RecommendOnlyCostEfficientRecruits) {
                        indicatorType = Math.Max(indicatorType, 2);
                    }
                    var longestWorkerName = uList2.Max(x => x.worker.WorkerName.Length);
                    tooltipActions.Add(() =>
                    {
                        ImGui.Text($"{worker.WorkerName} better at {candidateRankType.Id} ({costPerPoint:F2} gph/work unit, rank {candidateRank}, {worker.WagePerHour} gph) than:");
                        if (ImGui.BeginTable($"better##{candidateRankType.Id}", 5, ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("Name");
                            ImGui.TableSetupColumn(gphPerWorkUnitString);
                            ImGui.TableSetupColumn("Rank");
                            ImGui.TableSetupColumn("GPH");
                            ImGui.TableSetupColumn("Current job");
                            ImGui.TableHeadersRow();
                            foreach (var (villageWorker, costPerSpeedI) in uList2)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.WorkerName.PadLeft(longestWorkerName));
                                ImGui.TableNextColumn();
                                ImGui.Text(costPerSpeedI.ToString("F2").PadLeft(gphPerWorkUnitString.Length));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.JobRanks.GetValueOrDefault(candidateRankType, 0).ToString().PadLeft(4));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.WagePerHour.ToString().PadLeft(4));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.Job.Name switch { "" => $"Index {villageWorker.Job.Discriminator}", var s => s });
                            }

                            ImGui.EndTable();
                        }
                    });
                }
                else if (upgradeList.Where(x =>
                         {
                             var oldRank = x.worker.JobRanks.GetValueOrDefault(candidateRankType, 0);
                             return oldRank < candidateRank;
                         }).ToList() is { Count: > 0 } uList3)
                {
                    workersToReplace.Add(uList3[0].worker.WorkerName);
                    if (candidateRankType == expectedWage.MaxBy(x => x.Value).Key || assignedJob != null || !Settings.RecommendOnlyCostEfficientRecruits) {
                        indicatorType = Math.Max(indicatorType, 1);
                    }
                    var longestWorkerName = uList3.Max(x => x.worker.WorkerName.Length);
                    tooltipActions.Add(() =>
                    {
                        ImGui.Text(
                            $"{worker.WorkerName} has higher {candidateRankType.Id} rank ({costPerPoint:F2} gph/work unit, rank {candidateRank}, {worker.WagePerHour} gph) than:");
                        if (ImGui.BeginTable($"higher##{candidateRankType.Id}", 5, ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("Name");
                            ImGui.TableSetupColumn(gphPerWorkUnitString);
                            ImGui.TableSetupColumn("Rank");
                            ImGui.TableSetupColumn("GPH");
                            ImGui.TableSetupColumn("Current job");
                            ImGui.TableHeadersRow();
                            foreach (var (villageWorker, costPerSpeedI) in uList3)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.WorkerName.PadLeft(longestWorkerName));
                                ImGui.TableNextColumn();
                                ImGui.Text(costPerSpeedI.ToString("F2").PadLeft(gphPerWorkUnitString.Length));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.JobRanks.GetValueOrDefault(candidateRankType, 0).ToString().PadLeft(4));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.WagePerHour.ToString().PadLeft(4));
                                ImGui.TableNextColumn();
                                ImGui.Text(villageWorker.Job.Name switch { "" => $"Index {villageWorker.Job.Discriminator}", var s => s });
                            }

                            ImGui.EndTable();
                        }
                    });
                }
            }
        }

        switch (indicatorType)
        {
            case 2:
                Graphics.DrawTextWithBackground($"++{moreLeftText}", workerElement.GetClientRectCache.TopLeft.ToVector2Num(), Settings.GoodColor, FontAlign.Right, Color.Black);
                break;
            case 1:
                Graphics.DrawTextWithBackground($"+{moreLeftText}", workerElement.GetClientRectCache.TopLeft.ToVector2Num(), Settings.GoodColor, FontAlign.Right, Color.Black);
                break;
            case 0:
                Graphics.DrawTextWithBackground($"-{moreLeftText}", workerElement.GetClientRectCache.TopLeft.ToVector2Num(), FontAlign.Right, Color.Black);
                break;
        }

        if (tooltipActions.Any() && workerElement.GetClientRect().Contains(Input.MousePositionNum))
        {
            if (ImGui.BeginTooltip())
            {
                foreach (var tooltipAction in tooltipActions)
                {
                    tooltipAction();
                }

                ImGui.EndTooltip();
            }
        }
    }
}