using GitLoom.Core.Models;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-15 (pure): the %G? code table and the batched log parser. No signing environment needed —
// these run everywhere, unlike the gpg-gated integration cases.
public class SignatureStatusParserTests
{
    [Theory]
    [InlineData('G', SignatureStatus.Good)]
    [InlineData('U', SignatureStatus.UnknownValidity)]
    [InlineData('B', SignatureStatus.Bad)]
    [InlineData('X', SignatureStatus.Expired)]
    [InlineData('Y', SignatureStatus.ExpiredKey)]
    [InlineData('R', SignatureStatus.Revoked)]
    [InlineData('E', SignatureStatus.CannotCheck)]
    [InlineData('N', SignatureStatus.None)]
    [InlineData(' ', SignatureStatus.None)] // unexpected input degrades to None, never throws
    public void FromCode_ShouldMapEveryGitCode(char code, SignatureStatus expected)
        => Assert.Equal(expected, SignatureStatusParser.FromCode(code));

    [Fact]
    public void ParseLog_ShouldMapAllCodes_KeyedBySha()
    {
        // One canned row per status the UI cares about (G/B/U/N/E), signer last.
        var output = string.Join('\n', new[]
        {
            "aaaaaaa|G|Ada Lovelace <ada@example.com>",
            "bbbbbbb|B|Mallory <m@evil.example>",
            "ccccccc|U|Someone <s@example.com>",
            "ddddddd|E|",
            "eeeeeee|N|",
        });

        var map = SignatureStatusParser.ParseLog(output);

        Assert.Equal(SignatureStatus.Good, map["aaaaaaa"].Status);
        Assert.Equal("Ada Lovelace <ada@example.com>", map["aaaaaaa"].Signer);
        Assert.Equal(SignatureStatus.Bad, map["bbbbbbb"].Status);
        Assert.Equal(SignatureStatus.UnknownValidity, map["ccccccc"].Status);
        Assert.Equal(SignatureStatus.CannotCheck, map["ddddddd"].Status);
        Assert.Equal(SignatureStatus.None, map["eeeeeee"].Status);
        Assert.True(map["aaaaaaa"].IsVerified);
        Assert.False(map["eeeeeee"].IsSigned);
    }

    [Fact]
    public void ParseLog_ShouldPreserveSignerContainingSeparator()
    {
        // %GS is last and may contain the '|' separator; the 3-way split must keep it intact.
        var map = SignatureStatusParser.ParseLog("abc123|G|Weird | Name <w@example.com>");
        Assert.Equal("Weird | Name <w@example.com>", map["abc123"].Signer);
        Assert.Equal(SignatureStatus.Good, map["abc123"].Status);
    }

    [Fact]
    public void ParseLog_ShouldTolerateCrlfAndBlankLines()
    {
        var map = SignatureStatusParser.ParseLog("\r\nabc|G|x\r\n\r\n");
        Assert.Single(map);
        Assert.Equal(SignatureStatus.Good, map["abc"].Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseLog_ShouldReturnEmpty_ForNullOrEmpty(string? input)
        => Assert.Empty(SignatureStatusParser.ParseLog(input));
}
