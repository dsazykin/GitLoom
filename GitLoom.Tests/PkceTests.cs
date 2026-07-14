using GitLoom.Core.Security;

namespace GitLoom.Tests;

/// <summary>TI-P2-22 #1: PKCE verifier/challenge against the RFC 7636 spec vectors + base64url shape.</summary>
public class PkceTests
{
    // RFC 7636 Appendix B: the canonical verifier and its S256 challenge.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Pkce_VerifierChallenge_ShouldMatchRfc7636Vectors()
    {
        Assert.Equal(RfcChallenge, Pkce.ComputeChallenge(RfcVerifier));
    }

    [Fact]
    public void CreatePair_ShouldProduceBase64UrlNoPadding_AndS256()
    {
        var pair = Pkce.CreatePair();

        Assert.Equal("S256", pair.Method);
        Assert.Equal(pair.Challenge, Pkce.ComputeChallenge(pair.Verifier));
        foreach (var s in new[] { pair.Verifier, pair.Challenge })
        {
            Assert.DoesNotContain('=', s);
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
        }
        // 32 random bytes → 43 base64url chars (no padding).
        Assert.Equal(43, pair.Verifier.Length);
    }
}
