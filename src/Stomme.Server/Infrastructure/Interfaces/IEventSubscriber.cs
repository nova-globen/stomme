using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// A reaction to a sync event. Implementations are discovered by assembly scan and awaited by
    /// EventDistributorHostedService, which means the work here completes before the write that raised the
    /// event returns to its caller. Keep it short.
    /// </summary>
    public interface IEventSubscriber
    {
        Task EntityChangedAsync(SyncEvent syncEvent);
    }
}
