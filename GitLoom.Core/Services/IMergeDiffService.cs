namespace GitLoom.Core.Services;

using GitLoom.Core.Models;

public interface IMergeDiffService
{
    /// <summary>Splits a 3-way merge into ordered chunks covering the whole document.</summary>
    IReadOnlyList<MergeChunk> GenerateMergeChunks(string? baseText, string? leftText, string? rightText);

    /// <summary>Concatenates chunks per their Kind/Resolution into the merged document.
    /// Throws InvalidOperationException if any Conflict chunk is Unresolved.</summary>
    string AssembleMerged(IEnumerable<MergeChunk> chunks);
}
