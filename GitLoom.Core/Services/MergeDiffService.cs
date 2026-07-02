using System.Collections.Generic;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

public class MergeDiffService : IMergeDiffService
{
    private readonly ISideBySideDiffBuilder _diffBuilder;

    public MergeDiffService()
    {
        _diffBuilder = new SideBySideDiffBuilder(new Differ());
    }

    public List<MergeChunk> GenerateMergeChunks(string baseText, string leftText, string rightText)
    {
        var diffLeft = _diffBuilder.BuildDiffModel(baseText ?? "", leftText ?? "");
        var diffRight = _diffBuilder.BuildDiffModel(baseText ?? "", rightText ?? "");

        var chunks = new List<MergeChunk>();

        // TODO: Implement the full 3-way chunking logic using the DiffPlex models.
        // This involves stepping through the base document and identifying where 
        // left or right (or both) have diverged from the base, and grouping those 
        // changes into MergeChunk objects.

        return chunks;
    }
}
