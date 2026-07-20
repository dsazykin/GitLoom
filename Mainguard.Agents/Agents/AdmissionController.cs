using System;
using System.Globalization;
using System.IO;

namespace Mainguard.Agents.Agents;

/// <summary>A point-in-time VM memory reading (from <c>/proc/meminfo</c>). Kilobytes, as the file reports.</summary>
public readonly record struct MemorySample(long MemTotalKb, long MemAvailableKb)
{
    /// <summary>Fraction of memory in use (0..1). Unknown total → 0 (never blocks a spawn on no data).</summary>
    public double UsedFraction =>
        MemTotalKb <= 0 ? 0.0 : Math.Clamp(1.0 - ((double)MemAvailableKb / MemTotalKb), 0.0, 1.0);

    /// <summary>Total memory in whole GB (for the honest "16 GB supports 4–6" message).</summary>
    public double TotalGb => MemTotalKb / 1024.0 / 1024.0;
}

/// <summary>
/// P2-08 admission control. Answers <see cref="CanSpawn"/> honestly: below the memory-used threshold
/// (default 85%) a new agent is admitted; at/above it the spawn is refused with a message that states
/// the real ceiling ("Running N agents now; 16 GB supports 4–6 comfortably — free memory or stop an
/// agent"). The <c>/proc/meminfo</c> read is behind an <b>injectable sampler</b> (so tests feed 86%)
/// and cached ≤5 s so a burst of spawn checks does not re-read the file each time. Existing agents are
/// never affected — this gates only new spawns.
/// </summary>
public sealed class AdmissionController
{
    /// <summary>Default: refuse a new spawn once ≥85% of VM memory is in use.</summary>
    public const double DefaultUsedThreshold = 0.85;

    private readonly Func<MemorySample> _sampler;
    private readonly Func<int> _runningAgentCount;
    private readonly Func<DateTimeOffset> _clock;
    private readonly double _usedThreshold;
    private readonly TimeSpan _cacheTtl;
    private readonly object _gate = new();

    private MemorySample _cached;
    private DateTimeOffset _cachedAt;
    private bool _hasCache;

    /// <param name="sampler">Reads current memory. Injected so tests feed a fixed sample; defaults to <c>/proc/meminfo</c>.</param>
    /// <param name="runningAgentCount">Current live-agent count (for the honest message).</param>
    /// <param name="clock">Time source for the ≤5 s sample cache (injected).</param>
    /// <param name="usedThreshold">Used-memory fraction above which spawns are refused.</param>
    /// <param name="cacheTtl">How long a sample is reused (default 5 s).</param>
    public AdmissionController(
        Func<MemorySample>? sampler = null,
        Func<int>? runningAgentCount = null,
        Func<DateTimeOffset>? clock = null,
        double usedThreshold = DefaultUsedThreshold,
        TimeSpan? cacheTtl = null)
    {
        _sampler = sampler ?? ReadProcMeminfo;
        _runningAgentCount = runningAgentCount ?? (() => 0);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _usedThreshold = usedThreshold;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The current (≤5 s cached) memory reading — surfaced as headroom in <c>ListAgents</c> metadata.</summary>
    public MemorySample CurrentSample() => Sample();

    /// <summary>
    /// True when a new agent may be spawned. When false, <paramref name="reason"/> carries the honest
    /// ceiling text. Above the threshold the answer is stable regardless of how many spawn checks run.
    /// </summary>
    public bool CanSpawn(out string reason)
    {
        var sample = Sample();
        if (sample.UsedFraction < _usedThreshold)
        {
            reason = string.Empty;
            return true;
        }

        reason = BuildReason(sample, _runningAgentCount());
        return false;
    }

    private MemorySample Sample()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_hasCache && now - _cachedAt < _cacheTtl)
            {
                return _cached;
            }

            _cached = _sampler();
            _cachedAt = now;
            _hasCache = true;
            return _cached;
        }
    }

    private string BuildReason(MemorySample sample, int runningAgents)
    {
        var totalGb = sample.TotalGb;
        // A rough, honest comfort band: ~one agent per 3 GB (agents are memory-hungry CLIs), never below 1.
        var comfortable = Math.Max(1, (int)Math.Round(totalGb / 3.0));
        var low = Math.Max(1, comfortable - 1);
        var high = comfortable + 1;
        var gbText = totalGb >= 1
            ? $"{Math.Round(totalGb):0} GB"
            : $"{Math.Round(sample.MemTotalKb / 1024.0):0} MB";

        return $"Running {runningAgents} agent{(runningAgents == 1 ? "" : "s")} now; " +
               $"{gbText} supports {low}–{high} comfortably " +
               $"(memory is {Math.Round(sample.UsedFraction * 100):0}% used) — free memory or stop an agent.";
    }

    /// <summary>
    /// Default sampler: parses <c>/proc/meminfo</c> for <c>MemTotal</c> and <c>MemAvailable</c>. On a
    /// host without the file (Windows local-dev), returns an unknown sample (used fraction 0 → admits),
    /// since admission control is a VM-side (WSL2/Linux) concern.
    /// </summary>
    private static MemorySample ReadProcMeminfo()
    {
        try
        {
            if (!File.Exists("/proc/meminfo"))
            {
                return new MemorySample(0, 0);
            }

            long total = 0, available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKb(line);
                }

                if (total > 0 && available > 0)
                {
                    break;
                }
            }

            return new MemorySample(total, available);
        }
        catch (IOException)
        {
            return new MemorySample(0, 0);
        }
    }

    private static long ParseKb(string line)
    {
        // Format: "MemTotal:       16371512 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
            ? kb
            : 0;
    }
}
