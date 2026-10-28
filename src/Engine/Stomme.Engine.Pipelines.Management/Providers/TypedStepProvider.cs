using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Management.Providers;

/// <summary>
/// Provides step descriptors based on a list of step types registered in the <see cref="ManagementOptions"/>.
/// </summary>
[UsedImplicitly]
public class TypedStepProvider : IStepProvider
{
    private readonly IStepDescriber _stepDescriber;
    private readonly ManagementOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypedStepProvider"/> class.
    /// </summary>
    public TypedStepProvider(IOptions<ManagementOptions> options, IStepDescriber stepDescriber)
    {
        _stepDescriber = stepDescriber;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var stepTypes = _options.StepTypes;
        var descriptors = await DescribeStepTypesAsync(stepTypes, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
        return descriptors;
    }

    private async IAsyncEnumerable<StepDescriptor> DescribeStepTypesAsync(IEnumerable<Type> stepTypes, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var stepType in stepTypes)
        {
            var descriptor = await _stepDescriber.DescribeStepAsync(stepType, cancellationToken).ConfigureAwait(false);
            yield return descriptor;
        }
    }
}