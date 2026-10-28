using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds extension methods to <see cref="IStepRegistry"/>.
/// </summary>
public static class StepRegistryExtensions
{
    /// <summary>
    /// Finds the step descriptor for the specified step.
    /// </summary>
    public static Task<StepDescriptor?> FindAsync(this IStepRegistryLookupService stepRegistry, IStep step) => stepRegistry.FindAsync(step.Type, step.Version);

    /// <param name="stepRegistry">The step registry.</param>
    extension(IStepRegistry stepRegistry)
    {
        /// <summary>
        /// Finds the step descriptor for the specified step type.
        /// </summary>
        public StepDescriptor? Find<T>() where T:IStep => stepRegistry.Find(StepTypeNameHelper.GenerateTypeName<T>());

        /// <summary>
        /// Registers the specified step type with the registry.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The type of the step to register.</typeparam>
        public async Task RegisterAsync<T>(CancellationToken cancellationToken = default) where T : IStep => 
            await stepRegistry.RegisterAsync(typeof(T), cancellationToken).ConfigureAwait(false);
    }
}