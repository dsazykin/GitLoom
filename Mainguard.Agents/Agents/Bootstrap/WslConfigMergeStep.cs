using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 3: merge — never clobber — <c>%UserProfile%\.wslconfig</c>. Adds only GitLoom's <c>[wsl2]</c>
/// keys (<c>memory</c> = min(50% RAM, 8GB); <c>autoMemoryReclaim</c> = gradual), preserving every
/// other section/key/comment byte-for-byte, and writes a timestamped backup <b>before</b> the write.
/// An existing user value always wins (see <see cref="WslConfigMerger"/>).
/// </summary>
public sealed class WslConfigMergeStep : IBootstrapStep
{
    private const long EightGiB = 8L * 1024 * 1024 * 1024;

    private readonly IBootstrapFileSystem _fs;

    public WslConfigMergeStep(IBootstrapFileSystem fs) => _fs = fs;

    public string Name => "Configure WSL memory";

    private IReadOnlyDictionary<string, string> OurKeys() => new Dictionary<string, string>
    {
        ["memory"] = ComputeMemoryValue(_fs.TotalPhysicalMemoryBytes),
        ["autoMemoryReclaim"] = "gradual",
    };

    public Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        var existing = _fs.ReadWslConfig();
        var merged = WslConfigMerger.Merge(existing, OurKeys());
        // Satisfied only when merging changes nothing — i.e. our keys are already present. A missing
        // file (existing == null) is never satisfied.
        return Task.FromResult(existing != null && string.Equals(merged, existing, StringComparison.Ordinal));
    }

    public Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        var existing = _fs.ReadWslConfig();
        var merged = WslConfigMerger.Merge(existing, OurKeys());

        // Back up BEFORE writing, and never clobber user content (invariant §5.4).
        log.Report($"Backing up {_fs.WslConfigPath}…");
        _fs.BackupWslConfig();

        log.Report("Merging Mainguard [wsl2] defaults…");
        _fs.WriteWslConfig(merged);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The <c>memory=</c> default: min(50% of physical RAM, 8GB), floored to whole GB (min 1GB),
    /// formatted like <c>6GB</c>. Pure — table-tested.
    /// </summary>
    public static string ComputeMemoryValue(long totalPhysicalMemoryBytes)
    {
        var halfRam = Math.Max(0, totalPhysicalMemoryBytes) / 2;
        var capped = Math.Min(halfRam, EightGiB);
        var gb = (int)(capped / (1024L * 1024 * 1024));
        if (gb < 1)
            gb = 1;
        return $"{gb}GB";
    }
}
