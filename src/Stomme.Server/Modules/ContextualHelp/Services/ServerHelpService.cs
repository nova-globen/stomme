#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Modules.ContextualHelp.Startup;

namespace Stomme.Modules.ContextualHelp.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IHelpService"/>: a mediator dispatch, so a control rendered on the server goes
    /// through exactly the same <c>[Requires]</c> decision an HTTP caller does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered UNCONDITIONALLY, because the module DEFINITION is discovered on every install and an operator can place
    /// its control on a page by hand: that control's <c>@inject</c> must resolve, or the page cannot render. While
    /// <c>Help:Enabled</c> is off it answers "nothing" rather than dispatching - the handlers depend on services that are
    /// only composed while the feature is on (the MCP module's pattern).
    /// </para>
    /// <para>
    /// Refusals are answered the way the HTTP client answers them (null, empty, <see cref="Guid.Empty"/>), so a control
    /// behaves the same whichever side of the wire it runs on.
    /// </para>
    /// </remarks>
    public sealed class ServerHelpService : IHelpService
    {
        private readonly IMediator _mediator;
        private readonly IServiceProvider _services;

        public ServerHelpService(IMediator mediator, IServiceProvider services)
        {
            _mediator = mediator;
            _services = services;
        }

        private bool Enabled => HelpSettings.Enabled(HostConfiguration.Resolve(_services));

        public async Task<HelpTopicView> GetTopicAsync(string anchor, string? locale = null) =>
            Enabled ? (await _mediator.Send(new ResolveHelpTopicQuery(anchor, locale)))! : null!;

        public async Task<List<HelpTopicSummary>> SearchAsync(string query) =>
            Enabled ? (await _mediator.Send(new ListPublishedHelpTopicsQuery(query))).ToList() : new List<HelpTopicSummary>();

        public async Task<List<HelpTopicSummary>> GetTopicsAsync() =>
            Enabled ? (await _mediator.Send(new ListAllHelpTopicsQuery())).ToList() : new List<HelpTopicSummary>();

        public async Task<List<HelpConflictView>> GetConflictsAsync() =>
            Enabled ? (await _mediator.Send(new ListHelpContributionConflictsQuery())).ToList() : new List<HelpConflictView>();

        public async Task<HelpTopicEditView> GetTopicForEditAsync(Guid id) =>
            Enabled ? (await _mediator.Send(new GetHelpTopicForEditQuery(id)))! : null!;

        public async Task<Guid> SaveTopicAsync(HelpTopicSaveInput input)
        {
            if (!Enabled || input is null)
            {
                return Guid.Empty;
            }

            try
            {
                return await _mediator.Send(new SaveHelpTopicCommand(input.Id, input.Anchor ?? string.Empty, input.Locale ?? string.Empty,
                    input.Title ?? string.Empty, input.Markdown ?? string.Empty));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Guid.Empty;   // the HTTP client's answer to the controller's 400/404
            }
        }

        public async Task SetPublishedAsync(Guid id, bool published)
        {
            if (Enabled)
            {
                await Refused(() => _mediator.Send(new PublishHelpTopicCommand(id, published)));
            }
        }

        public async Task<string> PreviewAsync(string markdown)
        {
            if (!Enabled)
            {
                return null!;
            }

            try
            {
                return await _mediator.Send(new PreviewHelpMarkdownQuery(markdown));
            }
            catch (ArgumentException)
            {
                return null!;
            }
        }

        public async Task DeleteTopicAsync(Guid id)
        {
            if (Enabled)
            {
                await Refused(() => _mediator.Send(new DeleteHelpTopicCommand(id)));
            }
        }

        public async Task ResetTopicAsync(Guid id)
        {
            if (Enabled)
            {
                await Refused(() => _mediator.Send(new ResetHelpTopicToSeedCommand(id)));
            }
        }

        /// <summary>A command the domain refused ends quietly, as the HTTP client's 400/404 does. Authorization failures are NOT swallowed.</summary>
        private static async Task Refused(Func<Task<Unit>> send)
        {
            try
            {
                await send();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
