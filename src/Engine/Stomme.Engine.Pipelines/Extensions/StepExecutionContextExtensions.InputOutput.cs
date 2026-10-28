using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public static class StepExecutionContextExtensions
{
    extension(StepExecutionContext context)
    {
        public IDictionary<string, object> GetInputs()
        {
            return context.StepState!;
        }

        public IDictionary<string, object> GetOutputs()
        {
            var step = context.Step;
            var expressionExecutionContext = context.ExpressionExecutionContext;
            var stepDescriptor = context.StepDescriptor;
            var outputDescriptors = stepDescriptor.Outputs;

            var outputs = outputDescriptors.ToDictionary(x => x.Name, x =>
            {
                if (x.IsSerializable == false)
                    return "(not serializable)";

                var cachedValue = step.GetOutput(expressionExecutionContext, x.Name);

                if (cachedValue != null)
                    return cachedValue;

                if (x.ValueGetter(step) is Output output && context.TryGet(output.MemoryBlockReference(), out var outputValue))
                    return outputValue!;

                return null!;
            });

            return outputs;
        }
    }
}