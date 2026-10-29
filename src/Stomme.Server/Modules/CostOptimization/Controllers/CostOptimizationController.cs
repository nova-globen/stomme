#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.CostOptimization;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.CostOptimization.Controllers
{
    /// <summary>
    /// <c>api/CostOptimization</c> (the predecessor platform's <c>CostOptimizationApi</c>): the site's recommendations under
    /// <c>cost-optimization:view</c>; an advisor pass, apply and dismiss under <c>cost-optimization:manage</c>. A decision the
    /// recommendation refuses answers with its code - 404 for <c>not-found</c>, 409 for <c>not-open</c>, 400 for
    /// <c>invalid-cooldown</c> - and a blank action or reason is a validation problem.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class CostOptimizationController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public CostOptimizationController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/CostOptimization?status=Open&category=Caching
        [HttpGet]
        [RequiresPermissionKey(CostOptimizationPermissions.View)]
        public async Task<IActionResult> List([FromQuery] RecommendationStatus? status, [FromQuery] RecommendationCategory? category, CancellationToken ct) =>
            Ok(await _mediator.Send(new ListRecommendationsQuery(status, category), ct));

        // POST api/CostOptimization/run - one advisor pass for the site, now
        [HttpPost("run")]
        [RequiresPermissionKey(CostOptimizationPermissions.Manage)]
        public async Task<IActionResult> Run(CancellationToken ct) => Ok(await _mediator.Send(new RaiseRecommendationsCommand(), ct));

        // POST api/CostOptimization/{id}/apply
        [HttpPost("{id:guid}/apply")]
        [RequiresPermissionKey(CostOptimizationPermissions.Manage)]
        public async Task<IActionResult> Apply(Guid id, [FromBody] ApplyRecommendationRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                return Decision(await _mediator.Send(new ApplyRecommendationCommand(id, request.ActionTaken), ct));
            }
            catch (ValidationException validation)
            {
                return Invalid(validation);
            }
        }

        // POST api/CostOptimization/{id}/dismiss
        [HttpPost("{id:guid}/dismiss")]
        [RequiresPermissionKey(CostOptimizationPermissions.Manage)]
        public async Task<IActionResult> Dismiss(Guid id, [FromBody] DismissRecommendationRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest();
            }

            try
            {
                return Decision(await _mediator.Send(new DismissRecommendationCommand(id, request.Reason, request.CooldownDays), ct));
            }
            catch (ValidationException validation)
            {
                return Invalid(validation);
            }
        }

        private IActionResult Decision(RecommendationDecisionView decision) => decision switch
        {
            { Success: true } => Ok(decision),
            { Code: RecommendationDecisionView.NotFound } => NotFound(decision),
            { Code: RecommendationDecisionView.NotOpen } => Conflict(decision),
            _ => BadRequest(decision),
        };

        private IActionResult Invalid(ValidationException validation)
        {
            foreach (var failure in validation.Errors)
            {
                ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
            }

            return ValidationProblem(ModelState);
        }
    }
}
