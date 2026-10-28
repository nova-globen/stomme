using System.Linq.Expressions;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Provides extension methods for <see cref="IStepDescriber"/>.
/// </summary>
[PublicAPI]
public static class StepDescriberExtensions
{
    /// <param name="stepDescriber">The step describer.</param>
    extension(IStepDescriber stepDescriber)
    {
        /// <summary>
        /// Describes an output property.
        /// </summary>
        /// <param name="expression">The property expression.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="TStep">The type of the step.</typeparam>
        /// <typeparam name="TProperty">The type of the property.</typeparam>
        /// <returns>The output descriptor.</returns>
        public async Task<OutputDescriptor> DescribeOutputProperty<TStep, TProperty>(Expression<Func<TStep, TProperty>> expression, CancellationToken cancellationToken = default)
        {
            var propertyInfo = expression.GetProperty()!;
            return await stepDescriber.DescribeOutputPropertyAsync(propertyInfo, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Describes an input property.
        /// </summary>
        /// <param name="expression">The property expression.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="TStep">The type of the step.</typeparam>
        /// <typeparam name="TProperty">The type of the property.</typeparam>
        /// <returns>The input descriptor.</returns>
        public async Task<InputDescriptor> DescribeInputPropertyAsync<TStep, TProperty>(Expression<Func<TStep, TProperty>> expression, CancellationToken cancellationToken = default)
        {
            var propertyInfo = expression.GetProperty()!;
            return await stepDescriber.DescribeInputPropertyAsync(propertyInfo, cancellationToken).ConfigureAwait(false);
        }
    }
}