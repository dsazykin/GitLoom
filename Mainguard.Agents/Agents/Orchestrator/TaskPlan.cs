using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The three schema-validated fields of a P2-14 plan (contract §2 — the two-phase spawn). Load-bearing:
/// <see cref="Scope"/> is what P2-11's out-of-scope flag (SA-1/F6) compares diffs against, so a plan binds
/// to a scope. These fields combine with a daemon-assigned id/title/budget into the canonical
/// <c>Mainguard.Agents.Agents.TaskPlan</c> (the one plan type the whole stack — the P2-11 detector, the
/// approval card, and the daemon — share; there is deliberately no second <c>TaskPlan</c>).
/// </summary>
public sealed record TaskPlanFields(
    IReadOnlyList<string> Scope,
    string Approach,
    string TestStrategy);

/// <summary>The outcome of validating a candidate plan JSON against the P2-14 schema.</summary>
/// <param name="IsValid">True iff <paramref name="Errors"/> is empty.</param>
/// <param name="Errors">The full, deterministic error set (never fail-fast — the corpus test asserts exact sets).</param>
/// <param name="Fields">The parsed fields when valid; otherwise null.</param>
public sealed record TaskPlanValidationResult(bool IsValid, IReadOnlyList<string> Errors, TaskPlanFields? Fields)
{
    public static TaskPlanValidationResult Valid(TaskPlanFields fields) =>
        new(true, Array.Empty<string>(), fields);

    public static TaskPlanValidationResult Invalid(IReadOnlyList<string> errors) =>
        new(false, errors, null);
}

/// <summary>
/// The embedded JSON schema + validator for <see cref="TaskPlan"/>. Deliberately hand-rolled (no external
/// schema dependency) so the error set is stable and every rule is unit-pinned by the corpus test.
///
/// <para><b>Unknown top-level fields are rejected</b> (forward-compat honesty — plan §3.1): a coordinator
/// cannot smuggle an out-of-band field past the human by hiding it in an unknown key. All errors are
/// collected (not fail-fast) so the corpus can assert exact sets.</para>
/// </summary>
public static class TaskPlanSchema
{
    /// <summary>The only permitted top-level keys.</summary>
    public static readonly IReadOnlyList<string> AllowedFields = new[] { "scope", "approach", "testStrategy" };

    /// <summary>Reject a plan whose scope lists more than this many files (oversized guard).</summary>
    public const int MaxScopeFiles = 200;

    /// <summary>Reject a single scope path / field longer than this (oversized guard).</summary>
    public const int MaxFieldLength = 4_000;

    /// <summary>Reject a plan document larger than this many bytes (oversized guard).</summary>
    public const int MaxPlanBytes = 64 * 1024;

    /// <summary>Validate a candidate plan JSON, returning the full error set (empty ⇒ valid).</summary>
    public static TaskPlanValidationResult Validate(string? json)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add("plan is empty");
            return TaskPlanValidationResult.Invalid(errors);
        }

        if (json.Length > MaxPlanBytes)
        {
            errors.Add($"plan exceeds the maximum size of {MaxPlanBytes} bytes");
            return TaskPlanValidationResult.Invalid(errors);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Clone so the element outlives the disposed document.
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            errors.Add("plan is not valid JSON");
            return TaskPlanValidationResult.Invalid(errors);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("plan must be a JSON object");
            return TaskPlanValidationResult.Invalid(errors);
        }

        // Unknown top-level fields → rejected (deterministic, sorted for a stable error set).
        foreach (var prop in root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!AllowedFields.Contains(prop))
            {
                errors.Add($"unknown field '{prop}'");
            }
        }

        var scope = ValidateScope(root, errors);
        var approach = ValidateStringField(root, "approach", errors);
        var testStrategy = ValidateStringField(root, "testStrategy", errors);

        if (errors.Count > 0)
        {
            return TaskPlanValidationResult.Invalid(errors);
        }

        return TaskPlanValidationResult.Valid(new TaskPlanFields(scope!, approach!, testStrategy!));
    }

    private static IReadOnlyList<string>? ValidateScope(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("scope", out var scope))
        {
            errors.Add("scope is required");
            return null;
        }

        if (scope.ValueKind != JsonValueKind.Array)
        {
            errors.Add("scope must be an array of file paths");
            return null;
        }

        var files = new List<string>();
        var index = 0;
        var hadElementError = false;
        foreach (var element in scope.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                errors.Add($"scope[{index}] must be a non-empty string");
                hadElementError = true;
            }
            else
            {
                var value = element.GetString()!;
                if (value.Length > MaxFieldLength)
                {
                    errors.Add($"scope[{index}] exceeds the maximum length of {MaxFieldLength}");
                    hadElementError = true;
                }
                else
                {
                    files.Add(value);
                }
            }

            index++;
        }

        if (index == 0)
        {
            errors.Add("scope must list at least one file");
            return null;
        }

        if (index > MaxScopeFiles)
        {
            errors.Add($"scope exceeds the maximum of {MaxScopeFiles} files");
        }

        return hadElementError ? null : files;
    }

    private static string? ValidateStringField(JsonElement root, string field, List<string> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors.Add($"{field} is required");
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            errors.Add($"{field} must be a non-empty string");
            return null;
        }

        var value = element.GetString()!;
        if (value.Length > MaxFieldLength)
        {
            errors.Add($"{field} exceeds the maximum length of {MaxFieldLength}");
            return null;
        }

        return value;
    }

    /// <summary>Serialize the schema fields to canonical JSON (for persistence / the approval card).</summary>
    public static string Serialize(TaskPlanFields fields) => JsonSerializer.Serialize(new
    {
        scope = fields.Scope,
        approach = fields.Approach,
        testStrategy = fields.TestStrategy,
    });
}
