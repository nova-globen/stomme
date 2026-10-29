using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Stomme.EventBus;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Shared;

namespace Stomme.Controllers
{
    /// <summary>
    /// The dead-letter queue admin surface: status, list, retry, discard over the wired
    /// <see cref="IDeadLetterQueue"/> port. Host-only throughout: dead letters carry raw message
    /// payload details, and retrying/discarding is installation-wide bus surgery.
    /// </summary>
    /// <remarks>
    /// The bus is opt-in; with <c>Messaging:Enabled</c> false no <see cref="IDeadLetterQueue"/> is
    /// registered at all, so the port arrives as an enumerable and <see cref="GetStatus"/> answers the
    /// disabled shape instead of erroring — the OpenIddict status pattern. Retry/discard take the id in
    /// the BODY, not the route: transport ids contain slashes (e.g. <c>RavenTransportMessages/…</c>).
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public class DeadLetterController : Controller
    {
        private readonly IDeadLetterQueue _queue;
        private readonly bool _messagingEnabled;
        private readonly ILogManager _logger;

        // IConfigurationRoot, NOT IConfiguration — the host registers the fully-merged configuration
        // (override file included) as IConfigurationRoot; a plain IConfiguration resolves WITHOUT the
        // override, which made this controller answer "messaging disabled" on a bus-enabled install
        // (observed driving; the AdminSiteTemplate/SiteRepository comments record the same trap).
        public DeadLetterController(IEnumerable<IDeadLetterQueue> queues, IConfigurationRoot configuration, ILogManager logger)
        {
            _queue = queues.FirstOrDefault();
            _messagingEnabled = configuration.GetValue<bool>("Messaging:Enabled");
            _logger = logger;
        }

        private bool HasRealQueue => _queue != null && _queue.GetType().Name != "NullDeadLetterQueue";

        // GET: api/deadletter/status
        [HttpGet("status")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Models.DeadLetterStatus> GetStatus()
        {
            return new Models.DeadLetterStatus
            {
                MessagingEnabled = _messagingEnabled,
                HasDeadLetterQueue = HasRealQueue,
                Backend = _queue?.GetType().Name ?? "",
                Count = HasRealQueue ? await _queue.CountAsync() : 0
            };
        }

        // GET: api/deadletter?max=100
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<Models.DeadLetterItem>> Get(int max = 100)
        {
            if (!HasRealQueue)
            {
                return new List<Models.DeadLetterItem>();
            }

            var messages = await _queue.ListAsync(max);
            return messages.Select(message => new Models.DeadLetterItem
            {
                Id = message.Id,
                MessageType = message.MessageType,
                ErrorDetails = message.ErrorDetails,
                EnqueuedUtc = message.EnqueuedUtc,
                Headers = message.Headers.ToDictionary(header => header.Key, header => header.Value)
            });
        }

        // POST: api/deadletter/retry
        [HttpPost("retry")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> Retry([FromBody] DeadLetterActionRequest request)
        {
            if (!HasRealQueue || string.IsNullOrWhiteSpace(request?.Id))
            {
                return NotFound();
            }

            var retried = await _queue.RetryAsync(request.Id);
            if (retried)
            {
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Dead-Letter Message {Id} Returned To The Input Queue", request.Id);
                return Ok();
            }
            return NotFound();
        }

        // POST: api/deadletter/discard
        [HttpPost("discard")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> Discard([FromBody] DeadLetterActionRequest request)
        {
            if (!HasRealQueue || string.IsNullOrWhiteSpace(request?.Id))
            {
                return NotFound();
            }

            var discarded = await _queue.DiscardAsync(request.Id);
            if (discarded)
            {
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Dead-Letter Message {Id} Discarded", request.Id);
                return Ok();
            }
            return NotFound();
        }
    }

    /// <summary>Body for retry/discard — the id travels in the body because transport ids contain slashes.</summary>
    public class DeadLetterActionRequest
    {
        public string Id { get; set; }
    }
}
