using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Common.RecurringTasks;

public interface ISchedule
{
    ScheduledTimer CreateTimer(Func<Task> action, ILogger? logger = null);
}