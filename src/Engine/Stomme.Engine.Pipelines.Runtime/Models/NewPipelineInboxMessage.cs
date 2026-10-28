using Stomme.Engine.Pipelines.Helpers;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// A message that can be delivered to a pipeline instance.
/// </summary>
[Obsolete("This class will be removed in a future version.")]
public class NewPipelineInboxMessage
{
    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The type name of the step to deliver the message to.
    /// </summary>
    public string StepTypeName { get; set; } = default!;

    /// <summary>
    /// An optional bookmark payload that can be used to filter the pipeline instances to deliver the message to.
    /// </summary>
    public object BookmarkPayload { get; set; } = default!;

    /// <summary>
    /// An optional pipeline instance ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// An optional step instance ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// An optional correlation ID to select the pipeline instance to deliver the message to.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// An optional set of inputs to deliver to the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// The duration after which the message expires.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = DefaultTimeToLive;

    /// <summary>
    /// Creates a new instance of the <see cref="NewPipelineInboxMessage"/> class.
    /// </summary>
    /// <param name="bookmarkPayload">The bookmark payload to use to filter the pipeline instances to deliver the message to.</param>
    /// <param name="pipelineInstanceId">An optional pipeline instance ID to select the pipeline instance to deliver the message to.</param>
    /// <param name="correlationId">An optional correlation ID to select the pipeline instance to deliver the message to.</param>
    /// <param name="stepInstanceId">An optional step instance ID to select the pipeline instance to deliver the message to.</param>
    /// <param name="input">An optional set of inputs to deliver to the pipeline instance.</param>
    /// <param name="timeToLive">The duration after which the message expires.</param>
    /// <typeparam name="T">The type of the step to deliver the message to.</typeparam>
    /// <returns>A new instance of the <see cref="NewPipelineInboxMessage"/> class.</returns>
    public static NewPipelineInboxMessage For<T>(
        object bookmarkPayload,
        string? pipelineInstanceId = default,
        string? correlationId = default,
        string? stepInstanceId = default,
        IDictionary<string, object>? input = default,
        TimeSpan? timeToLive = default) where T : IStep
    {
        return new NewPipelineInboxMessage
        {
            Input = input,
            BookmarkPayload = bookmarkPayload,
            CorrelationId = correlationId,
            PipelineInstanceId = pipelineInstanceId,
            StepInstanceId = stepInstanceId,
            StepTypeName = StepTypeNameHelper.GenerateTypeName<T>(),
            TimeToLive = timeToLive ?? DefaultTimeToLive,
        };
    }
}