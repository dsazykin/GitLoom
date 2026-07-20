using LibGit2Sharp;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

// T-17 (ViewModel, TI-00 pattern): the diff viewer renders a friendly "LFS object (size)" summary
// instead of the raw pointer text when a diffed blob is a Git LFS pointer. No repo/git needed — the
// fake feeds a unified diff whose added side is a pointer; the working file does not exist on disk so
// RawContent is empty and detection falls to the diff body.
public class DiffViewerViewModelLfsTests
{
    private const string PointerAddedDiff =
        "diff --git a/asset.bin b/asset.bin\n" +
        "new file mode 100644\n" +
        "index 0000000..1111111\n" +
        "--- /dev/null\n" +
        "+++ b/asset.bin\n" +
        "@@ -0,0 +1,3 @@\n" +
        "+version https://git-lfs.github.com/spec/v1\n" +
        "+oid sha256:394e150401779536293e71470142d31b9af32750fb50c9c548d63632cf512d40\n" +
        "+size 1048576\n";

    private const string TextDiff =
        "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,3 @@\n a\n-cat\n+dog\n c\n";

    [Fact]
    public void UpdateDiff_LfsPointerAddition_ShouldShowLfsSummary_AndSkipHunks()
    {
        var fake = new FakeGitService { GetFileDiffWhitespaceImpl = (_, _, _, _) => PointerAddedDiff };
        var vm = new DiffViewerViewModel(fake, "/does/not/exist");

        vm.UpdateDiff(new GitFileStatus { FilePath = "asset.bin", State = FileStatus.NewInWorkdir });

        Assert.True(vm.IsLfsDiff);
        Assert.False(vm.IsBinaryDiff);
        Assert.Equal("LFS object (1 MB)", vm.LfsSummary);
        Assert.Empty(vm.Hunks);
        Assert.Empty(vm.DiffLines);
    }

    [Fact]
    public void UpdateDiff_OrdinaryTextDiff_ShouldNotBeLfs()
    {
        var fake = new FakeGitService { GetFileDiffWhitespaceImpl = (_, _, _, _) => TextDiff };
        var vm = new DiffViewerViewModel(fake, "/does/not/exist");

        vm.UpdateDiff(new GitFileStatus { FilePath = "f.txt", State = FileStatus.ModifiedInWorkdir });

        Assert.False(vm.IsLfsDiff);
        Assert.NotEmpty(vm.Hunks);
    }
}
