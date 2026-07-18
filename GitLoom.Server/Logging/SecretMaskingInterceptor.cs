using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace GitLoom.Server.Logging;

/// <summary>
/// Structured access logging for every RPC under the daemon's <c>Rpc</c> category: method, peer,
/// status, duration. Request and response bodies are rendered ONLY through
/// <see cref="SecretFieldMask.Redact"/>, so a <c>// SECRET</c> field's value/length/prefix never
/// reaches a log sink (G-13). The field-mask test invokes an RPC carrying a sentinel secret and asserts
/// the captured logs contain zero occurrences of it.
///
/// <para>It also records <b>handler faults</b>: a non-<see cref="RpcException"/> thrown out of a handler
/// otherwise reaches the client as a bare <c>Unknown</c> with nothing recorded daemon-side (the class of
/// invisibility the #201 missing-image crash fell into). Each handler catches it, logs an Error with the
/// method, peer, exception type, message, and stack (dev text, low secret risk), then rethrows — gRPC
/// still maps it to <c>Unknown</c> for the client, but it is now diagnosable from rpc.log/journal.</para>
/// </summary>
public sealed class SecretMaskingInterceptor : Interceptor
{
    private readonly ILogger _logger;

    public SecretMaskingInterceptor(ILoggerFactory loggerFactory)
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Rpc);
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var sw = Stopwatch.StartNew();
        LogRequest(context, request as IMessage);
        try
        {
            var response = await continuation(request, context);
            LogCompletion(context, StatusCode.OK, sw, response as IMessage);
            return response;
        }
        catch (RpcException ex)
        {
            LogCompletion(context, ex.StatusCode, sw, response: null);
            throw;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            LogHandlerFault(context, sw, ex);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var sw = Stopwatch.StartNew();
        LogRequest(context, request as IMessage);
        try
        {
            await continuation(request, responseStream, context);
            LogCompletion(context, StatusCode.OK, sw, response: null);
        }
        catch (RpcException ex)
        {
            LogCompletion(context, ex.StatusCode, sw, response: null);
            throw;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            LogHandlerFault(context, sw, ex);
            throw;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var sw = Stopwatch.StartNew();
        LogRequest(context, request: null);
        try
        {
            var response = await continuation(requestStream, context);
            LogCompletion(context, StatusCode.OK, sw, response as IMessage);
            return response;
        }
        catch (RpcException ex)
        {
            LogCompletion(context, ex.StatusCode, sw, response: null);
            throw;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            LogHandlerFault(context, sw, ex);
            throw;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var sw = Stopwatch.StartNew();
        LogRequest(context, request: null);
        try
        {
            await continuation(requestStream, responseStream, context);
            LogCompletion(context, StatusCode.OK, sw, response: null);
        }
        catch (RpcException ex)
        {
            LogCompletion(context, ex.StatusCode, sw, response: null);
            throw;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            LogHandlerFault(context, sw, ex);
            throw;
        }
    }

    private void LogRequest(ServerCallContext context, IMessage? request)
    {
        var body = request is null ? "<stream>" : SecretFieldMask.Redact(request);
        _logger.LogInformation("rpc-begin method={Method} peer={Peer} request={Request}",
            context.Method, context.Peer, body);
    }

    private void LogCompletion(ServerCallContext context, StatusCode status, Stopwatch sw, IMessage? response)
    {
        var body = response is null ? "<none>" : SecretFieldMask.Redact(response);
        _logger.LogInformation(
            "rpc-end method={Method} peer={Peer} status={Status} duration_ms={Duration} response={Response}",
            context.Method, context.Peer, status, sw.ElapsedMilliseconds, body);
    }

    /// <summary>
    /// Records a non-<see cref="RpcException"/> that escaped a handler. No request/response body is
    /// rendered here (only method/peer/type/message/stack), so nothing carrying a <c>// SECRET</c> field
    /// is logged — the message is developer text and the bodies never appear on this path.
    /// </summary>
    private void LogHandlerFault(ServerCallContext context, Stopwatch sw, Exception ex)
    {
        _logger.LogError(ex,
            "rpc-fault method={Method} peer={Peer} type={Type} duration_ms={Duration} message={Message}",
            context.Method, context.Peer, ex.GetType().Name, sw.ElapsedMilliseconds, ex.Message);
    }
}
