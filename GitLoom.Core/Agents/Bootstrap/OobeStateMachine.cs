using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// The persisted OOBE stages (P2-21 §3.3). Ordered: preflight → feature enablement → (reboot) →
/// resumed after reboot → P2-05 VM import → done. <see cref="RebootPending"/> and
/// <see cref="Resumed"/> straddle the reboot boundary the elevated Scheduled Task bridges.
/// </summary>
public enum OobeStage
{
    Diagnostics,
    EnableFeatures,
    RebootPending,
    Resumed,
    ImportVm,
    Done,
}

/// <summary>
/// The <c>oobe-state.json</c> payload persisted after every transition (in appdata). Unknown fields
/// are tolerated on read (<see cref="Extra"/>) so a newer installer's state file never crashes an
/// older resume. Serialized/round-tripped by <see cref="OobeStateJson"/>.
/// </summary>
public sealed record OobeState
{
    /// <summary>Schema version so a future installer can migrate an old state file.</summary>
    public int SchemaVersion { get; init; } = 1;

    public OobeStage Stage { get; init; } = OobeStage.Diagnostics;

    /// <summary>The two Windows optional features were successfully enabled by the elevated helper.</summary>
    public bool FeaturesEnabled { get; init; }

    /// <summary>Set once the resume Scheduled Task has fired after the reboot.</summary>
    public bool RebootCompleted { get; init; }

    /// <summary>The P2-05 import + first-boot + daemon health chain completed.</summary>
    public bool VmImported { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Forward-compatible bucket for fields a newer installer wrote (tolerated, preserved).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Stable JSON (de)serialization for <see cref="OobeState"/> — invariant, indented, unknown
/// fields tolerated.</summary>
public static class OobeStateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(OobeState state) => JsonSerializer.Serialize(state, Options);

    public static OobeState Deserialize(string json) =>
        JsonSerializer.Deserialize<OobeState>(json, Options)
        ?? throw new InvalidDataException("oobe-state.json deserialized to null.");
}

/// <summary>Persistence seam for the state file so the machine is unit-testable without appdata IO.</summary>
public interface IOobeStateStore
{
    OobeState? Load();
    void Save(OobeState state);
    void Clear();
}

/// <summary>File-backed <see cref="IOobeStateStore"/> writing <c>oobe-state.json</c> atomically
/// (temp-then-replace) so a crash mid-write never leaves a torn state file.</summary>
public sealed class JsonOobeStateStore : IOobeStateStore
{
    private readonly string _path;

    public JsonOobeStateStore(string path) => _path = path;

    /// <summary>The default location: <c>%LOCALAPPDATA%\GitLoom\oobe-state.json</c>.</summary>
    public static string DefaultPath()
        => Path.Combine(GitLoomPaths.DataRoot(), "oobe-state.json");

    public OobeState? Load()
    {
        if (!File.Exists(_path))
            return null;
        return OobeStateJson.Deserialize(File.ReadAllText(_path));
    }

    public void Save(OobeState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, OobeStateJson.Serialize(state));
        // Atomic replace where the platform supports it; fall back for first write.
        if (File.Exists(_path))
            File.Replace(tmp, _path, null);
        else
            File.Move(tmp, _path);
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}

/// <summary>Outcome of the elevated feature-enablement step.</summary>
/// <param name="Succeeded">Both features enabled.</param>
/// <param name="RebootRequired">Windows requires a reboot to finish enabling them (the usual case).</param>
public readonly record struct FeatureEnableResult(bool Succeeded, bool RebootRequired);

/// <summary>
/// The side-effecting steps the machine drives, injected so the pure transition logic is testable.
/// Each is invoked at most once per full install (the machine skips completed stages on resume).
/// </summary>
public sealed record OobeStageHandlers(
    /// <summary>Returns whether preflight passed. On false the machine stops before any modification.</summary>
    Func<CancellationToken, Task<bool>> RunDiagnostics,
    /// <summary>The single-UAC "Construct Sandbox" step: relaunches the elevated helper.</summary>
    Func<CancellationToken, Task<FeatureEnableResult>> EnableFeatures,
    /// <summary>The P2-05 import chain (idempotent).</summary>
    Func<CancellationToken, Task> ImportVm,
    /// <summary>Optional: whether the provisioned VM is STILL registered. When supplied and it reports
    /// false while the persisted state claims the VM was imported, the machine rewinds to re-import (the
    /// user unregistered GitLoomEnv between runs). Null keeps the legacy trust-the-persisted-flag behaviour
    /// — the console driver and unit fixtures that don't provide it are unaffected.</summary>
    Func<CancellationToken, Task<bool>>? VmIsRegistered = null);

/// <summary>The reason a <see cref="OobeStateMachine.RunAsync"/> pass returned before <see cref="OobeStage.Done"/>.</summary>
public enum OobeRunOutcome
{
    /// <summary>Reached <see cref="OobeStage.Done"/> — install complete.</summary>
    Completed,
    /// <summary>Diagnostics failed; nothing was modified. The user fixes the machine and re-runs.</summary>
    BlockedByDiagnostics,
    /// <summary>Features enabled; a reboot is pending. The resume Scheduled Task will re-enter here.</summary>
    AwaitingReboot,
}

/// <summary>The result of one <see cref="OobeStateMachine.RunAsync"/> pass.</summary>
public sealed record OobeRunResult(OobeRunOutcome Outcome, OobeState State);

/// <summary>
/// The P2-21 OOBE state machine. Mirrors the P2-05 bootstrapper's check-then-act, resume-safe shape:
/// each stage runs only if not already complete in the persisted <see cref="OobeState"/>, and the
/// state is saved after every transition. A reboot interrupts between <see cref="OobeStage.EnableFeatures"/>
/// and <see cref="OobeStage.Resumed"/>; the elevated Scheduled Task re-invokes <see cref="RunAsync"/>
/// and the machine picks up at <see cref="OobeStage.RebootPending"/>. Running the resume task twice is
/// idempotent — completed stages no-op.
/// </summary>
public sealed class OobeStateMachine
{
    private readonly IOobeStateStore _store;

    public OobeStateMachine(IOobeStateStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Discards persisted OOBE progress so the next <see cref="RunAsync"/> starts fresh at
    /// <see cref="OobeStage.Diagnostics"/> — the wizard's "start over" affordance.</summary>
    public void Reset() => _store.Clear();

    /// <summary>The persisted stage right now (null = no state file — a fresh install). Used by the
    /// resume-task guard: only <see cref="OobeStage.RebootPending"/> legitimises a registered task.</summary>
    public OobeStage? CurrentStage => _store.Load()?.Stage;

    /// <summary>Loads persisted state (or a fresh <see cref="OobeStage.Diagnostics"/> start) and drives
    /// the machine forward until it completes, blocks on diagnostics, or hands off to the reboot.</summary>
    public async Task<OobeRunResult> RunAsync(OobeStageHandlers handlers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var state = _store.Load() ?? new OobeState();

        // A persisted "VM imported" flag can go stale between runs: the user can `wsl --unregister
        // GitLoomEnv` (e.g. to take a rebuilt payload) and relaunch. Left unchecked the machine reads
        // Stage=Done, returns Completed, and the wizard sails past import straight into steps that operate
        // on a distro that is no longer there — the agent-CLI picker then tries to install onto a missing
        // VM. Re-verify the claim up front and rewind to a fresh import when reality disagrees; the
        // feature-enablement and reboot progress already banked are preserved (only the VM is redone).
        if (state.VmImported
            && handlers.VmIsRegistered is { } vmIsRegistered
            && !await vmIsRegistered(ct).ConfigureAwait(false))
        {
            state = Advance(state with { Stage = OobeStage.ImportVm, VmImported = false });
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            switch (state.Stage)
            {
                case OobeStage.Diagnostics:
                    {
                        var passed = await handlers.RunDiagnostics(ct).ConfigureAwait(false);
                        if (!passed)
                            return new OobeRunResult(OobeRunOutcome.BlockedByDiagnostics, state);
                        state = Advance(state with { Stage = OobeStage.EnableFeatures });
                        break;
                    }

                case OobeStage.EnableFeatures:
                    {
                        var result = await handlers.EnableFeatures(ct).ConfigureAwait(false);
                        if (!result.Succeeded)
                            throw new InvalidOperationException(
                                "Enabling the Windows features failed; the elevated helper reported no success.");

                        if (result.RebootRequired)
                        {
                            state = Advance(state with { Stage = OobeStage.RebootPending, FeaturesEnabled = true });
                            // The elevated resume Scheduled Task is already registered; hand off to the reboot.
                            return new OobeRunResult(OobeRunOutcome.AwaitingReboot, state);
                        }

                        // No reboot needed (rare) — collapse straight through the reboot stages.
                        state = Advance(state with { Stage = OobeStage.Resumed, FeaturesEnabled = true, RebootCompleted = true });
                        break;
                    }

                case OobeStage.RebootPending:
                    {
                        // Only reached when the resume task re-enters after the reboot.
                        state = Advance(state with { Stage = OobeStage.Resumed, RebootCompleted = true });
                        break;
                    }

                case OobeStage.Resumed:
                    {
                        state = Advance(state with { Stage = OobeStage.ImportVm });
                        break;
                    }

                case OobeStage.ImportVm:
                    {
                        await handlers.ImportVm(ct).ConfigureAwait(false);
                        state = Advance(state with { Stage = OobeStage.Done, VmImported = true });
                        break;
                    }

                case OobeStage.Done:
                    return new OobeRunResult(OobeRunOutcome.Completed, state);

                default:
                    throw new InvalidOperationException($"Unknown OOBE stage '{state.Stage}'.");
            }
        }
    }

    private OobeState Advance(OobeState next)
    {
        var stamped = next with { UpdatedUtc = DateTimeOffset.UtcNow };
        _store.Save(stamped);
        return stamped;
    }
}
