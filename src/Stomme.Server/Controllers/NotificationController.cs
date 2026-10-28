using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using System.Net;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class NotificationController : Controller
    {
        private readonly INotificationRepository _notifications;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public NotificationController(INotificationRepository notifications, IUserPermissions userPermissions, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _notifications = notifications;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>/read?siteid=x&direction=to&userid=1&count=5&isread=false
        [HttpGet("read")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<IEnumerable<Notification>> Get(string siteid, string direction, string userid, string count, string isread)
        {
            IEnumerable<Notification> notifications = null;

            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            int UserId;
            int Count;
            bool IsRead;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId && int.TryParse(userid, out UserId) && int.TryParse(count, out Count) && bool.TryParse(isread, out IsRead) && await IsAuthorizedAsync(UserId))
            {
                if (direction == "to")
                {
                    notifications = await _notifications.GetNotificationsAsync(SiteId, -1, UserId, Count, IsRead);
                }
                else
                {
                    notifications = await _notifications.GetNotificationsAsync(SiteId, UserId, -1, Count, IsRead);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Get Attempt {SiteId} {Direction} {UserId} {Count} {isRead}", siteid, direction, userid, count, isread);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                notifications = null;
            }


            return notifications;
        }

        // GET: api/<controller>/read?siteid=x&direction=to&userid=1&count=5&isread=false
        [HttpGet("read-count")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<int> Get(string siteid, string direction, string userid, string isread)
        {
            int notificationsCount = 0;

            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            int UserId;
            bool IsRead;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId && int.TryParse(userid, out UserId) && bool.TryParse(isread, out IsRead) && await IsAuthorizedAsync(UserId))
            {
                if (direction == "to")
                {
                    notificationsCount = await _notifications.GetNotificationCountAsync(SiteId, -1, UserId, IsRead);
                }
                else
                {
                    notificationsCount = await _notifications.GetNotificationCountAsync(SiteId, UserId, -1, IsRead);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Get Attempt {SiteId} {Direction} {UserId} {isRead}", siteid, direction, userid, isread);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                notificationsCount = 0;
            }


            return notificationsCount;
        }


        // GET: api/<controller>?siteid=x&type=y&userid=z
        [HttpGet]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<IEnumerable<Notification>> Get(string siteid, string direction, string userid)
        {
            IEnumerable<Notification> notifications = null;

            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            int UserId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId && int.TryParse(userid, out UserId) && await IsAuthorizedAsync(UserId))
            {
                if (direction == "to")
                {
                    notifications = await _notifications.GetNotificationsAsync(SiteId, -1, UserId);
                }
                else
                {
                    notifications = await _notifications.GetNotificationsAsync(SiteId, UserId, -1);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Get Attempt {SiteId} {Direction} {UserId}", siteid, direction, userid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                notifications = null;
            }

            return notifications;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Notification> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            Notification notification = await _notifications.GetNotificationAsync(id);
            if (notification != null && notification.SiteId == alias.SiteId && (await IsAuthorizedAsync(notification.FromUserId) || await IsAuthorizedAsync(notification.ToUserId)))
            {
                return notification;
            }
            else
            {
                if (notification != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Get Attempt {NotificationId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Notification> Post([FromBody] Notification notification)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && notification.SiteId == alias.SiteId && (await IsAuthorizedAsync(notification.FromUserId) || (notification.FromUserId == null && User.IsInRole(RoleNames.Admin))))
            {
                if (!User.IsInRole(RoleNames.Admin))
                {
                    // content must be HTML encoded for non-admins to prevent HTML injection
                    notification.Subject = WebUtility.HtmlEncode(notification.Subject);
                    notification.Body = WebUtility.HtmlEncode(notification.Body);
                }
                notification = await _notifications.AddNotificationAsync(notification);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Notification, notification.NotificationId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Notification Added {NotificationId}", notification.NotificationId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Post Attempt {Notification}", notification);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                notification = null;
            }
            return notification;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Notification> Put(int id, [FromBody] Notification notification)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && notification.SiteId == alias.SiteId && notification.NotificationId == id && await _notifications.GetNotificationAsync(notification.NotificationId, false) != null)
            {
                bool update = false;
                if (await IsAuthorizedAsync(notification.FromUserId))
                {
                    // notification belongs to current authenticated user - update is allowed
                    if (!User.IsInRole(RoleNames.Admin))
                    {
                        // content must be HTML encoded for non-admins to prevent HTML injection
                        notification.Subject = WebUtility.HtmlEncode(notification.Subject);
                        notification.Body = WebUtility.HtmlEncode(notification.Body);
                    }
                    update = true;
                }
                else
                {
                    if (await IsAuthorizedAsync(notification.ToUserId))
                    {
                        // notification was sent to current authenticated user - only isread and isdeleted properties can be updated
                        var isread = notification.IsRead;
                        var isdeleted = notification.IsDeleted;
                        notification = await _notifications.GetNotificationAsync(notification.NotificationId);
                        notification.IsRead = isread;
                        notification.IsDeleted = isdeleted;
                        update = true;
                    }
                }
                if (update)
                {
                    notification = await _notifications.UpdateNotificationAsync(notification);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Notification, notification.NotificationId, SyncEventActions.Update);
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "Notification Updated {NotificationId}", notification.NotificationId);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Put Attempt {Notification}", notification);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    notification = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Put Attempt {Notification}", notification);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                notification = null;
            }
            return notification;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            Notification notification = await _notifications.GetNotificationAsync(id);
            if (notification != null && notification.SiteId == alias.SiteId && (await IsAuthorizedAsync(notification.FromUserId) || await IsAuthorizedAsync(notification.ToUserId)))
            {
                await _notifications.DeleteNotificationAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Notification, notification.NotificationId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Notification Deleted {NotificationId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Notification Delete Attempt {NotificationId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        private async Task<bool> IsAuthorizedAsync(int? userid)
        {
            bool authorized = false;
            if (userid != null)
            {
                authorized = ((await _userPermissions.GetUserAsync(User)).UserId == userid);
            }
            return authorized;
        }

    }
}
