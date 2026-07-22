namespace Mainguard.Git.Security;

/// <summary>
/// The minimal secret-store contract the agent platform depends on (P2-01): a keyed
/// get/set/delete over an OS-backed keyring. Deliberately UI-free and EF-free so the
/// Phase-2 daemon (P2-02+) and the P2-24 compliance backends can implement it later
/// against a different backing store without dragging in app types.
///
/// <para><see cref="SecureKeyring"/> is the desktop implementation; its <c>Set</c>/<c>Get</c>/
/// <c>Delete</c> delegate to the existing <c>Save</c>/<c>Retrieve</c>/<c>DeleteSecret</c> so there
/// is exactly one secret-storage code path.</para>
/// </summary>
public interface ISecureKeyStore
{
    void Set(string key, string secret);
    string? Get(string key);
    void Delete(string key);

    /// <summary>The stored key NAMES starting with <paramref name="prefix"/> (never values) —
    /// what lets the P2-01 settings page and the spawn path enumerate the user's custom
    /// <c>llm_env_*</c> entries without a separate index.</summary>
    System.Collections.Generic.IReadOnlyList<string> List(string prefix);
}
