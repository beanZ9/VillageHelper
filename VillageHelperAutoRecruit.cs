using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.FilesInMemory.Village;
using ExileCore.Shared;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace VillageHelper;

public partial class VillageHelper
{
    public class RecruiterJobTarget {
        public int MinRank { get; set; }
        public int TargetQuantity { get; set; }
        public bool AlwaysHireUpgrades { get; set; }
    }
    
    private VillageJobType GetDominantJob(IReadOnlyDictionary<VillageJobType, int> jobRanks)
    {
        var wages = _wageStats.AverageWorkerWages;
        return jobRanks
            .Where(r => r.Key?.Id != null)
            .Select(r => (job: r.Key, predicted: (wages.TryGetValue(r.Key.Id, out var stats) ? stats.GoldPerWorkUnit : 0) * _wageStats.Speed(r.Value)))
            .MaxBy(x => x.predicted).job;
    }

    private List<(VillageRecruitmentPanelWorkerElement element, VillageWorkerForSale worker)> GetVisibleOfferedWorkers(VillageScreen villageScreen)
    {
        return GameController.IngameState.IngameUi.VillageRecruitmentPanel is { IsVisible: true, OfferedWorkers: { } offeredWorkers }
            ? JoinWorkerElements(offeredWorkers, villageScreen.WorkersForSale, x => x.IsVisible, x => x.WorkerName, w => w.WorkerName,
                x => x.Children[3].Children[1].Children[0].Text, w => w.WagePerHour, WorkerDisplayIdentity)
            : [];
    }

    private const int RegularRerollCost = 2000;
    private const int HighQualityRerollCost = 16000;

    // Note: with OnlyCostEfficient disabled, one worker may count towards more than one job 
    private int GetCurrentWorkerCount(VillageScreen villageScreen, string jobId, int minRank)
    {
        var count = 0;
        foreach (var worker in villageScreen.Workers)
        {
            if (worker.JobRanks.Count == 0)
            {
                continue;
            }

            if (Settings.AutoRecruiter.OnlyCostEfficient)
            {
                if (GetDominantJob(worker.JobRanks) is { Id: { } dominantId } dominant &&
                    dominantId == jobId && worker.JobRanks[dominant] >= minRank)
                {
                    count++;
                }
            }
            else if (worker.JobRanks.Any(r => r.Key.Id == jobId && r.Value >= minRank))
            {
                count++;
            }
        }

        return count;
    }
    
    private Element RegularRerollButton => GameController.IngameState.IngameUi.VillageRecruitmentPanel.Children[0].Children[1].Children[0];
    private Element HighQualityRerollButton => GameController.IngameState.IngameUi.VillageRecruitmentPanel.Children[0].Children[1].Children[1];
    
    private int GetOfferedWorkerCost(Element offeredWorkerElement)
    {
        return int.Parse(offeredWorkerElement.Children[0].Children[1].Children[0].Text.Replace(",", ""));
    }

    private (Element element, string jobId, int cost)? FindHireCandidate(VillageScreen villageScreen, HashSet<string> neededJobIds)
    {
        foreach (var (element, worker) in GetVisibleOfferedWorkers(villageScreen))
        {
            if (worker.JobRanks.Count == 0)
            {
                continue;
            }

            if (Settings.AutoRecruiter.OnlyCostEfficient)
            {
                if (GetDominantJob(worker.JobRanks) is { Id: { } dominantId } dominant &&
                    neededJobIds.Contains(dominantId) &&
                    Settings.AutoRecruiter.Targets.TryGetValue(dominantId, out var target) &&
                    worker.JobRanks[dominant] >= target.MinRank)
                {
                    return (element, dominantId, GetOfferedWorkerCost(element));
                }
            }
            else
            {
                foreach (var (job, rank) in worker.JobRanks)
                {
                    if (neededJobIds.Contains(job.Id) &&
                        Settings.AutoRecruiter.Targets.TryGetValue(job.Id, out var target) &&
                        rank >= target.MinRank)
                    {
                        return (element, job.Id, GetOfferedWorkerCost(element));
                    }
                }
            }
        }

        return null;
    }

    // Find possible worker upgrades from offers
    private (Element element, string jobId, int cost)? FindUpgradeCandidate(VillageScreen villageScreen, HashSet<string> upgradeEnabledJobIds)
    {
        if (upgradeEnabledJobIds.Count == 0)
        {
            return null;
        }

        foreach (var (element, worker) in GetVisibleOfferedWorkers(villageScreen))
        {
            if (worker.JobRanks.Count == 0)
            {
                continue;
            }

            if (GetDominantJob(worker.JobRanks) is not { Id: { } dominantId } dominant ||
                !upgradeEnabledJobIds.Contains(dominantId) ||
                !Settings.AutoRecruiter.Targets.TryGetValue(dominantId, out var target))
            {
                continue;
            }

            var candidateRank = worker.JobRanks[dominant];
            if (candidateRank < target.MinRank)
            {
                continue;
            }

            var candidateCostPerPoint = worker.WagePerHour / (float)GameController.Game.Files.VillageJobSkillLevels.GetByLevel(candidateRank).Speed;
            var isUpgrade = villageScreen.Workers.Any(existing =>
            {
                if (existing.Job.Type?.Id != dominantId)
                {
                    return false;
                }

                // matched on Id: looking up by the Job.Type instance can miss and read as rank 0
                var existingRank = GetWorkerJobRank(existing, existing.Job.Type);
                if (existingRank > candidateRank)
                {
                    return false;
                }

                var existingSpeed = GameController.Game.Files.VillageJobSkillLevels.GetByLevel(existingRank)?.Speed ?? 0;
                if (existingSpeed <= 0)
                {
                    return true;
                }

                var existingCostPerPoint = existing.WagePerHour / (float)existingSpeed;
                return existingCostPerPoint > candidateCostPerPoint;
            });

            if (isUpgrade)
            {
                return (element, dominantId, GetOfferedWorkerCost(element));
            }
        }

        return null;
    }

    // Input util
    private async SyncTask<bool> WaitFor(TimeSpan period)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < period)
        {
            await TaskUtils.NextFrame();
        }

        return true;
    }

    private bool AutoRecruiterCancelRequested => Settings.AutoRecruiter.CancelWithRightMouseButton && (Control.MouseButtons & MouseButtons.Right) != 0;

    private async SyncTask<bool> ClickElement(Element element, bool holdCtrl)
    {
        if (holdCtrl)
        {
            Input.KeyDown(Keys.LControlKey);
            await WaitFor(TimeSpan.FromMilliseconds(10));
        }

        Input.SetCursorPos(element.GetClientRectCache.ClickRandomNum(x: 5, y: 5));
        await WaitFor(TimeSpan.FromMilliseconds(Settings.AutoRecruiter.ClickDelayMs));

        if (AutoRecruiterCancelRequested)
        {
            if (holdCtrl)
            {
                Input.KeyUp(Keys.LControlKey);
            }

            return false;
        }

        Input.Click(MouseButtons.Left);
        await WaitFor(TimeSpan.FromMilliseconds(25));

        if (holdCtrl)
        {
            Input.KeyUp(Keys.LControlKey);
        }

        return true;
    }
    
    private const int AutoRecruiterIdleMouseDelayMs = 500;

    private async SyncTask<bool> WaitForMouseIdle()
    {
        while (Control.MouseButtons == MouseButtons.Left)
        {
            if (AutoRecruiterCancelRequested)
            {
                return false;
            }

            await TaskUtils.NextFrame();
        }

        var mousePos = Input.MousePositionNum;
        var sw = Stopwatch.StartNew();
        await TaskUtils.NextFrame();
        while (true)
        {
            if (AutoRecruiterCancelRequested)
            {
                return false;
            }

            var newPos = Input.MousePositionNum;
            if (newPos != mousePos)
            {
                mousePos = newPos;
                sw.Restart();
            }
            else if (sw.ElapsedMilliseconds >= AutoRecruiterIdleMouseDelayMs)
            {
                return true;
            }
            else
            {
                await TaskUtils.NextFrame();
            }
        }
    }
    
    // Auto recruiter logic
    private const int MaxVillageWorkers = 90;

    private int _autoRecruiterGoldSpent;
    private bool _autoRecruiterRunning;
    private string _autoRecruiterStopReason;

    private async SyncTask<bool> RunAutoRecruiter()
    {
        if (!await WaitForMouseIdle())
        {
            _autoRecruiterRunning = false;
            _autoRecruiterStopReason = "Cancelled by user";
            return false;
        }

        while (true)
        {
            if (!_autoRecruiterRunning || GameController.IngameState.IngameUi.VillageRecruitmentPanel is not { IsVisible: true })
            {
                return false;
            }
            
            if (AutoRecruiterCancelRequested)
            {
                _autoRecruiterRunning = false;
                _autoRecruiterStopReason = "Cancelled by user";
                return false;
            }

            var villageScreen = GameController.IngameState.IngameUi.VillageScreen;
            if (villageScreen.Workers.Count >= MaxVillageWorkers)
            {
                _autoRecruiterRunning = false;
                _autoRecruiterStopReason = "Worker limit reached";
                return false;
            }

            var neededJobIds = GameController.Game.Files.VillageJobTypes.EntriesList
                .Select(j => j.Id)
                .Where(id => !NonSkillJobTypes.Contains(id) &&
                             Settings.AutoRecruiter.Targets.TryGetValue(id, out var target) &&
                             target.TargetQuantity > GetCurrentWorkerCount(villageScreen, id, target.MinRank))
                .ToHashSet();

            var upgradeEnabledJobIds = Settings.AutoRecruiter.Targets
                .Where(kv => kv.Value.AlwaysHireUpgrades && !NonSkillJobTypes.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToHashSet();

            // Targets met and no job has always upgrade ticked -> done
            if (neededJobIds.Count == 0 && upgradeEnabledJobIds.Count == 0)
            {
                _autoRecruiterRunning = false;
                _autoRecruiterStopReason = "Done - All targets met";
                return false;
            }

            var candidate = FindHireCandidate(villageScreen, neededJobIds) ?? FindUpgradeCandidate(villageScreen, upgradeEnabledJobIds);

            bool clicked;
            if (candidate is { } found)
            {
                if (_autoRecruiterGoldSpent + found.cost > Settings.AutoRecruiter.MaxGoldToSpend)
                {
                    _autoRecruiterRunning = false;
                    _autoRecruiterStopReason = "Gold limit reached";
                    return false;
                }

                clicked = await ClickElement(found.element, true);
                if (clicked)
                {
                    _autoRecruiterGoldSpent += found.cost;
                }
            }
            else
            {
                var rerollCost = Settings.AutoRecruiter.UseHighQualityReroll ? HighQualityRerollCost : RegularRerollCost;
                if (_autoRecruiterGoldSpent + rerollCost > Settings.AutoRecruiter.MaxGoldToSpend)
                {
                    _autoRecruiterRunning = false;
                    _autoRecruiterStopReason = "Gold limit reached";
                    return false;
                }

                var rerollButton = Settings.AutoRecruiter.UseHighQualityReroll ? HighQualityRerollButton : RegularRerollButton;
                clicked = await ClickElement(rerollButton, false);
                if (clicked)
                {
                    _autoRecruiterGoldSpent += rerollCost;
                }
            }

            if (!clicked)
            {
                _autoRecruiterRunning = false;
                _autoRecruiterStopReason = "Cancelled by user";
                return false;
            }

            await WaitFor(TimeSpan.FromMilliseconds(Settings.AutoRecruiter.ClickDelayMs.Value));
        }
    }

    private SyncTask<bool> _autoRecruiterTask;

    private void UpdateAutoRecruiter(VillageScreen villageScreen)
    {
        if (_autoRecruiterRunning && GameController.IngameState.IngameUi.VillageRecruitmentPanel is { IsVisible: true })
        {
            _autoRecruiterTask ??= RunAutoRecruiter();
        }
        else if (_autoRecruiterTask != null)
        {
            Input.KeyUp(Keys.LControlKey);
            _autoRecruiterTask = null;
            _autoRecruiterRunning = false;
            _autoRecruiterGoldSpent = 0;
        }

        if (_autoRecruiterTask != null)
        {
            TaskUtils.RunOrRestart(ref _autoRecruiterTask, () => null);
        }

        DrawAutoRecruiterPanel();
    }

    private void DrawAutoRecruiterPanel()
    {
        if (GameController.IngameState.IngameUi.VillageRecruitmentPanel is not { IsVisible: true })
        {
            return;
        }

        if (ImGui.Begin("Where My Wagies At", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (_autoRecruiterRunning)
            {
                if (ImGui.Button("Stop"))
                {
                    _autoRecruiterRunning = false;
                }
            }
            else
            {
                if (ImGui.Button("Start Hiring"))
                {
                    _autoRecruiterRunning = true;
                    _autoRecruiterStopReason = null;
                    _autoRecruiterGoldSpent = 0;
                }
            }

            var villageScreen = GameController.IngameState.IngameUi.VillageScreen;
            ImGui.Text(_autoRecruiterRunning
                ? "Status: Running"
                : _autoRecruiterStopReason != null
                    ? $"Status: Idle ({_autoRecruiterStopReason})"
                    : "Status: Idle");
            ImGui.Text($"Gold spent: {_autoRecruiterGoldSpent}/{Settings.AutoRecruiter.MaxGoldToSpend.Value}");
            ImGui.Text($"Workers: {villageScreen.Workers.Count}/{MaxVillageWorkers}");

            ImGui.Separator();

            var useHighQuality = Settings.AutoRecruiter.UseHighQualityReroll.Value;
            if (ImGui.Checkbox("Use high-quality reroll (16k Gold)", ref useHighQuality))
            {
                Settings.AutoRecruiter.UseHighQualityReroll.Value = useHighQuality;
            }

            var onlyCostEfficient = Settings.AutoRecruiter.OnlyCostEfficient.Value;
            if (ImGui.Checkbox("Only consider highest paid job", ref onlyCostEfficient))
            {
                Settings.AutoRecruiter.OnlyCostEfficient.Value = onlyCostEfficient;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Consider workers only for their most expensive job type. Enable to avoid hiring\n" +
                                 "workers that would be overpaid for a lower ranked but overall more expensive job.\n" +
                                 "For example hiring a T5 Miner that has a way higher wage than others because he's also\n" +
                                 "a T3 mapper.\n" +
                                 "Note: Disabling this allows one worker to count towards more than one target quantity.");
            }

            var cancelWithRmb = Settings.AutoRecruiter.CancelWithRightMouseButton.Value;
            if (ImGui.Checkbox("Cancel with right mouse button", ref cancelWithRmb))
            {
                Settings.AutoRecruiter.CancelWithRightMouseButton.Value = cancelWithRmb;
            }

            var maxGold = Settings.AutoRecruiter.MaxGoldToSpend.Value;
            ImGui.PushItemWidth(150);
            if (ImGui.InputInt("Max gold to spend", ref maxGold, 100000))
            {
                Settings.AutoRecruiter.MaxGoldToSpend.Value = Math.Clamp(maxGold, 0, 100000000);
            }

            var clickDelay = Settings.AutoRecruiter.ClickDelayMs.Value;
            if (ImGui.InputInt("Click delay (ms)", ref clickDelay, 25))
            {
                Settings.AutoRecruiter.ClickDelayMs.Value = Math.Clamp(clickDelay, 50, 2000);
            }

            ImGui.PopItemWidth();
            ImGui.Separator();
            
            if (ImGui.BeginTable("AutoRecruiterTargetsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Job");
                ImGui.TableSetupColumn("Min Rank", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Target Qty", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Current Qty");
                ImGui.TableSetupColumn("Always Upgrade");
                ImGui.TableHeadersRow();
                
                foreach (var (jobType, index) in GameController.Game.Files.VillageJobTypes.EntriesList.DistinctBy(j => j.Id).Select((j, i) => (j, i)))
                {
                    if (NonSkillJobTypes.Contains(jobType.Id))
                    {
                        continue;
                    }

                    if (!Settings.AutoRecruiter.Targets.TryGetValue(jobType.Id, out var target))
                    {
                        Settings.AutoRecruiter.Targets[jobType.Id] = target = new RecruiterJobTarget();
                    }
                    
                    target.MinRank = Math.Clamp(target.MinRank, 1, 5);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(jobType.Id);
                    
                    ImGui.TableNextColumn();
                    ImGui.PushItemWidth(100);
                    var minRank = target.MinRank;
                    if (ImGui.InputInt($"##rank_{index}", ref minRank))
                    {
                        target.MinRank = Math.Clamp(minRank, 1, 5);
                    }
                    ImGui.PopItemWidth();
                    
                    ImGui.TableNextColumn();
                    ImGui.PushItemWidth(100);
                    var targetQuantity = target.TargetQuantity;
                    if (ImGui.InputInt($"##target_{index}", ref targetQuantity))
                    {
                        target.TargetQuantity = Math.Max(0, targetQuantity);
                    }
                    ImGui.PopItemWidth();
                    
                    ImGui.TableNextColumn();
                    ImGui.Text(GetCurrentWorkerCount(villageScreen, jobType.Id, target.MinRank).ToString());

                    ImGui.TableNextColumn();
                    var alwaysUpgrade = target.AlwaysHireUpgrades;
                    if (ImGui.Checkbox($"##upgrade_{index}", ref alwaysUpgrade))
                    {
                        target.AlwaysHireUpgrades = alwaysUpgrade;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Always hire workers for this job that are considered upgrades to any\n" +
                                         "existing and currently assigned worker (better rank / cheaper wage).");
                    }
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }
}
