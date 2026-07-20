namespace Mainguard.Git.Models;

public class SideBySideDiffRow
{
    public GitDiffLine LeftLine { get; set; } = new();
    public GitDiffLine RightLine { get; set; } = new();
}
