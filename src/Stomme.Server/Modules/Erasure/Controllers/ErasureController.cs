#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Models.Erasure;
using Stomme.Modules.Erasure.Domain;
using Stomme.Erasure.Persistence;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Erasure.Controllers
{
    /// <summary>
    /// The erasure receipts of the request's site, for the Host (D112): what an operator shows a supervisory
    /// authority. A receipt holds no personal data, so nothing here can leak one; a failed run can be retried.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    [Authorize(Roles = RoleNames.Host)]
    public sealed class ErasureController : ModuleControllerBase
    {
        private readonly IErasureRequestRepository _requests;
        private readonly IUnitOfWork _unitOfWork;
        private readonly SiteState _siteState;

        public ErasureController(IErasureRequestRepository requests, IUnitOfWork unitOfWork, SiteState siteState, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _requests = requests;
            _unitOfWork = unitOfWork;
            _siteState = siteState;
        }

        [HttpGet("receipts")]
        public async Task<IReadOnlyList<ErasureReceiptView>> Receipts([FromQuery] int take, CancellationToken ct) =>
            (await _requests.ListForSiteAsync(_siteState.Alias.SiteId, take <= 0 ? 100 : take, ct)).Select(ToView).ToList();

        [HttpGet("receipts/{id:guid}")]
        public async Task<IActionResult> Receipt(Guid id, CancellationToken ct)
        {
            var request = await _requests.GetByIdAsync(id, ct);
            return request is null || request.SiteId != _siteState.Alias.SiteId ? NotFound() : Ok(ToView(request));
        }

        /// <summary>A Failed run goes back to Pending and the job tries it again on its next tick.</summary>
        [HttpPost("receipts/{id:guid}/retry")]
        public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
        {
            var request = await _requests.GetByIdAsync(id, ct);
            if (request is null || request.SiteId != _siteState.Alias.SiteId)
            {
                return NotFound();
            }

            request.Retry();
            await _requests.UpdateAsync(request, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Ok(ToView(request));
        }

        internal static ErasureReceiptView ToView(ErasureRequestAggregate request) => new()
        {
            Id = request.Id,
            SubjectErasureId = request.SubjectErasureId,
            SiteId = request.SiteId,
            Status = request.Status.ToString(),
            RequestedOnUtc = request.RequestedOnUtc,
            ErasedOnUtc = request.ErasedOnUtc,
            CompletedOnUtc = request.CompletedOnUtc,
            RetainedUntilUtc = request.RetainedUntilUtc,
            Attempts = request.Attempts,
            LastError = request.LastError,
            Outcomes = request.ReadOutcomes().Select(o => new ErasureOutcomeView
            {
                Participant = o.Participant,
                Store = o.Store,
                Handling = o.Handling.ToString(),
                Affected = o.Affected,
                Reason = o.Reason?.ToString(),
                RetainedUntilUtc = o.RetainedUntilUtc,
                PurgedOnUtc = o.PurgedOnUtc,
            }).ToList(),
        };
    }
}
