using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.FilesInMemory.Village;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace VillageHelper;

public class VillageHelper : BaseSettingsPlugin<VillageHelperSettings>
{
    private TimeCache<List<DrawItem>> _overlayCache;
    private Dictionary<int, TimeSpan> _previousShipmentTimes;
    private readonly Dictionary<int, DateTime> _shipmentResetTimes = new ();
    private readonly Dictionary<int, TimeSpan> _shipmentResetValues = new ();

    private static readonly HashSet<string> NonSkillJobTypes = ["Idling", "LostAtSea", "LostMapping"];

    private const string IdlingJobId = "Idling";

    // Matched on Id rather than by key
    private static int GetWorkerJobRank(BaseVillageWorker worker, VillageJobType job) =>
        worker.JobRanks.FirstOrDefault(r => r.Key?.Id == job.Id).Value;

    private static int? TryParseWage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return int.TryParse(text.Replace(",", "").Replace("/h", ""), out var wage) ? wage : null;
    }
    
    // split worker names from ui elements into forename + surname, keeping in mind multi token surnames
    private static (string forename, string surname) SplitName(string name)
    {
        var halves = (name ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return (halves.ElementAtOrDefault(0), halves.ElementAtOrDefault(1));
    }

    private static string NameHalfWageKey(string nameHalf, int wage) => nameHalf != null ? $"{nameHalf}|{wage}" : null;

    private static string JobRanksKey(IEnumerable<KeyValuePair<VillageJobType, int>> jobRanks) =>
        string.Join(",", jobRanks.Where(r => r.Key?.Id != null).OrderBy(r => r.Key.Id).Select(r => $"{r.Key.Id}:{r.Value}"));
    
    private static string WorkerDisplayIdentity(VillageWorker worker) =>
        $"{worker.WorkerName}|{worker.WagePerHour}|{JobRanksKey(worker.JobRanks)}|{worker.Job.Type?.Id}";

    private static string WorkerDisplayIdentity(VillageWorkerForSale worker) =>
        $"{worker.WorkerName}|{worker.WagePerHour}|{JobRanksKey(worker.JobRanks)}";
    
    private static T TryReadElement<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default;
        }
    }
    

    // Some worker ui elements display a name that doesn't match their worker record (element says "Esile
    // Waldegrave" but the backing worker is "Esile Norene")
    // So we match on name and wage together first, then on wage plus either half of the name
    // If we can't get an exact match, we skip
    private List<(TElement element, TWorker worker)> JoinWorkerElements<TElement, TWorker>(
        IEnumerable<TElement> elements, IEnumerable<TWorker> workers,
        Func<TElement, bool> isVisible, Func<TElement, string> elementWorkerName, Func<TWorker, string> workerWorkerName,
        Func<TElement, string> elementWageText, Func<TWorker, int> workerWage, Func<TWorker, string> workerIdentity,
        Func<TElement, string> elementJobText = null, Func<TWorker, string> workerJobName = null)
    {
        var matches = new List<(TElement element, TWorker worker)>();
        try
        {
            var pending = new List<(TElement Element, string Name, int Wage, string JobText)>();
            foreach (var element in elements)
            {
                if (!TryReadElement(() => isVisible(element)))
                {
                    continue;
                }

                // every pass keys on the wage, so an element missing name or wage is no use to us
                if (TryReadElement(() => elementWorkerName(element)) is { } name &&
                    TryReadElement(() => TryParseWage(elementWageText(element))) is { } wage)
                {
                    pending.Add((element, name, wage, elementJobText == null ? null : TryReadElement(() => elementJobText(element))));
                }
            }

            var unclaimed = workers.ToList();

            void MatchUniqueOn(Func<(TElement Element, string Name, int Wage, string JobText), string> elementKey, Func<TWorker, string> workerKey)
            {
                var workersByKey = unclaimed.GroupBy(workerKey).Where(g => g.Key != null && g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
                var unambiguous = pending.GroupBy(elementKey).Where(g => g.Key != null && g.Count() == 1).Select(g => g.Single()).ToList();
                foreach (var entry in unambiguous)
                {
                    if (workersByKey.TryGetValue(elementKey(entry), out var worker))
                    {
                        matches.Add((entry.Element, worker));
                        pending.Remove(entry);
                        unclaimed.Remove(worker);
                    }
                }
            }
            
            if (workerJobName != null)
            {
                MatchUniqueOn(p => p.JobText == null ? null : $"{p.Name}|{p.Wage}|{p.JobText}",
                    w => $"{workerWorkerName(w)}|{workerWage(w)}|{workerJobName(w)}");
            }

            MatchUniqueOn(p => $"{p.Name}|{p.Wage}", w => $"{workerWorkerName(w)}|{workerWage(w)}");

            // on name mismatch only one of the two names seems to be wrong, so wage plus a single half should still get us the correct worker.
            // Some name tokens ("Aveline", "Norene", "Vonette", ..) can be either forename or surname).
            MatchUniqueOn(p => NameHalfWageKey(SplitName(p.Name).forename, p.Wage),
                w => NameHalfWageKey(SplitName(workerWorkerName(w)).forename, workerWage(w)));
            MatchUniqueOn(p => NameHalfWageKey(SplitName(p.Name).surname, p.Wage),
                w => NameHalfWageKey(SplitName(workerWorkerName(w)).surname, workerWage(w)));

            // handle special case identical workers
            foreach (var group in pending.GroupBy(p => $"{p.Name}|{p.Wage}").ToList())
            {
                var candidates = unclaimed.Where(w => $"{workerWorkerName(w)}|{workerWage(w)}" == group.Key).ToList();
                if (candidates.Count == 0 || candidates.Select(workerIdentity).Distinct().Count() > 1)
                {
                    continue;
                }

                foreach (var (entry, worker) in group.Zip(candidates))
                {
                    matches.Add((entry.Element, worker));
                    pending.Remove(entry);
                    unclaimed.Remove(worker);
                }
            }

            return matches;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to join worker elements: {ex}");
            return matches;
        }
    }

    private WorkerWageStats _wageStats;
    
    private static string FormatTable(string[] headers, List<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        var rightAlign = Enumerable.Range(0, headers.Length)
            .Select(i => rows.Count > 0 && rows.All(r => double.TryParse(r[i], NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
            .ToArray();

        var table = new StringBuilder();
        foreach (var row in rows.Prepend(headers))
        {
            table.AppendLine(string.Join("  ", row.Select((cell, i) => rightAlign[i] ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]))).TrimEnd());
        }

        return table.ToString();
    }

    private static bool? HasFreeSlot(VillageWorkerManagementPanel panel)
    {
        var workerLimitLabel = TryReadElement(() => panel.Children[0].Children[0].Children[1].Text)?.Split('/');
        return workerLimitLabel is { Length: 2 } &&
               int.TryParse(workerLimitLabel[0].Trim(), out var used) &&
               int.TryParse(workerLimitLabel[1].Trim(), out var capacity) &&
               capacity > 0
            ? used < capacity
            : null;
    }

    // Check if worker is worth assigning for recommendation highlight
    private bool WorthAssigning(bool? hasFreeSlot, List<(VillageWorkerManagementPanelWorkerElement element, VillageWorker worker)> assignedWorkers,
        VillageWorker candidate, VillageJobType panelJob)
    {
        if (hasFreeSlot is null or true)
        {
            return true;
        }

        GameController.IngameState.IngameUi.VillageScreen.CanBeEmployed(candidate.Job);
        var candidateRank = GetWorkerJobRank(candidate, panelJob);
        var candidateCostPerPoint = candidate.WagePerHour / _wageStats.Speed(candidateRank);

        return assignedWorkers.Select(x => x.worker).Any(assigned =>
        {
            if (HighestPaidSkill(assigned)?.Id != panelJob.Id)
            {
                return true;
            }

            // Same as ShowUpgradeTooltips()
            var assignedRank = GetWorkerJobRank(assigned, panelJob);
            return assignedRank < candidateRank ||
                   (assignedRank <= candidateRank && assigned.WagePerHour / _wageStats.Speed(assignedRank) > candidateCostPerPoint);
        });
    }

    // With the setting on we only recommend a worker for the skill their wage is actually being paid for.
    private bool IsRecommendedFor(VillageJobType candidateJob, Dictionary<VillageJobType, double> expectedWage) =>
        !Settings.ShowOnlyCostEfficientUpgrades || candidateJob == expectedWage.MaxBy(x => x.Value).Key;
    
    private VillageJobType HighestPaidSkill(BaseVillageWorker worker)
    {
        var wages = _wageStats.AverageWorkerWages;
        return worker.JobRanks
            .Where(r => r.Key?.Id != null && wages.ContainsKey(r.Key.Id))
            .Select(r => (job: r.Key, predicted: wages[r.Key.Id].GoldPerWorkUnit * _wageStats.Speed(r.Value)))
            .MaxBy(x => x.predicted).job;
    }

    public override bool Initialise()
    {
        GameController.LeftPanel.WantUse(() => true);
        
        _wageStats = new WorkerWageStats(GameController, ConfigDirectory);

        Settings.ExportWorkerStats.OnPressed += () =>
        {
            var wageRows = _wageStats.AverageWorkerWages
                .OrderBy(x => x.Value.GoldPerWorkUnit)
                .Select(x => new[]
                {
                    x.Key,
                    x.Value.GoldPerWorkUnit.ToString("F3", CultureInfo.InvariantCulture),
                    x.Value.SampleCount.ToString(CultureInfo.InvariantCulture),
                    x.Value.IsDefault ? "default" : "measured",
                })
                .ToList();

            var bucketRows = _wageStats.WageBuckets
                .OrderBy(x => x.Key)
                .SelectMany(job => job.Value
                    .OrderBy(x => x.Key)
                    .Where(x => x.Value.Count > 0)
                    .Select(rank =>
                    {
                        var mean = rank.Value.SumWage / rank.Value.Count;
                        var sd = Math.Sqrt(Math.Max(0, rank.Value.SumWageSq / rank.Value.Count - mean * mean));
                        return new[]
                        {
                            job.Key,
                            rank.Key.ToString(CultureInfo.InvariantCulture),
                            _wageStats.Speed(rank.Key).ToString("F0", CultureInfo.InvariantCulture),
                            rank.Value.Count.ToString(CultureInfo.InvariantCulture),
                            mean.ToString("F1", CultureInfo.InvariantCulture),
                            sd.ToString("F1", CultureInfo.InvariantCulture),
                        };
                    }))
                .ToList();

            ImGui.SetClipboardText(
                "# gold per work unit\n" +
                FormatTable(["job", "gphPerWorkUnit", "samples", "source"], wageRows) +
                "\n# samples per job and rank\n" +
                FormatTable(["job", "rank", "speed", "samples", "meanWage", "sdWage"], bucketRows));
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
        _wageStats.SaveIfDirty();
        return null;
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        
        // Wage data table in settings
        if (ImGui.TreeNode("Worker Wage Data"))
        {
            var averages = _wageStats.AverageWorkerWages;
            ImGui.TextWrapped("A worker's wage is set by their highest paying skill. " +
                              $"Average wages for each job type use preset values until there are at least {WorkerWageStats.MinCalibrationSamples} samples recorded.");
            ImGui.Spacing();

            if (ImGui.BeginTable("AverageWorkerWagesTable", 4, ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Job");
                ImGui.TableSetupColumn("Avg GPH/work unit");
                ImGui.TableSetupColumn("Samples");
                ImGui.TableSetupColumn("Source");
                ImGui.TableHeadersRow();
                foreach (var jobType in GameController.Game.Files.VillageJobTypes.EntriesList.Where(x => !NonSkillJobTypes.Contains(x.Id)))
                {
                    var stats = jobType.Id != null ? averages.GetValueOrDefault(jobType.Id) : null;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(jobType.Id);
                    ImGui.TableNextColumn();
                    ImGui.Text(stats?.GoldPerWorkUnit.ToString("F2") ?? "-");
                    ImGui.TableNextColumn();
                    ImGui.Text(stats?.SampleCount.ToString() ?? "-");
                    ImGui.TableNextColumn();
                    if (stats is { IsDefault: true })
                    {
                        ImGui.TextColored(Settings.BadColor.Value.ToImguiVec4(), "preset");
                    }
                    else
                    {
                        ImGui.Text(stats == null ? "-" : "measured");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Text($"Wages recorded: {_wageStats.TotalWageSamples}");
            ImGui.Spacing();
            
            ImGui.TextWrapped($"You can reset data here to recalibrate if GGG should decide to shuffle wages again.");
            if (ImGui.Button("Reset calibration data"))
            {
                _wageStats.Reset();
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

        // capture every offered worker in the recruitment panel to build average wage data
        if (GameController.IngameState.IngameUi.VillageRecruitmentPanel is { IsVisible: true })
        {
            _wageStats.Record(villageScreen.WorkersForSale);
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
            // address instead of name, names are not unique 
            HashSet<long> workersToReplace = [];
            if (GameController.IngameState.IngameUi.VillageRecruitmentPanel is
                {
                    IsVisible: true,
                    OfferedWorkers: { } offeredWorkers,
                    CurrentWorkers: { } currentWorkers,
                })
            {
                var hiredWorkers = villageScreen.Workers;
                var existingWorkersByAssignedSkill = hiredWorkers
                    .GroupBy(x => x.Job.Type)
                    .ToDictionary(x => x.Key, x => x.ToList());
                foreach (var (workerElement, worker) in JoinWorkerElements(offeredWorkers, villageScreen.WorkersForSale, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName,
                             x => x.Children[3].Children[1].Children[0].Text, w => w.WagePerHour, WorkerDisplayIdentity))
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, null, workersToReplace);
                }

                foreach (var (workerElement, worker) in JoinWorkerElements(currentWorkers, hiredWorkers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName,
                             x => x.Children[3].Children[1].Children[0].Text, w => w.WagePerHour, WorkerDisplayIdentity,
                             x => x.Children[2].Children[1].Text, w => w.Job.Name))
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, workersToReplace);
                }
            }

            if (GameController.IngameState.IngameUi.VillageWorkerManagementPanel is
                {
                    IsVisible: true,
                    AvailableWorkers: { } availableWorkers,
                    AssignedWorkers: { } assignedWorkers,
                } managementPanel)
            {
                var hiredWorkers = villageScreen.Workers;
                var existingWorkersByAssignedSkill = hiredWorkers.GroupBy(x => x.Job.Type).ToDictionary(x => x.Key, x => x.ToList());
                var hasFreeSlot = HasFreeSlot(managementPanel);

                var visibleAssignedWorkers = JoinWorkerElements(assignedWorkers, hiredWorkers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName,
                    x => x.Children[3].Children[0].Children[1].Children[0].Text, w => w.WagePerHour, WorkerDisplayIdentity,
                    x => x.Children[2].Children[0].Text, w => w.Job.Name);

                // Get job type of current panel from assigned worker's job type
                var panelJob = visibleAssignedWorkers
                    .Select(x => x.worker.Job.Type)
                    .Where(t => t?.Id != null && !NonSkillJobTypes.Contains(t.Id))
                    .GroupBy(t => t.Id)
                    .MaxBy(g => g.Count())?.First();

                var visibleAvailableWorkers = JoinWorkerElements(availableWorkers, hiredWorkers, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName,
                    x => x.Children[3].Children[0].Children[1].Children[0].Text, w => w.WagePerHour, WorkerDisplayIdentity,
                    x => x.Children[2].Children[0].Text, w => w.Job.Name);
                foreach (var (workerElement, worker) in visibleAvailableWorkers)
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, workersToReplace);
                }

                foreach (var (workerElement, worker) in visibleAssignedWorkers)
                {
                    ShowUpgradeTooltips(existingWorkersByAssignedSkill, worker, workerElement, worker.Job.Type, []);
                }

                // highlight workers recommended to unassign for better alternatives
                if (hasFreeSlot != true && workersToReplace.Any())
                {
                    foreach (var (workerElement, worker) in visibleAssignedWorkers.Where(x => workersToReplace.Contains(x.worker.Address)))
                    {
                        Graphics.DrawFrame(workerElement.GetClientRectCache, Settings.BadColor, 3);
                    }
                }

                // highlight workers recommended to assign - idle only
                if (panelJob != null &&
                    hiredWorkers
                        .Where(w => w.Job.Type?.Id == IdlingJobId && GetWorkerJobRank(w, panelJob) > 0 &&
                                    (!Settings.ShowOnlyCostEfficientUpgrades || HighestPaidSkill(w)?.Id == panelJob.Id))
                        .OrderByDescending(w => GetWorkerJobRank(w, panelJob))
                        .ThenBy(w => w.WagePerHour / _wageStats.Speed(GetWorkerJobRank(w, panelJob)))
                        .FirstOrDefault() is { } bestIdle &&
                    WorthAssigning(hasFreeSlot, visibleAssignedWorkers, bestIdle, panelJob) &&
                    visibleAvailableWorkers.FirstOrDefault(x => x.worker.Address == bestIdle.Address) is { element: not null } bestRow)
                {
                    Graphics.DrawFrame(bestRow.element.GetClientRectCache, Settings.GoodColor, 3);
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

    private static int LostWorkerCount(VillageScreen villageScreen, string lostJobTypeId) =>
        villageScreen.WorkersByJob.Where(job => job.Key?.Type?.Id == lostJobTypeId).Sum(job => job.Value.Count);

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
                            displayText = $"Shipment {resetValue:hh\\:mm} - STUCK";
                            textColor = Settings.BadColor;
                        } else {
                            displayText = $"Shipment {remainingShipmentTime:hh\\:mm}";
                            textColor = Settings.NeutralColor;
                        }
                    }

                    result.Add(new DrawItem(displayText, textColor, Color.Black));
                }
            }

            // Summed, not Any() - a lost job type appears once per site (Discriminator), so several stranded
            // workers spread over more than one entry would otherwise read the same as a single one.
            if (LostWorkerCount(villageScreen, "LostMapping") is > 0 and var lostMappers) {
                result.Add(new DrawItem($"{lostMappers} MAPPER{(lostMappers > 1 ? "S" : "")} DEAD!", Settings.BadColor, Color.Black));
            }

            if (LostWorkerCount(villageScreen, "LostAtSea") is > 0 and var lostSailors) {
                result.Add(new DrawItem($"{lostSailors} SAILOR{(lostSailors > 1 ? "S" : "")} DEAD!", Settings.BadColor, Color.Black));
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
            Graphics.MeasureText("MAPPER DEAD!"),
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
        VillageJobType assignedJob, HashSet<long> workersToReplace)
    {
        List<Action> tooltipActions = [];
        var indicatorType = 0;
        var moreLeftText = string.Empty;
        var wages = _wageStats.AverageWorkerWages;
        var expectedWage = worker.JobRanks.Where(x => x.Key?.Id != null && wages.ContainsKey(x.Key.Id))
            .ToDictionary(r => r.Key, r => wages[r.Key.Id].GoldPerWorkUnit * _wageStats.Speed(r.Value));
        var anyDefaults = expectedWage.Keys.Any(x => wages[x.Id].IsDefault);
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
                        ImGui.Text((skill.Key.Id?.PadLeft(longestSkill) ?? "") + (wages[skill.Key.Id].IsDefault ? " *" : ""));
                        ImGui.TableNextColumn();
                        ImGui.Text(skill.Value.ToString("F0").PadLeft(4));
                    }

                    ImGui.EndTable();
                }

                if (anyDefaults)
                {
                    ImGui.TextDisabled("* uncalibrated - still using the built-in default for this job");
                }
            });
            
            // Only the best-paying skill matters here, so it's enough for that one job to have a wage estimate.
            var maxWageSkill = expectedWage.MaxBy(x => x.Value);
            var assignedJobWage = assignedJob?.Id != null && wages.TryGetValue(assignedJob.Id, out var assignedStats)
                ? expectedWage.GetValueOrDefault(assignedJob, assignedStats.GoldPerWorkUnit)
                : 0;
            if (assignedJob != null && !NonSkillJobTypes.Contains(assignedJob.Id) && !Equals(maxWageSkill.Key, assignedJob) &&
                worker.WagePerHour > expectedWage.GetValueOrDefault(assignedJob, 0) && assignedJobWage > 0)
            {
                tooltipActions.Add(() =>
                {
                    var overpayFactor = expectedWage[maxWageSkill.Key] / assignedJobWage;
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
                    if (IsRecommendedFor(candidateRankType, expectedWage))
                    {
                        workersToReplace.Add(uList2[0].worker.Address);
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
                    if (IsRecommendedFor(candidateRankType, expectedWage))
                    {
                        workersToReplace.Add(uList3[0].worker.Address);
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