using Mainguard.Git.Security;
using Xunit;

namespace GitLoom.Tests;

// Host-provided link fields (html_url etc.) are external data handed to the OS shell;
// only absolute http/https URIs may launch (a file:/UNC/custom-scheme "URL" would
// start an arbitrary local program through the default handler).
public class SafeWebUrlTests
{
    [Theory]
    [InlineData("https://github.com/o/r/pull/1")]
    [InlineData("http://localhost:8080/callback")]
    [InlineData("HTTPS://GITHUB.COM/x")]
    public void Allows_AbsoluteHttpAndHttps(string url)
        => Assert.True(SafeWebUrl.IsHttpOrHttps(url));

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://extension/malicious")]
    [InlineData("ftp://host/file")]
    [InlineData("C:/Users/x/evil.bat")]
    [InlineData("github.com/o/r")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_NonWebOrRelative(string? url)
        => Assert.False(SafeWebUrl.IsHttpOrHttps(url));
}
