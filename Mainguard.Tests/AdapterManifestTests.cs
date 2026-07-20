using Mainguard.Agents.Agents.Adapters;

namespace Mainguard.Tests;

/// <summary>TI-P2-22 #4: adapter manifest schema corpus — valid, missing probe, unpinned version
/// (<c>@latest</c> refused by schema), unknown fields, bad hash, duplicate id.</summary>
public class AdapterManifestTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Manifest(string body) => $$"""{ "adapters": [ {{body}} ] }""";

    private const string ValidAdapter = $$"""
    {
      "id": "claude-code",
      "displayName": "Claude Code",
      "version": "1.2.3",
      "sha256": "{{Sha}}",
      "installCmd": ["npm", "install", "-g", "@anthropic-ai/claude-code@1.2.3"],
      "configShims": [{ "path": "/home/agent/.claude/settings.json", "content": "{}" }],
      "healthProbe": { "command": ["claude", "--version"], "expectedVersionSubstring": "1.2.3" }
    }
    """;

    [Fact]
    public void Valid_ShouldParse()
    {
        var m = AdapterManifest.Parse(Manifest(ValidAdapter));
        var a = Assert.Single(m.Adapters);
        Assert.Equal("claude-code", a.Id);
        Assert.Equal("1.2.3", a.Version);
        Assert.Equal("1.2.3", a.HealthProbe!.ExpectedVersionSubstring);
        Assert.Single(a.ConfigShims!);
    }

    [Fact]
    public void MissingHealthProbe_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "sha256": "{{Sha}}",
          "installCmd": ["true"] }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.MissingField, ex.Error);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("^1.0.0")]
    [InlineData("*")]
    public void UnpinnedVersion_ShouldBeRejected(string version)
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "{{version}}", "sha256": "{{Sha}}",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.UnpinnedVersion, ex.Error);
    }

    [Fact]
    public void InstallCmdWithAtLatest_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "sha256": "{{Sha}}",
          "installCmd": ["npm", "install", "-g", "claude@latest"],
          "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.UnpinnedVersion, ex.Error);
    }

    [Fact]
    public void UnknownField_ShouldBeRejectedByStrictSchema()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "sha256": "{{Sha}}",
          "installCmd": ["true"], "surpriseField": true,
          "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Fact]
    public void BadHash_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "sha256": "not-a-hash",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.BadHash, ex.Error);
    }

    [Fact]
    public void DuplicateId_ShouldBeRejected()
    {
        var m = Manifest($"{ValidAdapter}, {ValidAdapter}");
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(m));
        Assert.Equal(AdapterManifestError.DuplicateId, ex.Error);
    }

    // ---- Audit fix #13: per-adapter API-key env var --------------------------------------------------

    [Fact]
    public void ApiKeyEnvVar_Valid_ShouldParse()
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}') + @", ""apiKeyEnvVar"": ""OPENAI_API_KEY"" }";
        var m = AdapterManifest.Parse(Manifest(adapter));

        Assert.Equal("OPENAI_API_KEY", Assert.Single(m.Adapters).ApiKeyEnvVar);
    }

    [Fact]
    public void ApiKeyEnvVar_Absent_IsNull_MeaningInteractiveLogin()
    {
        Assert.Null(Assert.Single(AdapterManifest.Parse(Manifest(ValidAdapter)).Adapters).ApiKeyEnvVar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1BAD")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS-DASH")]
    public void ApiKeyEnvVar_Invalid_ShouldBeRejected(string bad)
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}') + $@", ""apiKeyEnvVar"": ""{bad}"" }}";
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(adapter)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }
}
