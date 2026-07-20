using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-48 regression tests for <see cref="RunAsElevationLauncher"/>'s result-file hygiene. The launch
/// itself (ShellExecuteEx + UAC) is Windows-only and exercised by the human install matrix; what IS
/// portable — and was the bug — is the handling of the JSON result file across attempts: a stale
/// <c>elevated-result.json</c> from an earlier run must never be read back as the current attempt's
/// outcome (a launch that failed before the helper ever ran would otherwise "succeed" on stale data).
/// </summary>
public class ElevationLauncherTests
{
    [Fact]
    public async Task ConstructSandbox_DeletesStaleResultFile_BeforeAnyLaunch()
    {
        var dir = Directory.CreateTempSubdirectory("mainguard-elevation-test").FullName;
        try
        {
            var resultPath = Path.Combine(dir, "elevated-result.json");
            File.WriteAllText(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = false,
                ResumeTaskRegistered = false,
            }.Serialize());

            // A helper path that exists nowhere → the launch fails before any process starts…
            var missingHelper = Path.Combine(dir, "Mainguard.Installer.Elevated.exe");
            var launcher = new RunAsElevationLauncher(missingHelper, "resume.exe", resultPath);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));

            // …and the stale result must already be gone, so no code path can mistake it for success.
            Assert.False(File.Exists(resultPath),
                "the stale elevated-result.json survived a failed launch attempt");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConstructSandbox_MissingHelper_ReportsActionablePath()
    {
        var dir = Directory.CreateTempSubdirectory("mainguard-elevation-test").FullName;
        try
        {
            var missingHelper = Path.Combine(dir, "Mainguard.Installer.Elevated.exe");
            var launcher = new RunAsElevationLauncher(
                missingHelper, "resume.exe", Path.Combine(dir, "elevated-result.json"));

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));
            Assert.Contains(missingHelper, ex.Message);
            Assert.Contains("reinstall or rebuild", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
