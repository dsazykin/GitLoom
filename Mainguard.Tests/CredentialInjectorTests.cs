using System;
using System.Collections.Generic;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-01 — <see cref="CredentialInjector.BuildEnvFileContent"/> is pure and in-memory: it emits
/// newline-terminated <c>KEY=value</c> lines in dictionary order and rejects any value containing a
/// newline (env-file integrity). No file IO — asserted by API shape.
/// </summary>
public class CredentialInjectorTests
{
    // #7 (plan §7) — newline-terminated KEY=value pairs, order preserved, pure (returns a string).
    [Fact]
    public void BuildEnvFileContent_Purity_And_Format()
    {
        // Insertion-ordered dictionary so the expected order is deterministic.
        var secrets = new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "sk-ant-abc",
            ["OPENAI_API_KEY"] = "sk-openai-def",
        };

        var content = CredentialInjector.BuildEnvFileContent(secrets);

        Assert.Equal("ANTHROPIC_API_KEY=sk-ant-abc\nOPENAI_API_KEY=sk-openai-def\n", content);
        Assert.EndsWith("\n", content); // every line, including the last, is newline-terminated
    }

    [Fact]
    public void BuildEnvFileContent_Empty_ReturnsEmptyString()
    {
        Assert.Equal("", CredentialInjector.BuildEnvFileContent(new Dictionary<string, string>()));
    }

    // #8 (plan §7) — \n and \r values are rejected with a typed ArgumentException.
    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("carriage\rreturn")]
    [InlineData("trailing\n")]
    public void BuildEnvFileContent_NewlineValue_Throws(string badValue)
    {
        var secrets = new Dictionary<string, string> { ["KEY"] = badValue };
        Assert.Throws<ArgumentException>(() => CredentialInjector.BuildEnvFileContent(secrets));
    }
}
