using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using NovaGloben.Module.Blogs.Domain;

namespace NovaGloben.Module.Blogs.Persistence
{
    /// <summary>
    /// The Blogs module's closed aggregate repository — the clean-arch <c>IRepository&lt;BlogPost, Guid&gt;</c>
    /// plus one module-specific read (<see cref="GetByModuleAsync"/>). It is PROVIDER-NEUTRAL by construction:
    /// the module registers no store, no DbContext and no provider branch, and this contract is closed over the
    /// generic <c>IRepository</c> the host binds to the install's provider.
    /// <para>
    /// It lives OUTSIDE the <c>Stomme.Modules.*</c> namespace (the framework's reflective seam-store parity
    /// gate discovers interfaces under that namespace and demands a RavenDB twin); this module rides the
    /// storage-blind clean-arch stack, so it needs no twin and correctly sits outside that gate. It is public
    /// because the CQRS handlers that inject it are public.
    /// </para>
    /// </summary>
    public interface IBlogPostRepository : IRepository<BlogPost, Guid>
    {
        /// <summary>
        /// Every post for one module instance (blog), newest first. The <c>ModuleId</c> filter runs in the
        /// store via <see cref="IRepository{TAggregate, TId}.FindAsync"/> on an int equality — a SAFE
        /// provider-neutral predicate (equality on a non-string scalar translates identically on EF, RavenDB
        /// and the file store; it does not touch the string case-sensitivity divergences).
        /// </summary>
        Task<IReadOnlyList<BlogPost>> GetByModuleAsync(int moduleId, CancellationToken ct = default);
    }
}
