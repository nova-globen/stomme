using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Guestbook.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Guestbook.Persistence
{
    /// <summary>
    /// The module's closed aggregate repository — the clean-arch <c>IRepository&lt;GuestbookEntryAggregate,
    /// Guid&gt;</c> plus one module-specific read (<see cref="GetByModuleAsync"/>).
    /// <para>
    /// It lives in <c>Stomme.Guestbook.Persistence</c>, DELIBERATELY OUTSIDE the <c>Stomme.Modules.*</c>
    /// namespace, and that is load-bearing: <c>ModuleStoreRegistrationTests</c> discovers every public
    /// interface <b>under <c>Stomme.Modules.*</c></b> that closes <c>IRepository&lt;,&gt;</c> and demands a
    /// RavenDB twin for it (the D42/D55 reflective SEAM-store parity gate). This sample rides the clean-arch
    /// EF stack (like FeatureManagement), NOT the framework's document seam, so it has no Raven twin by design;
    /// placing the contract outside that namespace keeps it correctly outside that gate rather than tripping
    /// it. It must be public because the CQRS handlers that inject it are public (the mediator dispatches to
    /// handlers by dynamic invoke, which requires them to be accessible to <c>Stomme.Mediator</c>).
    /// </para>
    /// </summary>
    public interface IGuestbookRepository : IRepository<GuestbookEntryAggregate, System.Guid>
    {
        /// <summary>
        /// Every signature for one module instance, newest first. The <c>ModuleId</c> filter runs in the
        /// database, not in memory — the "keep the filter in the database" rule (docs/decisions.md D21).
        /// </summary>
        Task<IReadOnlyList<GuestbookEntryAggregate>> GetByModuleAsync(int moduleId, CancellationToken ct = default);
    }
}
