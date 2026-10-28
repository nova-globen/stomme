using System.Text.Json;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public record StepStateFilterContext(StepExecutionContext StepExecutionContext, InputDescriptor InputDescriptor, JsonElement Value, CancellationToken CancellationToken);