using System;
using System.IO;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents;

/// <summary>
/// The engine-agnostic live terminal session behind an agent's CLI: a bidirectional byte stream
/// (read = child output, write = keystrokes toward the child), resize, kill, and the child's exit.
/// <see cref="PtySession"/> is the real implementation (ConPTY/forkpty via Porta.Pty); tests supply
/// duplex-pipe fakes so the daemon's PTY-binding and attach plumbing is verifiable cross-platform
/// without a real PTY or a Docker jail.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Bidirectional stream: read = child output, write = input toward the child.</summary>
    Stream IO { get; }

    /// <summary>Completes with the child's exit code when the process exits.</summary>
    Task<int> ExitCode { get; }

    /// <summary>Propagates a new terminal size to the child (SIGWINCH).</summary>
    void Resize(int cols, int rows);

    /// <summary>Force-terminates the child. Safe to call repeatedly.</summary>
    void Kill();
}
