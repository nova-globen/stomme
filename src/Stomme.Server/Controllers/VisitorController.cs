using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Repository;
using System.Net;
using System;
using System.Globalization;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class VisitorController : Controller
    {
        private readonly IVisitorRepository _visitors;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;
        private readonly Stomme.Infrastructure.Security.VisitorCookie _visitorCookie;

        public VisitorController(IVisitorRepository visitors, ILogManager logger, ITenantManager tenantManager, Stomme.Infrastructure.Security.VisitorCookie visitorCookie)
        {
            _visitors = visitors;
            _logger = logger;
            _tenantManager = tenantManager;
            _visitorCookie = visitorCookie;
        }

        // GET: api/<controller>?siteid=x&fromdate=y
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<Visitor>> Get(string siteid, string fromdate)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                return await _visitors.GetVisitorsAsync(SiteId, DateTime.ParseExact(fromdate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Visitor Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<Visitor> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            bool authorized = User.IsInRole(RoleNames.Admin);
            if (!authorized)
            {
                // Only a visitor cookie this host issued proves the id: a plain one of the caller's own making
                // read every visitor's IP address, user agent and pages by id.
                authorized = (id == _visitorCookie.VisitorIdOf(Request, alias.SiteId));
            }

            var visitor = await _visitors.GetVisitorAsync(id);
            if (authorized && visitor != null && visitor.SiteId == alias.SiteId)
            {
                return visitor;
            }
            else
            {
                if (visitor != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Visitor Get Attempt {VisitorId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }
    }
}
