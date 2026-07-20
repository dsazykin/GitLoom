using System;
using System.Security.Cryptography;
using System.Text;

namespace Mainguard.Git.Security;

/// <summary>
/// A PKCE (RFC 7636) verifier/challenge pair. <see cref="Verifier"/> is the high-entropy secret kept
/// in memory only; <see cref="Challenge"/> is the S256 transform sent in the authorize request. The
/// verifier is NEVER logged and NEVER placed in any URL we construct (only the challenge travels in the
/// authorize request; the verifier travels only in the HTTPS token-exchange body).
/// </summary>
public sealed record PkcePair(string Verifier, string Challenge)
{
    /// <summary>Only S256 is issued — the plain method is refused for loopback desktop flows.</summary>
    public string Method => "S256";
}

/// <summary>
/// Pure PKCE primitives (RFC 7636, S256). Separated from <see cref="LoopbackOAuthListener"/> so the
/// spec test vectors run with no sockets. All outputs are base64url with no padding, per §4.1/§4.2.
/// </summary>
public static class Pkce
{
    // RFC 7636 §4.1: verifier = 43–128 chars of unreserved set. 32 random bytes → 43 base64url chars.
    private const int VerifierByteLength = 32;

    /// <summary>Generates a fresh verifier (32 CSPRNG bytes, base64url) + its S256 challenge.</summary>
    public static PkcePair CreatePair()
    {
        var bytes = RandomNumberGenerator.GetBytes(VerifierByteLength);
        var verifier = Base64Url(bytes);
        return new PkcePair(verifier, ComputeChallenge(verifier));
    }

    /// <summary>
    /// S256 challenge = base64url(SHA-256(ASCII(verifier))), no padding. This is the exact transform
    /// the RFC 7636 Appendix B vector exercises.
    /// </summary>
    public static string ComputeChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>base64url per RFC 4648 §5 with padding stripped (RFC 7636 §A / §B).</summary>
    public static string Base64Url(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
