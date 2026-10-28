using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

/// <summary>
/// Provides extensions to <see cref="IPipelineExecutionPipelineBuilder"/> that adds support for installing <see cref="IPipelineExecutionMiddleware"/> components.
/// </summary>
public static class PipelineExecutionMiddlewareExtensions
{
    extension(IPipelineExecutionPipelineBuilder pipelineBuilder)
    {
        /// <summary>
        /// Installs the specified middleware component into the pipeline being built.
        /// </summary>
        public IPipelineExecutionPipelineBuilder UseMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(params object[] args) where TMiddleware : IPipelineExecutionMiddleware
        {
            var delegateFactory = CreateMiddlewareDelegateFactory<TMiddleware>(pipelineBuilder, args);
            return pipelineBuilder.Use(delegateFactory);
        }

        /// <summary>
        /// Installs the specified middleware component into the pipeline being built.
        /// </summary>
        public IPipelineExecutionPipelineBuilder Insert<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(int index, params object[] args) where TMiddleware : IPipelineExecutionMiddleware
        {
            var delegateFactory = CreateMiddlewareDelegateFactory<TMiddleware>(pipelineBuilder, args);
            return pipelineBuilder.Insert(index, delegateFactory);
        }

        /// <summary>
        /// Replaces the terminal middleware component with the specified middleware component.
        /// </summary>
        public IPipelineExecutionPipelineBuilder ReplaceTerminal<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(params object[] args) where TMiddleware : IPipelineExecutionMiddleware
        {
            var index = pipelineBuilder.Components.Count() - 1;
            return pipelineBuilder.Replace<TMiddleware>(index, args);
        }

        /// <summary>
        /// Replaces the middleware component at the specified index with the specified middleware component.
        /// </summary>
        public IPipelineExecutionPipelineBuilder Replace<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(int index, params object[] args) where TMiddleware : IPipelineExecutionMiddleware
        {
            var delegateFactory = CreateMiddlewareDelegateFactory<TMiddleware>(pipelineBuilder, args);
            return pipelineBuilder.Replace(index, delegateFactory);
        }

        /// <summary>
        /// Creates a middleware delegate for the specified middleware component.
        /// </summary>
        public Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> CreateMiddlewareDelegateFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(params object[] args) where TMiddleware : IPipelineExecutionMiddleware
        {
            var middleware = typeof(TMiddleware);

            return next =>
            {
                var invokeMethod = MiddlewareHelpers.GetInvokeMethod(middleware);
                var ctorParams = new[]
                {
                    next
                }.Concat(args).ToArray();
                var instance = ActivatorUtilities.CreateInstance(pipelineBuilder.ServiceProvider, middleware, ctorParams);
                return (PipelineMiddlewareDelegate)invokeMethod.CreateDelegate(typeof(PipelineMiddlewareDelegate), instance);
            };
        }
    }
}