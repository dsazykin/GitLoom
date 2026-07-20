using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GitLoom.App.ViewModels.Agents;

using Mainguard.Git;
namespace GitLoom.App.Services;

/// <summary>
/// A persisted snapshot of a per-agent workspace's dock arrangement (P2-13): which of the two
/// layouts, and the pane order. Versioned so a schema change is detected on load and falls back to
/// the default rather than throwing.
/// </summary>
public sealed record DockLayoutState(
    int Version,
    WorkspaceLayoutKind Layout,
    IReadOnlyList<string> ToolOrder)
{
    public const int CurrentVersion = 1;

    public static DockLayoutState Default(WorkspaceLayoutKind layout = WorkspaceLayoutKind.FlightDeck) =>
        new(CurrentVersion, layout, new[] { "terminal", "diff", "staging" });
}

/// <summary>
/// Saves/restores <see cref="DockLayoutState"/> as JSON under the app-data directory (P2-13 step 5).
/// Restore is total: any read/parse failure or a version/schema mismatch yields the default layout,
/// never an exception — a corrupt file can never wedge the workspace shut.
/// </summary>
public sealed class DockLayoutPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _directory;

    /// <summary>Default location: <c>&lt;GitLoom data root&gt;/workspace-layouts</c> — the one
    /// GitLoomPaths data root the rest of the app persists under (previously roaming %AppData%;
    /// Restore is total, so the one-time move simply yields the default layout once).</summary>
    public DockLayoutPersistence()
        : this(Path.Combine(Mainguard.Git.GitLoomPaths.DataRoot(), "workspace-layouts"))
    {
    }

    /// <summary>Test seam: point persistence at a temp directory.</summary>
    public DockLayoutPersistence(string directory) => _directory = directory;

    private string PathFor(string key) => Path.Combine(_directory, $"{Sanitize(key)}.json");

    /// <summary>Persist the layout for a workspace key (e.g. an agent kind). Best-effort — an IO
    /// failure is swallowed so a locked/full disk never breaks the UI.</summary>
    public void Save(string key, DockLayoutState state)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(key), JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Non-fatal: the workspace still opens with its in-memory layout.
        }
    }

    /// <summary>Restore the layout for a key, or the default on absence / parse failure / schema drift.</summary>
    public DockLayoutState Load(string key, WorkspaceLayoutKind fallback = WorkspaceLayoutKind.FlightDeck)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return DockLayoutState.Default(fallback);
            var state = JsonSerializer.Deserialize<DockLayoutState>(File.ReadAllText(path), JsonOptions);
            // Schema drift: unknown version or a malformed payload → default (never throw).
            if (state is null || state.Version != DockLayoutState.CurrentVersion || state.ToolOrder is null or { Count: 0 })
                return DockLayoutState.Default(fallback);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return DockLayoutState.Default(fallback);
        }
    }

    private static string Sanitize(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return string.IsNullOrWhiteSpace(key) ? "default" : key;
    }
}
