using System.Linq;
using GitLoom.Core.Safety;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-30 — the pure secret-detection catalog. Each named rule matches a planted sample, the rule
/// never surfaces the value (it only ever returns a bool), and innocuous text does not false-positive.
/// No IO, no git.
/// </summary>
public class SecretPatternsTests
{
    // Planted, syntactically-valid-but-fake samples for each rule. Values are distinctive so the
    // "never leaks" checks elsewhere can grep for them.
    [Theory]
    [InlineData("aws-access-key-id", "AWS_KEY=AKIAIOSFODNN7EXAMPLE")]
    [InlineData("aws-secret-access-key", "aws_secret_access_key = \"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\"")]
    [InlineData("github-token", "token: ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKL")]
    [InlineData("github-token", "GITHUB_PAT=github_pat_11ABCDE0123456789abcdefg_helloworldabcdefghij")]
    [InlineData("google-api-key", "key=AIzaSyA0123456789012345678901234567abcd")]
    [InlineData("slack-token", "SLACK=xoxb-000000000000-abcDEF123456")]
    [InlineData("private-key-block", "-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("jwt", "auth = eyJhbGciOiJI.eyJzdWIiOiIxMjM0.SflKxwRJSMeKKF2Q")]
    [InlineData("generic-secret-assignment", "password = \"s3cr3tV4lu3_x9Q2wZ\"")]
    public void Rule_ShouldMatch_PlantedSample(string rule, string line)
    {
        var pattern = SecretPatterns.All.Single(p => p.Rule == rule);
        Assert.True(pattern.IsMatch(line), $"expected {rule} to match: {line}");
    }

    [Theory]
    [InlineData("This function returns the user's password hash.")]
    [InlineData("// TODO: refactor the api_key loader before release")]
    [InlineData("const greeting = \"hello world\";")]
    [InlineData("AKIA is the AWS access-key-id prefix.")]
    [InlineData("password = \"changeme\"")]              // placeholder, and too short
    [InlineData("api_key = \"your_api_key_here\"")]       // placeholder token
    [InlineData("secret = \"xxxxxxxxxxxxxxxx\"")]         // zero-entropy filler
    public void Rules_ShouldNotFalsePositive_OnInnocuousText(string line)
    {
        foreach (var pattern in SecretPatterns.All)
        {
            Assert.False(pattern.IsMatch(line), $"{pattern.Rule} false-positived on: {line}");
        }
    }

    [Fact]
    public void IsMatch_ReturnsOnlyBool_SoTheValueIsNeverExposed()
    {
        // The public surface of a rule is bool — there is no API that returns the matched text,
        // which is the structural guarantee behind "a finding never echoes the secret".
        var method = typeof(SecretPattern).GetMethod(nameof(SecretPattern.IsMatch));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
    }

    [Fact]
    public void ShannonEntropy_IsZero_ForARepeatedCharacter()
    {
        Assert.Equal(0, SecretPatterns.ShannonEntropy("aaaaaaaa"), 3);
        Assert.True(SecretPatterns.ShannonEntropy("s3cr3tV4lu3_x9Q2wZ") > 3.0);
    }
}
