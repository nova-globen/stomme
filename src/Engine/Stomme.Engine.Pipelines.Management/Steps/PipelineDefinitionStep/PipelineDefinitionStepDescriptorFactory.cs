using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;
using Humanizer;

namespace Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;

public class PipelineDefinitionStepDescriptorFactory
{
    public StepDescriptor CreateDescriptor(PipelineDefinition definition, PipelineDefinition? latestPublishedDefinition = null)
    {
        var typeName = definition.Name!.Pascalize();
        var tenantId = definition.TenantId;
        
        var ports = definition.Outcomes.Select(outcome => new Port
        {
            Name = outcome,
            DisplayName = outcome,
            IsBrowsable = true,
            Type = PortType.Flow
        }).ToList();

        var rootPort = new Port
        {
            Name = nameof(PipelineDefinitionStep.Root),
            DisplayName = "Root",
            IsBrowsable = false,
            Type = PortType.Embedded
        };

        ports.Insert(0, rootPort);

        return new()
        {
            TenantId = tenantId,
            TypeName = typeName,
            ClrType = typeof(PipelineDefinitionStep),
            Name = typeName,
            Version = definition.Version,
            DisplayName = definition.Name,
            Description = definition.Description,
            Category = definition.Options.StepCategory ?? "Pipelines",
            Kind = StepKind.Action,
            IsBrowsable = definition.IsPublished,
            Inputs = DescribeInputs(definition).ToList(),
            Outputs = DescribeOutputs(definition).ToList(),
            Ports = ports,
            CustomProperties =
            {
                ["RootType"] = nameof(PipelineDefinitionStep),
                ["PipelineDefinitionId"] = definition.DefinitionId,
                ["PipelineDefinitionVersionId"] = definition.Id
            },
            ConstructionProperties = new Dictionary<string, object>
            {
                [nameof(PipelineDefinitionStep.PipelineDefinitionId)] = definition.DefinitionId,
                [nameof(PipelineDefinitionStep.PipelineDefinitionVersionId)] = definition.Id,
                [nameof(PipelineDefinitionStep.Version)] = definition.Version,
            },
            Constructor = context =>
            {
                var stepResult = context.CreateStep<PipelineDefinitionStep>();
                var step = stepResult.Step;
                step.Type = typeName;
                step.PipelineDefinitionId = definition.DefinitionId;
                step.PipelineDefinitionVersionId = definition.Id;
                step.Version = definition.Version;
                step.LatestAvailablePublishedVersion = latestPublishedDefinition?.Version ?? definition.Version;
                step.LatestAvailablePublishedVersionId = latestPublishedDefinition?.Id ?? definition.Id;

                return stepResult;
            }
        };
    }
    
    private static IEnumerable<InputDescriptor> DescribeInputs(PipelineDefinition definition)
    {
        var inputs = definition.Inputs.Select(inputDefinition =>
        {
            var nakedType = inputDefinition.Type;
            var inputName = inputDefinition.Name;
            var safeInputName = PropertyNameHelper.GetSafePropertyName(typeof(PipelineDefinitionStep), inputName);

            return new InputDescriptor
            {
                Type = nakedType,
                IsWrapped = true,
                ValueGetter = step => step.SyntheticProperties.GetValueOrDefault(safeInputName),
                ValueSetter = (step, value) => step.SyntheticProperties[safeInputName] = value!,
                Name = safeInputName,
                DisplayName = inputDefinition.DisplayName,
                Description = inputDefinition.Description,
                Category = inputDefinition.Category,
                UIHint = inputDefinition.UIHint,
                StorageDriverType = inputDefinition.StorageDriverType,
                IsSynthetic = true
            };
        });

        foreach (var input in inputs)
            yield return input;
    }

    private static IEnumerable<OutputDescriptor> DescribeOutputs(PipelineDefinition definition)
    {
        return definition.Outputs.Select(outputDefinition =>
        {
            var nakedType = outputDefinition.Type;
            var outputName = outputDefinition.Name;
            var safeOutputName = PropertyNameHelper.GetSafePropertyName(typeof(PipelineDefinitionStep), outputName);

            return new OutputDescriptor
            {
                Type = nakedType,
                ValueGetter = step => step.SyntheticProperties.GetValueOrDefault(safeOutputName),
                ValueSetter = (step, value) => step.SyntheticProperties[safeOutputName] = value!,
                Name = safeOutputName,
                DisplayName = outputDefinition.DisplayName,
                Description = outputDefinition.Description,
                IsSynthetic = true
            };
        });
    }
}