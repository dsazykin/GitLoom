using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-21 #2/#3 / plan §6 #2 + §4 edge rows: every check returns Pass or an actionable Fail (message
// + doc link), ARM64 is an explicit unsupported gate, and any hard fail sets HardStop so the OOBE
// stops before ANY system modification. All Windows probes are behind ISystemProbe fakes.
public class SystemDiagnosticsTests
{
    // ---- #2: each check Pass or actionable Fail --------------------------------------------------

    [Fact]
    public async Task Diagnostics_HealthyMachine_AllPass_CanProceed()
    {
        var report = await Run(Healthy());
        Assert.True(report.CanProceed);
        Assert.False(report.HardStop);
        Assert.All(report.Checks, c => Assert.Equal(DiagnosticStatus.Pass, c.Status));
    }

    [Theory]
    [InlineData("virt")]
    [InlineData("os")]
    [InlineData("wsl")]
    [InlineData("disk")]
    public async Task Diagnostics_EachFailure_IsActionable(string failingId)
    {
        var probe = Healthy();
        var wsl = ReadyWsl();
        switch (failingId)
        {
            case "virt": probe.Virtualization = new VirtualizationInfo(false, false); break;
            case "os": probe.Os = new OsBuildInfo(true, 10, 19045); break; // Windows 10
            case "wsl": wsl = new FakeWslStatusProbe(new WslStatusReport(WslInstallState.NotInstalled, null, null, null)); break;
            case "disk": probe.FreeDisk = 5L * 1024 * 1024 * 1024; break; // 5 GB
        }

        var report = await new SystemDiagnostics(probe, wsl).RunAsync(CancellationToken.None);
        var check = report.Checks.Single(c => c.Id == failingId);

        Assert.Equal(DiagnosticStatus.Fail, check.Status);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
        Assert.False(string.IsNullOrWhiteSpace(check.DocLink));
        Assert.StartsWith("https://", check.DocLink);
        Assert.True(report.HardStop);
    }

    [Fact]
    public async Task Diagnostics_VirtualizationDisabled_MentionsBios_NothingModified()
    {
        var probe = Healthy();
        probe.Virtualization = new VirtualizationInfo(false, false);

        var report = await new SystemDiagnostics(probe, ReadyWsl()).RunAsync(CancellationToken.None);
        var virt = report.Checks.Single(c => c.Id == "virt");

        Assert.Equal(DiagnosticStatus.Fail, virt.Status);
        Assert.Contains("BIOS", virt.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.HardStop, "a firmware-virtualization fail MUST hard-stop before modification");
    }

    // ---- #2 (ARM64) / §4 edge row: explicit unsupported gate at entry, short-circuits everything ---

    [Fact]
    public async Task Diagnostics_Arm64_IsExplicitUnsupportedGate_ShortCircuits()
    {
        var probe = Healthy();
        probe.Architecture = Architecture.Arm64;

        var report = await new SystemDiagnostics(probe, ReadyWsl()).RunAsync(CancellationToken.None);

        var only = Assert.Single(report.Checks);
        Assert.Equal("arch", only.Id);
        Assert.Equal(DiagnosticStatus.Unsupported, only.Status);
        Assert.Contains("ARM64", only.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(only.DocLink));
        Assert.True(report.HardStop);
        Assert.False(report.CanProceed);
    }

    // ---- #3: any hard fail → HardStop set (the state-ordering guard the OOBE reads) ---------------

    [Fact]
    public async Task Diagnostics_AnyHardFail_SetsHardStop_BeforeAnySystemModification()
    {
        var probe = Healthy();
        probe.FreeDisk = 1L * 1024 * 1024 * 1024; // 1 GB — below the 20 GB floor

        var report = await new SystemDiagnostics(probe, ReadyWsl()).RunAsync(CancellationToken.None);

        Assert.True(report.HardStop);
        Assert.False(report.CanProceed);
        // The OOBE state machine consults CanProceed BEFORE EnableFeatures — proven here by the flag.
        Assert.Contains(report.Failures, f => f.Id == "disk");
    }

    [Fact]
    public async Task Diagnostics_DiskExactlyAtFloor_Passes()
    {
        var probe = Healthy();
        probe.FreeDisk = SystemDiagnostics.MinFreeDiskBytes;
        var report = await new SystemDiagnostics(probe, ReadyWsl()).RunAsync(CancellationToken.None);
        Assert.Equal(DiagnosticStatus.Pass, report.Checks.Single(c => c.Id == "disk").Status);
    }

    // ---- Audit fix #6: administrator-account gate --------------------------------------------------

    [Fact]
    public async Task Diagnostics_StandardUserAccount_HardStops_WithActionableMessage()
    {
        // A standard user elevating with a DIFFERENT admin account would install the runtime under
        // that other account (resume task + per-user WSL distro) — setup must stop honestly first.
        var probe = Healthy();
        probe.UserIsAdministrator = false;

        var report = await Run(probe);

        Assert.True(report.HardStop);
        var admin = Assert.Single(report.Failures, f => f.Id == "admin");
        Assert.Equal(DiagnosticStatus.Fail, admin.Status);
        Assert.False(string.IsNullOrWhiteSpace(admin.Message));
        Assert.Equal(SystemDiagnostics.DocAdmin, admin.DocLink);
        Assert.Contains("administrator", admin.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostics_UacFilteredAdmin_Passes()
    {
        // The probe answers "can this user elevate as themselves" — an unelevated admin is a pass.
        var report = await Run(Healthy());
        Assert.Equal(DiagnosticStatus.Pass, report.Checks.Single(c => c.Id == "admin").Status);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Task<DiagnosticReport> Run(FakeSystemProbe probe) =>
        new SystemDiagnostics(probe, ReadyWsl()).RunAsync(CancellationToken.None);

    private static FakeSystemProbe Healthy() => new()
    {
        Architecture = Architecture.X64,
        Os = new OsBuildInfo(true, 10, 22631),          // Windows 11 23H2
        Virtualization = new VirtualizationInfo(true, true),
        FreeDisk = 120L * 1024 * 1024 * 1024,            // 120 GB
    };

    private static FakeWslStatusProbe ReadyWsl() =>
        new(new WslStatusReport(WslInstallState.Wsl2Ready, "2", "2.0.9.0", "5.15.133.1-1"));

    private sealed class FakeSystemProbe : ISystemProbe
    {
        public Architecture Architecture { get; set; } = Architecture.X64;
        public OsBuildInfo Os { get; set; }
        public VirtualizationInfo Virtualization { get; set; }
        public long FreeDisk { get; set; }

        public bool UserIsAdministrator { get; set; } = true;

        Architecture ISystemProbe.OsArchitecture => Architecture;
        public OsBuildInfo GetOsBuild() => Os;
        public VirtualizationInfo GetVirtualization() => Virtualization;
        public long GetFreeDiskBytes() => FreeDisk;
        public bool IsUserAdministrator() => UserIsAdministrator;
    }

    private sealed class FakeWslStatusProbe : IWslStatusProbe
    {
        private readonly WslStatusReport _report;
        public FakeWslStatusProbe(WslStatusReport report) => _report = report;
        public Task<WslStatusReport> QueryAsync(CancellationToken ct) => Task.FromResult(_report);
    }
}
