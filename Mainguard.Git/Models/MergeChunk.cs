namespace Mainguard.Git.Models;

public enum ChunkKind { Unchanged, LeftOnly, RightOnly, Conflict }
public enum ChunkResolution { Unresolved, TakeLeft, TakeRight, TakeBoth, Custom }

public sealed class MergeChunk
{
    public ChunkKind Kind { get; init; }
    public ChunkResolution Resolution { get; set; } = ChunkResolution.Unresolved;
    public string BaseText { get; init; } = "";   // this chunk's slice of the base
    public string LeftText { get; init; } = "";   // this chunk's slice of "ours"
    public string RightText { get; init; } = "";   // this chunk's slice of "theirs"
    public string? CustomText { get; set; }        // used when Resolution == Custom
}
