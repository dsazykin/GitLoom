namespace GitLoom.Core.Models;

public enum ChunkType
{
    Unchanged,
    AddedInLeft,
    AddedInRight,
    DeletedInLeft,
    DeletedInRight,
    Conflict
}

public class MergeChunk
{
    public ChunkType Type { get; set; }

    // 1-based line numbers. EndLine is inclusive.
    public int LeftStartLine { get; set; }
    public int LeftEndLine { get; set; }

    public int MiddleStartLine { get; set; }
    public int MiddleEndLine { get; set; }

    public int RightStartLine { get; set; }
    public int RightEndLine { get; set; }

    public string LeftText { get; set; } = string.Empty;
    public string MiddleText { get; set; } = string.Empty;
    public string RightText { get; set; } = string.Empty;

    // Status in the UI (true if user clicked accept/ignore)
    public bool IsResolved { get; set; }
}
