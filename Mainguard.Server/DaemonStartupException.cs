using System;

namespace Mainguard.Server;

/// <summary>
/// Typed startup failure. Notably raised when the loopback port is already bound —
/// the message names the port (edge row 3) instead of surfacing a raw socket error.
/// </summary>
public sealed class DaemonStartupException : Exception
{
    public int Port { get; }

    public DaemonStartupException(int port, string message, Exception? inner = null)
        : base(message, inner)
    {
        Port = port;
    }
}
