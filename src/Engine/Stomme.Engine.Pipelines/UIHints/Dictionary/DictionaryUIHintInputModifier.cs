using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.UIHints.Dictionary;

public class DictionaryUIHintInputModifier : IStepDescriptorModifier
{
    public void Modify(StepDescriptor descriptor)
    {
        var dictionaryInputs = descriptor.Inputs.Where(x => x.UIHint == InputUIHints.Dictionary).ToList();
        
        foreach (var dictionaryInput in dictionaryInputs)
            dictionaryInput.EvaluatorType = typeof(DictionaryValueEvaluator);
    }
}