using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Handlers.Notifications;

public class ValidatePipeline : INotificationHandler<PipelineDefinitionValidating>
{
    public Task HandleAsync(PipelineDefinitionValidating notification, CancellationToken cancellationToken)
    {
        var pipeline = notification.Pipeline;
        var inputs = pipeline.Inputs;
        var outputs = pipeline.Outputs;
        
        ValidateUniqueNames(inputs, "inputs", notification.ValidationErrors);
        ValidateUniqueNames(outputs, "outputs", notification.ValidationErrors);
        
        return Task.CompletedTask;
    }
    
    private void ValidateUniqueNames(IEnumerable<ArgumentDefinition> variables, string variableType, ICollection<PipelineValidationError> validationErrors)
    {
        var duplicateNames = variables.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (duplicateNames.Any())
        {
            var message = $"The following {variableType} are defined more than once: {string.Join(", ", duplicateNames)}";
            validationErrors.Add(new(message));
        }
    }
}