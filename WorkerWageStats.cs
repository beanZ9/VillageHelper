using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Elements.Village;
using Newtonsoft.Json;

namespace VillageHelper;

public class WageBucket
{
    public int Count { get; set; }
    public double SumWage { get; set; }
    public double SumWageSq { get; set; }
}

public record JobWageStats(double GoldPerWorkUnit, int SampleCount, bool IsDefault);

// The best paying skill determines a worker's wage
// wage ~= GoldPerWorkUnit[job] * Speed(rank of best paying skill) * per-worker roll

public class WorkerWageStats
{
    // Values averaged from over 4000 worker samples in 3.29: Allflame
    // These hardcoded values are only used initially until that job has MinCalibrationSamples
    private static readonly Dictionary<string, double> DefaultGoldPerWorkUnit = new()
    {
        ["Mining"] = 5.091,
        ["Farming"] = 6.348,
        ["Shipping"] = 7.611,
        ["Disenchanting"] = 12.960,
        ["Smelting"] = 19.256,
        ["Mapping"] = 51.453,
        ["Fishing"] = 2.833,
    };

    public const int MinCalibrationSamples = 30;

    // skip worker samples where contribution of two skills to the wage is within margin / unclear
    private const double AttributionMargin = 1.15;

    private const int MaxRecordedWorkersCached = 8192;

    private readonly GameController _gameController;
    private readonly string _filePath;
    private readonly HashSet<string> _recordedWorkers = [];
    private bool _averageWorkerWagesDirty = true;
    private Dictionary<string, JobWageStats> _averageWorkerWagesCache;
    private bool _dirty;

    public WorkerWageStats(GameController gameController, string configDirectory)
    {
        _gameController = gameController;
        _filePath = Path.Combine(configDirectory, "WorkerWageStats.json");

        try
        {
            if (File.Exists(_filePath))
            {
                WageBuckets = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<int, WageBucket>>>(File.ReadAllText(_filePath)) ?? new();
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to load worker wage stats: {ex}");
        }
    }

    // job id -> skill rank -> aggregated wage data
    public Dictionary<string, Dictionary<int, WageBucket>> WageBuckets { get; private set; } = new();

    public int TotalWageSamples => WageBuckets.Values.Sum(byRank => byRank.Values.Sum(b => b.Count));

    public Dictionary<string, JobWageStats> AverageWorkerWages
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

    public double Speed(int rank) => _gameController.Game.Files.VillageJobSkillLevels.GetByLevel(rank)?.Speed ?? 0;

    public void Reset()
    {
        WageBuckets.Clear();
        _averageWorkerWagesDirty = true;
        _dirty = true;
    }

    public void SaveIfDirty()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            File.WriteAllText(_filePath, JsonConvert.SerializeObject(WageBuckets, Formatting.Indented));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"VillageHelper: failed to save worker wage stats: {ex}");
        }

        _dirty = false;
    }

    public void Record(IEnumerable<BaseVillageWorker> workers)
    {
        var wages = AverageWorkerWages;
        foreach (var worker in workers)
        {
            RecordWorker(worker, wages);
        }
    }

    private void RecordWorker(BaseVillageWorker worker, Dictionary<string, JobWageStats> wages)
    {
        var jobRanks = worker.JobRanks.Where(r => r.Key?.Id != null).ToDictionary(r => r.Key.Id, r => r.Value);
        if (jobRanks.Count == 0)
        {
            return;
        }

        // workers are not unique by name
        var signature = $"{worker.WorkerName}|{worker.WagePerHour}|{string.Join(",", jobRanks.OrderBy(r => r.Key).Select(r => $"{r.Key}:{r.Value}"))}";
        if (_recordedWorkers.Count >= MaxRecordedWorkersCached)
        {
            _recordedWorkers.Clear();
        }

        if (!_recordedWorkers.Add(signature))
        {
            return;
        }

        if (DominantJob(jobRanks, wages) is not { } dominant)
        {
            return;
        }

        var (jobId, rank) = dominant;
        if (!WageBuckets.TryGetValue(jobId, out var byRank))
        {
            WageBuckets[jobId] = byRank = new Dictionary<int, WageBucket>();
        }

        if (!byRank.TryGetValue(rank, out var bucket))
        {
            byRank[rank] = bucket = new WageBucket();
        }

        bucket.Count++;
        bucket.SumWage += worker.WagePerHour;
        bucket.SumWageSq += (double)worker.WagePerHour * worker.WagePerHour;
        _averageWorkerWagesDirty = true;
        _dirty = true;
    }

    // The skill that determines this worker's wage, or null when the top two are too close to call.
    private (string JobId, int Rank)? DominantJob(IEnumerable<KeyValuePair<string, int>> jobRanks, Dictionary<string, JobWageStats> wages)
    {
        var ranked = jobRanks
            .Where(r => r.Key != null)
            .Select(r => (jobId: r.Key, rank: r.Value, predicted: (wages.TryGetValue(r.Key, out var s) ? s.GoldPerWorkUnit : 0) * Speed(r.Value)))
            .OrderByDescending(x => x.predicted)
            .ToList();

        if (ranked is not [{ predicted: > 0 } best, ..])
        {
            return null;
        }

        return ranked.Count > 1 && best.predicted < ranked[1].predicted * AttributionMargin
            ? null
            : (best.jobId, best.rank);
    }

    private Dictionary<string, JobWageStats> ComputeAverageWorkerWages()
    {
        var result = new Dictionary<string, JobWageStats>();
        foreach (var jobId in DefaultGoldPerWorkUnit.Keys.Concat(WageBuckets.Keys).Distinct())
        {
            double sumSpeedWage = 0, sumSpeedSq = 0;
            var count = 0;
            foreach (var (rank, bucket) in WageBuckets.GetValueOrDefault(jobId) ?? new Dictionary<int, WageBucket>())
            {
                var speed = Speed(rank);
                if (bucket.Count <= 0 || speed <= 0)
                {
                    continue;
                }

                sumSpeedWage += speed * bucket.SumWage;
                sumSpeedSq += bucket.Count * speed * speed;
                count += bucket.Count;
            }

            if (count >= MinCalibrationSamples && sumSpeedSq > 0)
            {
                result[jobId] = new JobWageStats(sumSpeedWage / sumSpeedSq, count, false);
            }
            else if (DefaultGoldPerWorkUnit.TryGetValue(jobId, out var value))
            {
                result[jobId] = new JobWageStats(value, count, true);
            }
        }

        return result;
    }
}
