using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

// G-4: git's stderr reaches typed exception messages and UI error panels, and git
// echoes remote URLs verbatim ("fatal: unable to access '<url>'"). When a user has
// embedded credentials in a remote URL, the userinfo must be masked before stderr
// leaves the runner.
public class GitServiceStderrRedactionTests
{
    [Theory]
    [InlineData(
        "fatal: unable to access 'https://user:ghp_secret123@github.com/o/r.git/': The requested URL returned error: 403",
        "fatal: unable to access 'https://***@github.com/o/r.git/': The requested URL returned error: 403")]
    [InlineData(
        "fatal: unable to access 'https://ghp_tokenonly@github.com/o/r.git/'",
        "fatal: unable to access 'https://***@github.com/o/r.git/'")]
    [InlineData(
        "error: failed to push some refs to 'ssh://git@github.com/o/r.git'",
        "error: failed to push some refs to 'ssh://***@github.com/o/r.git'")]
    public void RedactUrlCredentials_MasksUserinfo(string input, string expected)
        => Assert.Equal(expected, GitService.RedactUrlCredentials(input));

    [Theory]
    [InlineData("fatal: not a git repository")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host")]
    [InlineData("")]
    public void RedactUrlCredentials_LeavesCredentialFreeTextAlone(string input)
        => Assert.Equal(input, GitService.RedactUrlCredentials(input));

    [Fact]
    public void RedactUrlCredentials_MasksEveryOccurrence()
    {
        var input = "push to https://a:b@h1.com/x failed; pull from http://c:d@h2.com/y failed";
        var expected = "push to https://***@h1.com/x failed; pull from http://***@h2.com/y failed";
        Assert.Equal(expected, GitService.RedactUrlCredentials(input));
    }
}
