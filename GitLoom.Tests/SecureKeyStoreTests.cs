using System;
using System.IO;
using Mainguard.Git.Security;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-P2-01 — the <see cref="ISecureKeyStore"/> extraction round-trips an <c>llm_*</c> key through
/// <see cref="SecureKeyring"/>'s new interface members and removes the backing file on delete.
/// </summary>
public class SecureKeyStoreTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-keystore-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    // #9 (plan §7) / TI #1 — Set/Get/Delete via the new interface round-trips and removes the file.
    [Fact]
    public void Set_Get_Delete_ShouldRoundTrip_ThroughISecureKeyStore()
    {
        using var dir = new TempDir();
        ISecureKeyStore store = new SecureKeyring(dir.Path);

        store.Set("llm_anthropic", "sk-ant-secret");
        Assert.Equal("sk-ant-secret", store.Get("llm_anthropic"));

        var backingFile = Path.Combine(dir.Path, "llm_anthropic.keyring");
        Assert.True(File.Exists(backingFile));

        store.Delete("llm_anthropic");
        Assert.Null(store.Get("llm_anthropic"));
        Assert.False(File.Exists(backingFile));
    }

    // The interface members delegate onto the same storage path the legacy Save/Retrieve/DeleteSecret use.
    [Fact]
    public void ISecureKeyStore_And_Legacy_ShareOneBackingStore()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("llm_openai", "sk-openai-secret");

        Assert.Equal("sk-openai-secret", ((ISecureKeyStore)keyring).Get("llm_openai"));

        ((ISecureKeyStore)keyring).Delete("llm_openai");
        Assert.Null(keyring.RetrieveSecret("llm_openai"));
    }
}
