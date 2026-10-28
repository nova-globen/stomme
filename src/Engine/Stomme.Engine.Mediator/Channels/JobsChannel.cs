using Stomme.Engine.Mediator.Abstractions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;

namespace Stomme.Engine.Mediator.Channels;

/// <inheritdoc cref="Stomme.Engine.Mediator.Contracts.IJobsChannel" />
public class JobsChannel : ChannelBase<EnqueuedJob>, IJobsChannel
{
}