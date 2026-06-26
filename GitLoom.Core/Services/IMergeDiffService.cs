using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

public interface IMergeDiffService
{
    /// <summary>
    /// Generates a unified list of merge chunks by comparing the base text against left and right versions.
    /// </summary>
    /// <param name="baseText">The common ancestor text.</param>
    /// <param name="leftText">The incoming (theirs) text.</param>
    /// <param name="rightText">The current (ours) text.</param>
    /// <returns>A list of chunks representing the 3-way differences.</returns>
    List<MergeChunk> GenerateMergeChunks(string baseText, string leftText, string rightText);
}
