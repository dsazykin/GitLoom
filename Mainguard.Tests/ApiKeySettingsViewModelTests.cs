using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.UI.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Mainguard.Tests;

/// <summary>
/// TI-P2-01 — the AI Providers page ViewModel and the CLI-OAuth ToS acknowledgment. Validate-then-store:
/// an invalid key is never persisted (keyring dir stays empty), a valid key is stored under
/// <c>llm_&lt;provider&gt;</c>, the candidate key is nulled after the check, delete removes it, and the ToS
/// acknowledgment survives a fresh <see cref="AppDbContext"/>.
/// </summary>
public class ApiKeySettingsViewModelTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-apikey-vm-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static Func<AppDbContext> InMemoryDbFactory()
    {
        // Shared in-memory SQLite: one open connection keeps the schema alive across contexts.
        var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var seed = new AppDbContext(options)) seed.Database.EnsureCreated();
        return () => new AppDbContext(options);
    }

    // #10 (plan §7) / TI #9 — invalid key → nothing persisted, inline error set.
    [Fact]
    public async Task Save_InvalidKey_ShouldNotPersist()
    {
        using var dir = new TempDir();
        var store = (ISecureKeyStore)new SecureKeyring(dir.Path);
        var vm = new ApiKeySettingsViewModel(
            store,
            healthCheck: (_, _, _) => Task.FromResult(new KeyHealth { IsValid = false, FailureReason = "bad key" }));

        vm.SelectedProvider = "anthropic";
        vm.ApiKey = "sk-ant-bad";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(store.Get("llm_anthropic"));
        Assert.Empty(Directory.GetFiles(dir.Path, "llm_*.keyring"));
        Assert.True(vm.IsHealthError);
        Assert.Equal("bad key", vm.HealthMessage);
        Assert.Equal(string.Empty, vm.ApiKey); // candidate nulled after the check
    }

    // #10/#11 (plan §7) / TI #10 — valid key stored, health line updated, candidate nulled; re-save re-checks.
    [Fact]
    public async Task Save_ValidKey_ShouldStore_RecheckHealth_AndNullLocalCopies()
    {
        using var dir = new TempDir();
        var store = (ISecureKeyStore)new SecureKeyring(dir.Path);
        var checkCount = 0;
        var vm = new ApiKeySettingsViewModel(
            store,
            healthCheck: (_, _, _) =>
            {
                Interlocked.Increment(ref checkCount);
                return Task.FromResult(new KeyHealth { IsValid = true, RequestsPerMinute = 1000, EstimatedConcurrentAgents = 12 });
            });

        vm.SelectedProvider = "openai";
        vm.ApiKey = "sk-openai-good";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("sk-openai-good", store.Get("llm_openai"));
        Assert.False(vm.IsHealthError);
        Assert.Contains("12 concurrent agents", vm.HealthMessage);
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.Contains(vm.Providers, r => r.Provider == "openai" && r.HasKey);

        // Re-save over the existing key overwrites atomically and re-checks (edge row 5).
        vm.ApiKey = "sk-openai-rotated";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("sk-openai-rotated", store.Get("llm_openai"));
        Assert.Equal(2, checkCount);
    }

    // Unreachable provider → typed failure surfaced, nothing stored (edge: provider unreachable).
    [Fact]
    public async Task Save_Unreachable_ShouldSurfaceError_AndStoreNothing()
    {
        using var dir = new TempDir();
        var store = (ISecureKeyStore)new SecureKeyring(dir.Path);
        var vm = new ApiKeySettingsViewModel(
            store,
            healthCheck: (_, _, _) => throw new Mainguard.Git.Exceptions.GitOperationException("Could not reach the 'anthropic' API"));

        vm.SelectedProvider = "anthropic";
        vm.ApiKey = "sk-ant-x";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(store.Get("llm_anthropic"));
        Assert.True(vm.IsHealthError);
        Assert.Contains("Could not reach", vm.HealthMessage);
    }

    // Delete removes the stored key and resets the row (edge: delete).
    [Fact]
    public void Delete_ShouldRemoveStoredKey()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("llm_anthropic", "sk-ant-stored");
        var vm = new ApiKeySettingsViewModel((ISecureKeyStore)keyring);

        Assert.Contains(vm.Providers, r => r.Provider == "anthropic" && r.HasKey);
        vm.Providers.First(r => r.Provider == "anthropic").DeleteCommand.Execute(null);

        Assert.Null(keyring.RetrieveSecret("llm_anthropic"));
        Assert.Contains(vm.Providers, r => r.Provider == "anthropic" && !r.HasKey);
    }

    // #11 (plan §7) / TI #11 — ToS acknowledgment persists across a fresh AppDbContext.
    [Fact]
    public void TosAcknowledgment_ShouldPersistAcrossContexts()
    {
        var dbFactory = InMemoryDbFactory();

        var vm = new CliOAuthTosDialogViewModel("anthropic", dbFactory);
        vm.AcknowledgeCommand.Execute(null);
        Assert.True(vm.Acknowledged);

        // Read back through a brand-new context instance.
        using var fresh = dbFactory();
        Assert.True(fresh.HasTosAcknowledgment("anthropic"));
        var row = fresh.TosAcknowledgments.Single(t => t.Provider == "anthropic");
        Assert.True(row.AcknowledgedAt <= DateTimeOffset.UtcNow);
        Assert.False(fresh.HasTosAcknowledgment("openai"));
    }

    // The settings VM skips the dialog when an acknowledgment already exists (idempotent activation).
    [Fact]
    public async Task UseClaudeSubscription_ShouldSkipDialog_WhenAlreadyAcknowledged()
    {
        var dbFactory = InMemoryDbFactory();
        using (var db = dbFactory())
        {
            db.TosAcknowledgments.Add(new TosAcknowledgment { Provider = "anthropic", AcknowledgedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();
        }

        using var dir = new TempDir();
        var vm = new ApiKeySettingsViewModel((ISecureKeyStore)new SecureKeyring(dir.Path), dbFactory: dbFactory);
        var dialogShown = false;
        vm.ShowTosDialogAsync = _ => { dialogShown = true; return Task.FromResult(true); };

        await vm.UseClaudeSubscriptionCommand.ExecuteAsync(null);

        Assert.False(dialogShown);
        Assert.True(vm.IsCliOAuthEnabled);
    }
}
