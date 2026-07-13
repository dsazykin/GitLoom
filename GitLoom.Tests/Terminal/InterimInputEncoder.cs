using System.Text;

namespace GitLoom.Tests.Terminal;

/// <summary>
/// Reflects the interim P2-03 control's <b>input</b> path for the two input-side coverage areas.
///
/// <para>The interim <c>TerminalControl</c> maps keystrokes to bytes (see its <c>MapKey</c>) but has
/// no bracketed-paste framing and no pointer/mouse handler at all: <c>OnTextInput</c> forwards raw
/// UTF-8 regardless of DECSET ?2004, and there is no <c>OnPointerPressed</c> encoder. This type
/// encodes that reality honestly — so the coverage matrix measures the real gap rather than a
/// contrived stub, and both areas land on the shrink-only allowlist until a real encoder ships.</para>
/// </summary>
public static class InterimInputEncoder
{
    /// <summary>
    /// The interim path forwards paste text verbatim — it never wraps it in ESC[200~ … ESC[201~,
    /// even when bracketed paste is active. Returns the raw bytes (which will not match the wrapped
    /// conformant encoding), documenting the missing framing.
    /// </summary>
    public static byte[] EncodePaste(string text, bool bracketedPasteActive)
    {
        // bracketedPasteActive is ignored on purpose: the interim engine has no 200~/201~ framing.
        _ = bracketedPasteActive;
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// The interim engine has no mouse encoder — there is no code path from a pointer event to a
    /// mouse-report sequence. Returns null to signal "unsupported".
    /// </summary>
    public static byte[]? EncodeMouseClick(int button, int col, int row, bool sgr)
    {
        _ = button;
        _ = col;
        _ = row;
        _ = sgr;
        return null;
    }
}
