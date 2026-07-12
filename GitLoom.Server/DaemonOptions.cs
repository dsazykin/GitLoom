namespace GitLoom.Server;

/// <summary>
/// Parsed daemon launch options. Transport-agnostic and loopback-only by construction
/// (there is no "bind address" knob — the host always binds 127.0.0.1, invariant 2).
/// </summary>
public sealed record DaemonOptions
{
    /// <summary>The default loopback port for the local-dev / CI daemon.</summary>
    public const int DefaultPort = 5250;

    /// <summary>Loopback TCP port to bind.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Run directly on Windows/localhost (no WSL) for the dev loop and CI. Linux-only
    /// subsystems (forkpty, cgroups) are guarded behind capability checks and simply
    /// absent here — they arrive in later tasks.
    /// </summary>
    public bool LocalDev { get; init; }

    /// <summary>
    /// Start, self-probe over the loopback gRPC endpoint, and exit 0 on success — the
    /// CI <c>--local-dev</c> smoke. Prints nothing on success.
    /// </summary>
    public bool Smoke { get; init; }

    /// <summary>Override the session-token file path (test isolation). Null → OS default.</summary>
    public string? TokenPath { get; init; }

    public static DaemonOptions Parse(string[] args)
    {
        var options = new DaemonOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-dev":
                    options = options with { LocalDev = true };
                    break;
                case "--smoke":
                    options = options with { Smoke = true };
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options = options with { Port = port };
                    i++;
                    break;
            }
        }

        return options;
    }
}
