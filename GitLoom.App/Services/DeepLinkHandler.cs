using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Mainguard.Git.Security;

namespace GitLoom.App.Services;

/// <summary>
/// The <c>gitloom://</c> deep-link entry point (P2-22 §J-4). Three responsibilities, all delegating the
/// security-critical parsing to the pure <see cref="DeepLinkParser"/> in Core:
/// <list type="bullet">
///   <item>register/unregister the per-user protocol handler (via <see cref="WindowsIntegration"/>);</item>
///   <item>single-instance forwarding — a second launch carrying a URI hands it to the already-running
///   instance over a named pipe (the OAuth loopback flow deliberately avoids the protocol handler, so
///   this path only ever carries non-secret navigation links);</item>
///   <item>dispatch a parsed, non-secret command to the app. A link that carries a secret-shaped
///   parameter is refused by the parser and never dispatched (invariant 1).</item>
/// </list>
/// </summary>
public sealed class DeepLinkHandler
{
    /// <summary>The per-user named pipe the running instance listens on for forwarded deep links.</summary>
    public const string PipeName = "GitLoom.DeepLink.v1";

    private readonly IRegistryCommandRunner _registry;
    private readonly Action<DeepLinkCommand> _dispatch;

    public DeepLinkHandler(Action<DeepLinkCommand> dispatch, IRegistryCommandRunner? registry = null)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _registry = registry ?? new RegExeRegistryCommandRunner();
    }

    /// <summary>Registers the per-user <c>gitloom://</c> protocol handler pointing at this executable.</summary>
    public async Task RegisterProtocolAsync(string exePath, CancellationToken ct = default)
    {
        foreach (var cmd in WindowsIntegration.InstallCommands(exePath))
            await _registry.RunAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Parses an incoming URI and dispatches it if it is a valid, non-secret command. Returns
    /// the outcome so the caller can log ignored/rejected links. Never throws on bad input.</summary>
    public DeepLinkResult Handle(string uri)
    {
        var result = DeepLinkParser.Parse(uri);
        if (result is { Outcome: DeepLinkOutcome.Command, Command: { } command })
            _dispatch(command);
        return result;
    }

    /// <summary>
    /// Attempts to hand <paramref name="uri"/> to an already-running instance. Returns true if a running
    /// instance accepted it (this process should then exit); false if none is listening (this process is
    /// the primary and should handle the URI itself). The URI is validated before being sent, so a
    /// malformed/secret link is never even forwarded.
    /// </summary>
    public static async Task<bool> TryForwardToRunningInstanceAsync(string uri, int connectTimeoutMs = 300, CancellationToken ct = default)
    {
        if (DeepLinkParser.Parse(uri).Outcome == DeepLinkOutcome.Rejected)
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(uri.AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false; // no running instance — we are the primary
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the single-instance listener loop: accept a forwarded URI, parse+dispatch, repeat. The
    /// primary instance owns this loop for its lifetime; cancel the token on shutdown.
    /// </summary>
    public async Task ListenForForwardedLinksAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var uri = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(uri))
                    Handle(uri);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A broken pipe connection must not kill the listener — loop and re-listen.
            }
        }
    }
}
