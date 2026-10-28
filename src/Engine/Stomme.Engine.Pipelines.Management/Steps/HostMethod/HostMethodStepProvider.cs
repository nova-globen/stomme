using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Management.Steps.HostMethod;

/// <summary>
/// Provides steps for each configured host method type registered via <see cref="HostMethodStepsOptions"/>.
/// Public instance methods of the configured types are exposed as steps (as determined by <see cref="IHostMethodStepDescriber"/>).
/// Inputs come from public properties and method parameters.
/// </summary>
[UsedImplicitly]
public class HostMethodStepProvider(IOptions<HostMethodStepsOptions> options, IHostMethodStepDescriber hostMethodStepDescriber) : IStepProvider
{
    public async ValueTask<IEnumerable<StepDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<StepDescriptor>();

        foreach (var kvp in options.Value.StepTypes)
        {
            var key = kvp.Key;
            var type = kvp.Value;
            var methodDescriptors = await hostMethodStepDescriber.DescribeAsync(key, type, cancellationToken).ConfigureAwait(false);
            descriptors.AddRange(methodDescriptors);
        }

        return descriptors;
    }
}
