using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Pipelines.StepExecution;

public static class StepExecutionMiddlewareExtensions
{
    extension(IStepExecutionPipelineBuilder pipelineBuilder)
    {
        public IStepExecutionPipelineBuilder UseMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(params object[] args) where TMiddleware : IStepExecutionMiddleware
        {
            var delegateFactory = CreateMiddlewareDelegateFactory<TMiddleware>(pipelineBuilder, args);
            return pipelineBuilder.Use(delegateFactory);
        }

        public IStepExecutionPipelineBuilder Insert<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TMiddleware>(int index, params object[] args) where TMiddleware : IStepExecutionMiddleware
        {
            var delegateFactory = CreateMiddlewareDelegateFactory<TMiddleware>(pipelineBuilder, args);
            return pipelineBuilder.Insert(index, delegateFactory);
        }

        /// <summary>
        /// Creates a middleware delegate for the specified middleware component.
        /// </summary>
        public Func<StepMiddlewareDelegate, StepMiddlewareDelegate> CreateMiddlewareDelegateFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(params object[] args) where TMiddleware : IStepExecutionMiddleware
        {
            var middleware = typeof(TMiddleware);

            return next =>
            {
                var invokeMethod = MiddlewareHelpers.GetInvokeMethod(middleware);
                var ctorArgs = new[]
                {
                    next
                }.Concat(args).ToArray();
                var instance = ActivatorUtilities.CreateInstance(pipelineBuilder.ServiceProvider, middleware, ctorArgs);
                return (StepMiddlewareDelegate)invokeMethod.CreateDelegate(typeof(StepMiddlewareDelegate), instance);
            };
        }
    }
}