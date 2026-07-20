using Mainguard.Git.Review;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-11 test 7 (TestDelta_ParserFixtures): TRX/xUnit + pass/fail-fallback parsing, and the branch-vs-main
/// delta (new failures / new passes). Pure — the strip renders this, never recomputes it.
/// </summary>
public class TestDeltaParserTests
{
    private const string BaselineTrx = """
    <?xml version="1.0" encoding="UTF-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>
        <UnitTestResult testName="A.Test1" outcome="Passed" />
        <UnitTestResult testName="A.Test2" outcome="Passed" />
        <UnitTestResult testName="A.Test3" outcome="Failed" />
      </Results>
    </TestRun>
    """;

    private const string BranchTrx = """
    <?xml version="1.0" encoding="UTF-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>
        <UnitTestResult testName="A.Test1" outcome="Passed" />
        <UnitTestResult testName="A.Test2" outcome="Failed" />
        <UnitTestResult testName="A.Test3" outcome="Passed" />
        <UnitTestResult testName="A.Test4" outcome="Passed" />
      </Results>
    </TestRun>
    """;

    [Fact]
    public void Trx_Delta_ReportsNewFailuresAndNewPasses()
    {
        var baseline = TestDeltaParser.ParseTrx(BaselineTrx);
        var branch = TestDeltaParser.ParseTrx(BranchTrx);
        var delta = TestDeltaParser.Compute(branch, baseline);

        Assert.Contains("A.Test2", delta.NewFailures);   // was passing, now failing
        Assert.Contains("A.Test3", delta.NewPasses);      // was failing, now passing
        Assert.Equal(4, delta.TotalCurrent);
        Assert.Equal(3, delta.PassedCurrent);
        Assert.Equal(1, delta.FailedCurrent);
    }

    [Fact]
    public void NewTestFailing_CountsAsNewFailure()
    {
        var delta = TestDeltaParser.Compute(
            TestDeltaParser.ParsePassFail("A.New FAIL"),
            TestDeltaParser.ParsePassFail("A.Old PASS"));
        Assert.Contains("A.New", delta.NewFailures);
    }

    [Theory]
    [InlineData("A.Test PASS")]
    [InlineData("PASS A.Test")]
    [InlineData("A.Test: PASSED")]
    public void PassFail_TolerantOfOrderAndVerbs(string line)
    {
        var outcomes = TestDeltaParser.ParsePassFail(line);
        Assert.Single(outcomes);
        Assert.True(outcomes[0].Passed);
        Assert.Equal("A.Test", outcomes[0].Name);
    }

    [Fact]
    public void Malformed_Trx_ReturnsEmpty_NeverThrows()
    {
        Assert.Empty(TestDeltaParser.ParseTrx("<not-xml"));
        Assert.Empty(TestDeltaParser.ParseTrx(""));
    }
}
