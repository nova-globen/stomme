#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Erasure.Persistence;
using Stomme.Models.Erasure;
using Stomme.Modules.Erasure.Controllers;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Erasure.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IErasureService"/>, for the receipts page rendered on the server (BL-191). The framework
    /// registers <c>IClientService</c> only in the WebAssembly client's Program, so on a site whose Runtime is Server - the
    /// default - the page had nothing to resolve and showed "Missing service(s): IErasureService" instead of its receipts.
    /// It makes the controller's Host decision itself, because a page resolving it in-process never passes through
    /// <c>[Authorize]</c>, and a denial THROWS: an empty list would say "nothing was erased", which is a different statement
    /// from "this is not yours". Then the same reads and the same retry as the controller - never an HTTP call to its own host.
    /// </summary>
    public sealed class ServerErasureService(IErasureRequestRepository requests, IUnitOfWork unitOfWork, SiteState siteState, ICurrentUser currentUser) : IErasureService
    {
        public async Task<List<ErasureReceiptView>> GetReceiptsAsync(int take)
        {
            var site = RequireHostSite();
            return (await requests.ListForSiteAsync(site, take <= 0 ? 100 : take)).Select(ErasureController.ToView).ToList();
        }

        public async Task<ErasureReceiptView> RetryAsync(Guid id)
        {
            var site = RequireHostSite();
            var request = await requests.GetByIdAsync(id);
            if (request is null || request.SiteId != site)
            {
                throw new KeyNotFoundException("There is no erasure receipt with that id on this site.");
            }

            request.Retry();
            await requests.UpdateAsync(request);
            await unitOfWork.SaveChangesAsync();
            return ErasureController.ToView(request);
        }

        private int RequireHostSite()
        {
            if (!currentUser.IsAuthenticated || !currentUser.Roles.Contains(RoleNames.Host))
            {
                throw new UnauthorizedAccessException("Erasure receipts are the Host's.");
            }

            return siteState.Alias?.SiteId is > 0 and var site
                ? site
                : throw new InvalidOperationException("The erasure receipts are read for a site, and none is resolved.");
        }
    }
}
