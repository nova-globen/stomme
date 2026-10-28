using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Stomme.Transport.Grpc
{
    /// <summary>
    /// The Stomme gRPC-Web boundary interceptor (brief 04 / D10): translates the domain failures a service
    /// method (or the code it calls) throws into gRPC status codes — the gRPC equivalent of the framework's
    /// REST 401/403/400 responses. Registered once via <c>AddGrpc</c>, so every module service inherits it and
    /// stays thin. A service that wants a specific code for a specific case still throws
    /// <see cref="RpcException"/> directly (e.g. a per-method permission check); this interceptor only catches
    /// the common, otherwise-opaque failures.
    /// </summary>
    /// <remarks>
    /// Adopted from the predecessor platform's <c>GrpcExceptionInterceptor</c> and adapted to Stomme, which has no
    /// FluentValidation layer: the mapping keys off BCL exception types the framework already raises
    /// (<see cref="UnauthorizedAccessException"/> for a denied permission, <see cref="ArgumentException"/> for
    /// bad input, and so on) rather than a bespoke exception hierarchy.
    /// </remarks>
    public sealed class GrpcExceptionInterceptor : Interceptor
    {
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            try
            {
                return await continuation(request, context);
            }
            catch (RpcException)
            {
                // Already a status the service chose deliberately — let it flow through untouched.
                throw;
            }
            catch (Exception ex)
            {
                throw Map(ex);
            }
        }

        public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            ServerCallContext context,
            ClientStreamingServerMethod<TRequest, TResponse> continuation)
        {
            try
            {
                return await continuation(requestStream, context);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Map(ex);
            }
        }

        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            try
            {
                await continuation(request, responseStream, context);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Map(ex);
            }
        }

        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            try
            {
                await continuation(requestStream, responseStream, context);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Map(ex);
            }
        }

        private static RpcException Map(Exception ex) => ex switch
        {
            // A denied permission (the framework's 403) — the gRPC twin of the REST access-denied response.
            UnauthorizedAccessException => new RpcException(new Status(StatusCode.PermissionDenied, ex.Message)),
            // Bad input (the framework's 400) — including ArgumentNullException / ArgumentOutOfRangeException.
            ArgumentException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
            // A missing entity (the framework's 404).
            KeyNotFoundException => new RpcException(new Status(StatusCode.NotFound, ex.Message)),
            // A broken invariant/precondition — surfaced actionably rather than as an opaque Internal.
            InvalidOperationException => new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)),
            OperationCanceledException => new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled.")),
            _ => new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred.")),
        };
    }
}
