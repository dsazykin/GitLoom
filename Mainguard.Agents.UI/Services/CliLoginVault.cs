using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The host-side persistence format for a CLI's interactive-login state (P2-01 alignment: secrets
/// live ONLY in the OS-backed keyring — the agent side is tmpfs by design). One keyring entry per
/// adapter kind (<c>cli_login_&lt;adapterId&gt;</c>) holding a JSON object of $HOME-relative path →
/// base64 content, e.g. <c>{".claude/.credentials.json": "eyJ..."}</c>. Pure (de)serialization —
/// the keyring get/set stays with the caller's injectable keystore funcs so tests never touch a
/// real keyring.
/// </summary>
public static class CliLoginVault
{
    /// <summary>The keyring entry prefix; the suffix is the adapter id (the agentKind).</summary>
    public const string KeystoreKeyPrefix = "cli_login_";

    /// <summary>The keyring entry name for one adapter's saved login state.</summary>
    public static string KeystoreKeyFor(string agentKind) => KeystoreKeyPrefix + agentKind;

    /// <summary>Parses a stored vault value into login files. A null/blank/corrupt value (a hand-
    /// edited keyring file, an interrupted write) yields empty — the CLI just asks for a fresh
    /// login, which is the pre-vault behavior, never a crash.</summary>
    public static IReadOnlyList<CliLoginFile> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<CliLoginFile>();
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stored);
            if (map is null)
            {
                return Array.Empty<CliLoginFile>();
            }

            var files = new List<CliLoginFile>(map.Count);
            foreach (var (path, base64) in map)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(base64))
                {
                    continue;
                }

                try
                {
                    var content = Convert.FromBase64String(base64);
                    if (content.Length > 0)
                    {
                        files.Add(new CliLoginFile(path, content));
                    }
                }
                catch (FormatException)
                {
                    // One corrupt entry loses that file, not the whole vault.
                }
            }

            return files;
        }
        catch (JsonException)
        {
            return Array.Empty<CliLoginFile>();
        }
    }

    /// <summary>
    /// Serializes the vault after folding <paramref name="harvested"/> into <paramref name="stored"/>:
    /// a harvested path replaces its stored copy (the jail's version is always newer), while stored
    /// paths the harvest didn't return are KEPT — a file absent from one session (e.g. the CLI
    /// hadn't recreated it yet) must not erase a working login. Returns null when there is nothing
    /// to store (the caller skips the keyring write).
    /// </summary>
    public static string? MergeAndSerialize(string? stored, IReadOnlyList<CliLoginFile> harvested)
    {
        var merged = Parse(stored).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        foreach (var file in harvested)
        {
            if (!string.IsNullOrWhiteSpace(file.Path) && file.Content is { Length: > 0 })
            {
                merged[file.Path] = file.Content;
            }
        }

        if (merged.Count == 0)
        {
            return null;
        }

        var map = merged.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value), StringComparer.Ordinal);
        return JsonSerializer.Serialize(map);
    }
}
