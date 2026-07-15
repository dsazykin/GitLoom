using System;
using System.IO;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Default <see cref="IBootstrapFileSystem"/> over the real disk: <c>%UserProfile%\.wslconfig</c>,
/// timestamped backups written before any write, and host-RAM detection. The one place P2-05 touches
/// the filesystem, so the steps stay pure/testable.
/// </summary>
public sealed class BootstrapFileSystem : IBootstrapFileSystem
{
    public BootstrapFileSystem(string? userProfileDir = null)
    {
        // GitLoomPaths.HomeDirectory, not GetFolderPath: the default-option GetFolderPath verifies
        // the directory exists and returns "" when it doesn't, silently making this path relative.
        var profile = userProfileDir ?? GitLoomPaths.HomeDirectory();
        WslConfigPath = Path.Combine(profile, ".wslconfig");
    }

    public string WslConfigPath { get; }

    public string? ReadWslConfig() =>
        File.Exists(WslConfigPath) ? File.ReadAllText(WslConfigPath) : null;

    public void BackupWslConfig()
    {
        if (!File.Exists(WslConfigPath))
            return;

        // Timestamped so an existing backup is never clobbered (invariant §5.4).
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backup = WslConfigPath + $".gitloom.{stamp}.bak";
        File.Copy(WslConfigPath, backup, overwrite: false);
    }

    public void WriteWslConfig(string content) => File.WriteAllText(WslConfigPath, content);

    public bool FileExists(string path) => File.Exists(path);

    // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is the plan-sanctioned GlobalMemoryStatusEx
    // stand-in for the memory= default computation.
    public long TotalPhysicalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}
