using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-00 §A.4.1 — the shared daemon in-proc fixture. Hosts <c>Mainguard.Server</c> via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, isolates the session-token file to a
/// temp path, exposes the token, a correct/authenticated channel, a wrong-token channel,
/// and a log-capture sink for the G-13 field-mask assertions. Every daemon in-proc test
/// uses this — hand-rolled hosts are a bug. Call <see cref="StartNew"/> for an
/// independent second host (the daemon-restart / reconnect scenarios).
/// </summary>
public sealed class DaemonFixture : WebApplicationFactory<Program>
{
    private readonly string _tokenPath;
    private readonly CapturingLoggerProvider _logs = new();

    public DaemonFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-daemon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tokenPath = Path.Combine(dir, "daemon.token");
    }

    /// <summary>An independent, freshly-started in-proc daemon (own token + host).</summary>
    public static DaemonFixture StartNew()
    {
        var host = new DaemonFixture();
        _ = host.Services; // force host build
        return host;
    }

    /// <summary>The session token the running host authenticates against.</summary>
    public string Token => Services.GetRequiredService<SessionTokenFile>().Token;

    /// <summary>Formatted log lines captured from the daemon's logging pipeline.</summary>
    public IReadOnlyList<string> CapturedLogs => _logs.Lines;

    /// <summary>A gRPC channel over the in-proc test handler (attach metadata per call).</summary>
    public GrpcChannel CreateChannel()
        => GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>Bearer metadata carrying the correct token (or an override for negatives).</summary>
    public Metadata AuthHeaders(string? token = null)
        => new() { { "authorization", $"bearer {token ?? Token}" } };

    /// <summary>Bearer metadata carrying a wrong token — the "wrong-token channel" factory.</summary>
    public Metadata WrongTokenHeaders()
        => new() { { "authorization", "bearer 0000000000000000000000000000000000000000000000000000000000000000" } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Isolate the on-disk token to a temp path so tests never touch the real
        // ~/.mainguard/daemon.token, and capture the daemon's logs for the mask test.
        builder.UseSetting("Daemon:TokenPath", _tokenPath);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(_logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                var dir = Path.GetDirectoryName(_tokenPath);
                if (dir is not null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }

    /// <summary>A minimal in-memory logger provider — the G-13 field-mask log sink. Each captured line
    /// is prefixed with its <c>[category]</c> so the daemon-logging tests can assert which subsystem a
    /// line belongs to (e.g. <c>[mainguardd.Rpc]</c>); the mask assertions use <c>Contains</c>, so the
    /// prefix is transparent to them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines, categoryName);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _lines;
            private readonly string _category;

            public CapturingLogger(ConcurrentQueue<string> lines, string category)
            {
                _lines = lines;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _lines.Enqueue($"[{_category}] {formatter(state, exception)}");
            }
        }
    }
}
