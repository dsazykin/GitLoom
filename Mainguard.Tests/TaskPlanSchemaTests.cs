using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-14 test 1 (TI-P2-14.1) — the TaskPlan JSON-schema corpus: valid + every invalid shape maps to an
/// exact, deterministic error set, and unknown top-level fields are rejected (forward-compat honesty).
/// </summary>
public class TaskPlanSchemaTests
{
    [Fact]
    public void TaskPlan_SchemaCorpus_ValidPlan_ParsesFields()
    {
        var result = TaskPlanSchema.Validate(
            """{"scope":["src/Auth/Foo.cs","tests/AuthTests.cs"],"approach":"Extract ITokenClock","testStrategy":"AuthTests green"}""");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Fields);
        Assert.Equal(new[] { "src/Auth/Foo.cs", "tests/AuthTests.cs" }, result.Fields!.Scope);
        Assert.Equal("Extract ITokenClock", result.Fields.Approach);
        Assert.Equal("AuthTests green", result.Fields.TestStrategy);
    }

    [Theory]
    [MemberData(nameof(InvalidCorpus))]
    public void TaskPlan_SchemaCorpus_InvalidPlans_YieldExactErrorSets(string json, string[] expectedErrors)
    {
        var result = TaskPlanSchema.Validate(json);

        Assert.False(result.IsValid);
        Assert.Null(result.Fields);
        Assert.Equal(
            expectedErrors.OrderBy(e => e).ToArray(),
            result.Errors.OrderBy(e => e).ToArray());
    }

    public static IEnumerable<object[]> InvalidCorpus()
    {
        yield return new object[] { "not json at all", new[] { "plan is not valid JSON" } };
        yield return new object[] { "[1,2,3]", new[] { "plan must be a JSON object" } };
        yield return new object[] { "   ", new[] { "plan is empty" } };

        yield return new object[]
        {
            """{"approach":"x","testStrategy":"t"}""",
            new[] { "scope is required" },
        };
        yield return new object[]
        {
            """{"scope":[],"approach":"x","testStrategy":"t"}""",
            new[] { "scope must list at least one file" },
        };
        yield return new object[]
        {
            """{"scope":"one-file.cs","approach":"x","testStrategy":"t"}""",
            new[] { "scope must be an array of file paths" },
        };
        yield return new object[]
        {
            """{"scope":[123],"approach":"x","testStrategy":"t"}""",
            new[] { "scope[0] must be a non-empty string" },
        };
        yield return new object[]
        {
            """{"scope":["a.cs",""],"approach":"x","testStrategy":"t"}""",
            new[] { "scope[1] must be a non-empty string" },
        };
        yield return new object[]
        {
            """{"scope":["a.cs"],"testStrategy":"t"}""",
            new[] { "approach is required" },
        };
        yield return new object[]
        {
            """{"scope":["a.cs"],"approach":5,"testStrategy":"t"}""",
            new[] { "approach must be a non-empty string" },
        };
        yield return new object[]
        {
            """{"scope":["a.cs"],"approach":"x"}""",
            new[] { "testStrategy is required" },
        };
        yield return new object[]
        {
            """{"scope":["a.cs"],"approach":"x","testStrategy":"t","danger":true}""",
            new[] { "unknown field 'danger'" },
        };
        // Multiple errors collected (never fail-fast): unknown field + all three required fields.
        yield return new object[]
        {
            """{"foo":1}""",
            new[] { "unknown field 'foo'", "scope is required", "approach is required", "testStrategy is required" },
        };
        // Oversized scope (the guard).
        yield return new object[]
        {
            OversizedScopePlan(),
            new[] { $"scope exceeds the maximum of {TaskPlanSchema.MaxScopeFiles} files" },
        };
    }

    private static string OversizedScopePlan()
    {
        var files = string.Join(",", Enumerable.Range(0, TaskPlanSchema.MaxScopeFiles + 1).Select(i => $"\"f{i}.cs\""));
        return $$"""{"scope":[{{files}}],"approach":"x","testStrategy":"t"}""";
    }
}
