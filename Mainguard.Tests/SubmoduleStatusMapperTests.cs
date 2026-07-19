using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

// TI-16 (pure): the SubmoduleStatus flag set → SubmoduleState mapping, pinned exactly for each
// representative flag combination, plus the precedence rule and an exhaustive no-throw sweep.
public class SubmoduleStatusMapperTests
{
    // Convenience: the flags a submodule that is present in head/index/config always carries.
    private const SubmoduleStatus Registered =
        SubmoduleStatus.InHead | SubmoduleStatus.InIndex | SubmoduleStatus.InConfig;

    [Fact]
    public void Map_FreshClone_EmptyWorkdir_ShouldBeUninitialized()
    {
        // Recorded everywhere but never checked out: git leaves an empty directory.
        Assert.Equal(SubmoduleState.Uninitialized,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.WorkDirUninitialized));
    }

    [Fact]
    public void Map_NotInWorkDir_ShouldBeUninitialized()
    {
        // In config/head/index but the gitlink is simply absent from the working tree.
        Assert.Equal(SubmoduleState.Uninitialized, SubmoduleStatusMapper.Map(Registered));
    }

    [Fact]
    public void Map_CheckedOutAtRecordedCommit_CleanTree_ShouldBeUpToDate()
    {
        Assert.Equal(SubmoduleState.UpToDate,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir));
    }

    [Fact]
    public void Map_WorkDirAtDifferentCommit_ShouldBeModified()
    {
        // The "+" case: checked out at a commit other than what the superproject records.
        Assert.Equal(SubmoduleState.Modified,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir | SubmoduleStatus.WorkDirModified));
    }

    [Fact]
    public void Map_StagedPointerBump_ShouldBeModified()
    {
        // A staged submodule bump (index gitlink differs from HEAD's) still counts as Modified.
        Assert.Equal(SubmoduleState.Modified,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir | SubmoduleStatus.IndexModified));
    }

    [Fact]
    public void Map_AddedGitlink_ShouldBeModified()
    {
        Assert.Equal(SubmoduleState.Modified,
            SubmoduleStatusMapper.Map(SubmoduleStatus.InConfig | SubmoduleStatus.InWorkDir
                | SubmoduleStatus.IndexAdded | SubmoduleStatus.WorkDirAdded));
    }

    [Theory]
    [InlineData(SubmoduleStatus.WorkDirFilesUntracked)]
    [InlineData(SubmoduleStatus.WorkDirFilesModified)]
    [InlineData(SubmoduleStatus.WorkDirFilesIndexDirty)]
    public void Map_DirtyInnerTree_ShouldBeDirty(SubmoduleStatus dirtyFlag)
    {
        // At the recorded commit but with uncommitted/untracked content inside the submodule.
        Assert.Equal(SubmoduleState.Dirty,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir | dirtyFlag));
    }

    [Fact]
    public void Map_ModifiedAndDirty_ShouldPreferModified()
    {
        // Both a stale recorded pointer AND a dirty inner tree: the superproject-significant
        // pointer change wins (documented precedence).
        Assert.Equal(SubmoduleState.Modified,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir
                | SubmoduleStatus.WorkDirModified | SubmoduleStatus.WorkDirFilesModified));
    }

    [Fact]
    public void Map_UninitializedWins_EvenWithOtherFlags()
    {
        // WorkDirUninitialized dominates: nothing else is meaningful until it's checked out.
        Assert.Equal(SubmoduleState.Uninitialized,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.WorkDirUninitialized
                | SubmoduleStatus.WorkDirModified));
    }

    [Fact]
    public void Map_Unmodified_ShouldBeUninitialized_NotInWorkDir()
    {
        // The all-zero flag set carries no InWorkDir bit, so it maps to Uninitialized.
        Assert.Equal(SubmoduleState.Uninitialized, SubmoduleStatusMapper.Map(SubmoduleStatus.Unmodified));
    }

    [Fact]
    public void SubmoduleStatusMapping_ShouldCoverAllStates()
    {
        // TI-16 consolidated case: one representative flag set per SubmoduleState.
        Assert.Equal(SubmoduleState.Uninitialized,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.WorkDirUninitialized));
        Assert.Equal(SubmoduleState.UpToDate,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir));
        Assert.Equal(SubmoduleState.Modified,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir | SubmoduleStatus.WorkDirModified));
        Assert.Equal(SubmoduleState.Dirty,
            SubmoduleStatusMapper.Map(Registered | SubmoduleStatus.InWorkDir | SubmoduleStatus.WorkDirFilesUntracked));
    }

    [Fact]
    public void Map_ShouldBeTotalAndDeterministic_OverAllFlagCombinations()
    {
        // Every combination of the low bits maps to a defined state without throwing, and the
        // mapping is deterministic (same input → same output).
        for (int bits = 0; bits < (1 << 14); bits++)
        {
            var status = (SubmoduleStatus)bits;
            var first = SubmoduleStatusMapper.Map(status);
            Assert.Contains(first, new[]
            {
                SubmoduleState.Uninitialized, SubmoduleState.UpToDate,
                SubmoduleState.Modified, SubmoduleState.Dirty
            });
            Assert.Equal(first, SubmoduleStatusMapper.Map(status));
        }
    }
}
