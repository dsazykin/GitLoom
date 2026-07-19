using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Security;

/// <summary>
/// The real <see cref="ILoopbackCallbackChannel"/>: an <see cref="HttpListener"/> bound to an ephemeral
/// port on <c>127.0.0.1</c> (RFC 8252 §7.3 — loopback interface, OS-assigned port, no admin needed).
/// The FIRST hit on <c>/callback</c> is captured and answered with a "you can close this tab" page;
/// every later hit is answered <c>410 Gone</c> (single-use), so a replayed redirect cannot re-drive the
/// flow. Disposing releases the port immediately.
/// </summary>
public sealed class HttpListenerCallbackChannel : ILoopbackCallbackChannel
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _firstCallback =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private int _consumed;

    public HttpListenerCallbackChannel()
    {
        _port = FreeLoopbackPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string RedirectUri => $"http://127.0.0.1:{_port}/callback";

    public Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        // Complete-or-cancel: a cancelled wait (timeout/user cancel) never leaves the caller hanging.
        return _firstCallback.Task.WaitAsync(ct);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                // Listener stopped/disposed — normal shutdown.
                break;
            }

            try
            {
                if (Interlocked.Exchange(ref _consumed, 1) == 0
                    && ctx.Request.Url is { } url
                    && url.AbsolutePath.TrimEnd('/').EndsWith("/callback", StringComparison.Ordinal))
                {
                    var query = ParseQuery(url.Query);
                    Respond(ctx, 200, SuccessPage);
                    _firstCallback.TrySetResult(query);
                }
                else
                {
                    // Single-use: any subsequent hit (a replay, a refresh) is Gone.
                    Respond(ctx, 410, "This authentication link has already been used.");
                }
            }
            catch
            {
                // A broken client connection must never take the loop down.
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static void Respond(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static int FreeLoopbackPort()
    {
        // Grab an OS-assigned free port, then hand it to HttpListener. A tiny race window exists but is
        // acceptable for an interactive one-shot auth; the alternative (HttpListener :0) is not supported.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _firstCallback.TrySetCanceled();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }
        _lifetime.Dispose();
    }

    private const string SuccessPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Mainguard</title></head>" +
        "<body style=\"font-family:system-ui;text-align:center;margin-top:4rem\">" +
        "<h2>You're signed in to Mainguard.</h2><p>You can close this tab and return to the app.</p>" +
        "</body></html>";
}
