using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// An event that is published when a step execution log record is deleted.
/// </summary>
/// <param name="Record">The step execution record.</param>
public record StepExecutionRecordDeleted(StepExecutionRecord Record) : INotification;