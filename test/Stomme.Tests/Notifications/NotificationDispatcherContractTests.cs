using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Shared;
using Stomme.Tests.Fixtures.DispatchTestModule;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// <c>NotificationDispatcherContractTests</c>: a dependent referencing ONLY the
    /// Shared port dispatches and reaches a fake channel; the four producer shapes dispatch; parameters survive
    /// persistence to render time; recipients - not callers - are constrained (an external e-mail only with the site's
    /// opt-in, an Endpoint never, a user only of THIS site). Also carries 8 dispatch facts (the File store + a fake transport
    /// with a capturing provider; the dispatch job's pass for the mediator command and the bus consumer).
    /// </summary>
    public sealed class NotificationDispatcherContractTests
    {
        private const string TemplateKey = "test.dispatch";
        private const string WebhookProviderKey = "capture-webhook";
        private const string InAppProviderKey = "capture-inapp";
        private const int UserId = 7;

        // ---- the stub dependent references only the Shared port ------------------------------------------------

        [Fact]
        public void The_stub_dependent_references_only_the_shared_port_reflectively_and_in_source()
        {
            Assert.Empty(ForbiddenSignatureReferences(typeof(DispatchTestProducers)));
            Assert.Empty(ForbiddenSourceReferences(Directory.GetFiles(FixtureDirectory(), "*.cs").Select(File.ReadAllText)));
        }

        [Fact]
        public void The_dependency_scanners_flag_a_planted_module_reference_in_both_directions()
        {
            // Known-dirty: a producer holding the module's implementation instead of the port.
            var flagged = ForbiddenSignatureReferences(typeof(PlantedModuleCoupledProducer));
            Assert.Contains(flagged, entry => entry.Contains(nameof(NotificationDispatcher), StringComparison.Ordinal));
            Assert.NotEmpty(ForbiddenSourceReferences(["using Stomme.Modules.Notifications.Application;\nclass P { NotificationDispatcher d; }"]));
            Assert.NotEmpty(ForbiddenSourceReferences(["class P { Stomme.Notifications.Persistence.INotificationRequestRepository r; }"]));

            // Known-clean: the port alone.
            Assert.Empty(ForbiddenSignatureReferences(typeof(PlantedPortOnlyProducer)));
            Assert.Empty(ForbiddenSourceReferences(["using Stomme.Notifications;\nclass P { INotificationDispatcher d; }"]));
        }

        [Fact]
        public async Task The_stub_dependent_dispatches_and_reaches_a_fake_channel()
        {
            var inApp = new FakeTransportChannel(ChannelKind.InApp, InAppProviderKey);
            await using var harness = await StartAsync(inApp);
            await harness.AddAsync(GenericTemplate());
            await harness.AddAsync(NotificationPipelineHarness.Channel(ChannelKind.InApp, InAppProviderKey));

            var id = await harness.InScopeAsync(sp => new DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>())
                .CloudUsageThresholdAsync(UserId, 81.5m, 100m));
            await harness.RunSiteAsync();

            var sent = Assert.Single(inApp.Sent);
            Assert.Equal("Spent 81.50 of 100.00.", sent.Payload.Body);
            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(id))!.Status);
        }

        [Fact]
        public async Task The_healthcheck_alert_shape_dispatches()
        {
            var (harness, inApp, webhook) = await StartWithInAppAndWebhookAsync();
            await using var _ = harness;

            var id = await harness.InScopeAsync(sp => new DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>())
                .HealthCheckAlertAsync(UserId, "database"));
            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationSeverity.Critical, request.Severity);
            Assert.Equal("health", request.Topic);
            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.Equal("Health check 'database' is unhealthy", Assert.Single(inApp.Sent).Payload.Subject); // a subject is text, never HTML-encoded
            Assert.Equal("Health check 'database' is unhealthy", Assert.Single(webhook.Sent).Payload.Subject);
        }

        [Fact]
        public async Task The_export_ready_shape_dispatches()
        {
            var email = new FakeTransportChannel(ChannelKind.Email, "capture-email");
            await using var harness = await StartAsync(email);
            await harness.AddAsync(GenericTemplate());
            await harness.AddAsync(NotificationPipelineHarness.Channel(ChannelKind.Email, "capture-email"));

            var id = await harness.InScopeAsync(sp => new DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>())
                .ExportReadyAsync(UserId, "Q3 invoices"));
            await harness.RunSiteAsync();

            Assert.Equal(NotificationStatus.Sent, (await harness.GetAsync<NotificationRequestAggregate>(id))!.Status);
            var sent = Assert.Single(email.Sent);
            Assert.Equal(RecipientKind.User, sent.Recipient.Kind);
            Assert.Equal("Your export 'Q3 invoices' is ready", sent.Payload.Subject); // as the reader sees it - never "&#x27;"
        }

        [Fact]
        public async Task The_controlflow_runtime_parsed_shape_dispatches()
        {
            var (harness, inApp, webhook) = await StartWithInAppAndWebhookAsync();
            await using var _ = harness;
            var step = new Dictionary<string, object>
            {
                ["template"] = DispatchTestProducers.TemplateKey,
                ["recipients"] = new object[] { "user:" + UserId },
                ["channels"] = "webhook, inapp",
                ["parameters"] = new Dictionary<string, object> { ["subject"] = "Run 42", ["body"] = 42 },
                ["severity"] = "warning",
            };

            var id = await harness.InScopeAsync(sp => new DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>()).ParsedStepAsync(step));
            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationSeverity.Warning, request.Severity);
            Assert.Equal([ChannelKind.Webhook, ChannelKind.InApp], request.RequestedChannels);
            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.Equal("42", Assert.Single(webhook.Sent).Payload.Body);
            Assert.Single(inApp.Sent);
        }

        [Fact]
        public async Task The_cloud_usage_shape_dispatches()
        {
            var inApp = new FakeTransportChannel(ChannelKind.InApp, InAppProviderKey);
            await using var harness = await StartAsync(inApp);
            await harness.AddAsync(GenericTemplate());
            await harness.AddAsync(NotificationPipelineHarness.Channel(ChannelKind.InApp, InAppProviderKey));

            var id = await harness.InScopeAsync(sp => new DispatchTestProducers(sp.GetRequiredService<INotificationDispatcher>())
                .CloudUsageThresholdAsync(UserId, 120m, 100m));
            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal("cloud-usage", request.Topic);
            Assert.Equal(NotificationStatus.Sent, request.Status);
        }

        // ---- parameters survive persistence to render time ---------------------------------------------------------

        [Fact]
        public async Task Parameters_survive_persistence_and_are_rendered_by_a_later_scope()
        {
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            await using var harness = await StartAsync(webhook);
            await harness.AddAsync(NotificationPipelineHarness.Template(TemplateKey, "Pipeline {{ name }} failed", "Run {{ runId }} failed.", "name", "runId"));
            await harness.AddAsync(NotificationPipelineHarness.Channel(ChannelKind.Webhook, WebhookProviderKey));

            var id = await harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, UserId.ToString())], [NotificationChannelKind.Webhook],
                [new DispatchParameter("name", "nightly-load"), new DispatchParameter("runId", "r-981")]));

            // A different scope re-reads the persisted request - the parameters must have travelled with it.
            var reread = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal([new NotificationParameter("name", "nightly-load"), new NotificationParameter("runId", "r-981")], reread.Parameters);
            Assert.Empty(webhook.Sent);

            await harness.RunSiteAsync();

            var sent = Assert.Single(webhook.Sent);
            Assert.Equal("Pipeline nightly-load failed", sent.Payload.Subject);
            Assert.Equal("Run r-981 failed.", sent.Payload.Body);
        }

        // ---- recipients are constrained ------------------------------------------------------

        [Fact]
        public async Task An_external_email_recipient_is_refused_unless_the_site_opts_in_and_nothing_is_persisted()
        {
            await using var harness = await StartAsync();
            var request = new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.Email, "ops@example.com")], [NotificationChannelKind.Email]);

            var refused = await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(request));
            Assert.Contains(NotificationSiteSettingNames.ExternalRecipients, refused.Message);
            Assert.DoesNotContain("ops@example.com", refused.Message); // the reason is logged; the address is not
            Assert.Empty(await ListRequestsAsync(harness));

            harness.Settings.ExternalRecipients[NotificationPipelineHarness.SiteId] = true;
            var id = await harness.DispatchAsync(request);

            var persisted = Assert.Single(await ListRequestsAsync(harness));
            Assert.Equal(id, persisted.Id);
            Assert.Equal(NotificationStatus.Queued, persisted.Status);
            Assert.Equal(new Recipient(RecipientKind.Email, "ops@example.com"), Assert.Single(persisted.Recipients));
        }

        [Fact]
        public async Task A_malformed_external_email_address_is_refused_even_with_the_opt_in()
        {
            await using var harness = await StartAsync();
            harness.Settings.ExternalRecipients[NotificationPipelineHarness.SiteId] = true;

            await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.Email, "not an address")], [NotificationChannelKind.Email])));
            Assert.Empty(await ListRequestsAsync(harness));
        }

        [Fact]
        public async Task An_endpoint_recipient_is_refused_even_with_the_external_opt_in()
        {
            await using var harness = await StartAsync();
            harness.Settings.ExternalRecipients[NotificationPipelineHarness.SiteId] = true;

            var refused = await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.Endpoint, "https://hooks.example.com/relay")], [NotificationChannelKind.Webhook])));
            Assert.Contains("Endpoint", refused.Message);
            Assert.Empty(await ListRequestsAsync(harness));
        }

        [Fact]
        public async Task A_user_who_is_not_a_user_of_this_site_is_refused()
        {
            await using var harness = await StartAsync();
            harness.Directory.SiteUsers.Clear();
            harness.Directory.SiteUsers.Add((NotificationPipelineHarness.SiteId + 1, 99)); // a user of ANOTHER site

            await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, "99")], [NotificationChannelKind.InApp])));
            await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, "not-a-number")], [NotificationChannelKind.InApp])));
            Assert.Empty(await ListRequestsAsync(harness));
        }

        [Fact]
        public async Task A_dispatch_with_no_resolved_site_is_refused()
        {
            // The job path's mock alias (SiteId = -1): a producer running in a job must set the site's alias first.
            await using var harness = await StartAsync(configure: services =>
                services.AddScoped(_ => new SiteState { Alias = new Stomme.Models.Alias { AliasId = -1, TenantId = 1, SiteId = -1 } }));

            await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, UserId.ToString())], [NotificationChannelKind.InApp])));
        }

        [Fact]
        public async Task An_invalid_request_is_refused_before_anything_is_persisted()
        {
            await using var harness = await StartAsync();
            DispatchRecipient[] user = [new DispatchRecipient(NotificationRecipientKind.User, UserId.ToString())];

            foreach (var invalid in new[]
            {
                new NotificationDispatchRequest(" ", user, [NotificationChannelKind.InApp]),
                new NotificationDispatchRequest(TemplateKey, [], [NotificationChannelKind.InApp]),
                new NotificationDispatchRequest(TemplateKey, user, []),
                new NotificationDispatchRequest(TemplateKey, user, [(NotificationChannelKind)42]),
                new NotificationDispatchRequest(TemplateKey, user, [NotificationChannelKind.InApp], [new DispatchParameter("", "x")]),
                new NotificationDispatchRequest(TemplateKey, user, [NotificationChannelKind.InApp], [new DispatchParameter("a", "1"), new DispatchParameter("A", "2")]),
                new NotificationDispatchRequest(TemplateKey, user, [NotificationChannelKind.InApp], Severity: (NotificationSeverity)9),
            })
            {
                await Assert.ThrowsAsync<NotificationDispatchRefusedException>(() => harness.DispatchAsync(invalid));
            }

            Assert.Empty(await ListRequestsAsync(harness));
        }

        [Fact]
        public async Task Without_a_registered_bus_nothing_is_sent_and_the_request_waits_for_the_job()
        {
            await using var harness = await StartAsync();
            var id = await harness.DispatchAsync(new NotificationDispatchRequest(TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, UserId.ToString())], [NotificationChannelKind.InApp]));

            Assert.Null(harness.Bus);
            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Queued, request.Status);
            Assert.Equal(NotificationPipelineHarness.Start, request.NextAttemptOn);
            Assert.Null(request.CompletedOn);
        }

        // ---- dispatch facts (8) ------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Dispatch_PersistsQueuedRequest_AndEnqueuesDispatchMessage()
        {
            // The bus is opt-in, so it is registered here; the recipient is an external address,
            // so the site opts in.
            await using var harness = await StartAsync(withEventBus: true);
            harness.Settings.ExternalRecipients[NotificationPipelineHarness.SiteId] = true;

            var id = await harness.DispatchAsync(new NotificationDispatchRequest(
                TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.Email, "ops@example.com")],
                [NotificationChannelKind.Webhook]));

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Queued, request.Status);
            Assert.Empty(request.Attempts);
            Assert.Contains(harness.Bus!.SentLocal, m => m is NotificationDispatchHint hint && hint.RequestId == id);
        }

        [Fact]
        public async Task Send_AllChannelsSucceed_MarksSent()
        {
            var (harness, webhook, _) = await BuildHostAsync();
            await using var _disp = harness;
            await SeedAsync(harness, Template(), WebhookChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook);

            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.Single(webhook.Sent);
            Assert.Equal("Body", webhook.Sent[0].Payload.Body);
        }

        [Fact]
        public async Task Send_OneChannelFails_MarksPartiallyFailed()
        {
            var (harness, _, inApp) = await BuildHostAsync();
            await using var _disp = harness;
            inApp.Result = ChannelDeliveryResult.Fail("transport refused");
            await SeedAsync(harness, Template(), WebhookChannel(), InAppChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook, ChannelKind.InApp);

            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.PartiallyFailed, request.Status);
        }

        [Fact]
        public async Task Send_AllChannelsFail_MarksFailed()
        {
            var (harness, webhook, _) = await BuildHostAsync();
            await using var _disp = harness;
            webhook.Result = ChannelDeliveryResult.Fail("transport refused");
            await SeedAsync(harness, Template(), WebhookChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook);

            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Failed, request.Status);
        }

        [Fact]
        public async Task Send_RequestedKindWithoutEnabledChannel_RecordsFailedAttempt()
        {
            var (harness, _, _) = await BuildHostAsync();
            await using var _disp = harness;
            await SeedAsync(harness, Template(), WebhookChannel());
            // A site with NO e-mail channel now gets a default one, so "without an enabled channel" is an e-mail
            // channel an administrator turned off.
            var disabledEmail = NotificationPipelineHarness.Channel(ChannelKind.Email, NotificationProviderKeys.Email, "E-mail (off)");
            disabledEmail.Disable();
            await harness.AddAsync(disabledEmail);
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook, ChannelKind.Email);

            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.PartiallyFailed, request.Status);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.Email && a.Status == DeliveryStatus.Failed);
            Assert.Contains(request.Attempts, a => a.Channel == ChannelKind.Webhook && a.Status == DeliveryStatus.Succeeded);
        }

        [Fact]
        public async Task Retry_ReattemptsOnlyFailedChannels_AndCanRecover()
        {
            var (harness, webhook, _) = await BuildHostAsync();
            await using var _disp = harness;
            webhook.Result = ChannelDeliveryResult.Fail("transport refused");
            await SeedAsync(harness, Template(), WebhookChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook);

            await harness.RunSiteAsync();
            Assert.Equal(NotificationStatus.Failed, (await harness.GetAsync<NotificationRequestAggregate>(id))!.Status);

            // A retry is the job's next pass once the back-off has elapsed.
            webhook.Result = ChannelDeliveryResult.Ok();
            harness.Clock.Advance(NotificationDispatchProcessor.BackoffFor(1));
            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status);
            // One failed attempt then one successful - the retry re-attempted the still-failing channel.
            Assert.Equal(2, request.Attempts.Count);
        }

        [Fact]
        public async Task DispatchConsumer_SchedulesDeferredRetry_WhenNotSent()
        {
            var (harness, webhook, _) = await BuildHostAsync();
            await using var _disp = harness;
            webhook.Result = ChannelDeliveryResult.Fail("transport refused");
            await SeedAsync(harness, Template(), WebhookChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook);

            await harness.RunSiteAsync();

            // The retry is re-armed on the request itself: the scalar
            // retry state the job selects on - one retry consumed, next attempt after the first back-off.
            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Failed, request.Status);
            Assert.Equal(1, request.AttemptCount);
            Assert.Equal(NotificationPipelineHarness.Start + NotificationDispatchProcessor.BackoffFor(1), request.NextAttemptOn);
            Assert.Null(request.CompletedOn);
        }

        [Fact]
        public async Task DispatchConsumer_DoesNotScheduleRetry_WhenSent()
        {
            var (harness, _, _) = await BuildHostAsync();
            await using var _disp = harness;
            await SeedAsync(harness, Template(), WebhookChannel());
            var id = await SeedRequestAsync(harness, ChannelKind.Webhook);

            await harness.RunSiteAsync();

            var request = (await harness.GetAsync<NotificationRequestAggregate>(id))!;
            Assert.Equal(NotificationStatus.Sent, request.Status);
            Assert.Equal(0, request.AttemptCount);
            Assert.Equal(NotificationPipelineHarness.Start, request.CompletedOn);
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        private static async Task<NotificationPipelineHarness> StartAsync(
            FakeTransportChannel? transport = null, bool withEventBus = false, Action<IServiceCollection>? configure = null)
        {
            var harness = await NotificationPipelineHarness.StartAsync(transport is null ? [] : [transport], withEventBus, configure);
            harness.Directory.SiteUsers.Add((NotificationPipelineHarness.SiteId, UserId));
            return harness;
        }

        private static async Task<(NotificationPipelineHarness Harness, FakeTransportChannel InApp, FakeTransportChannel Webhook)> StartWithInAppAndWebhookAsync()
        {
            var inApp = new FakeTransportChannel(ChannelKind.InApp, InAppProviderKey);
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            var harness = await NotificationPipelineHarness.StartAsync([inApp, webhook]);
            harness.Directory.SiteUsers.Add((NotificationPipelineHarness.SiteId, UserId));
            await harness.AddAsync(GenericTemplate());
            await harness.AddAsync(
                NotificationPipelineHarness.Channel(ChannelKind.InApp, InAppProviderKey),
                NotificationPipelineHarness.Channel(ChannelKind.Webhook, WebhookProviderKey));
            return (harness, inApp, webhook);
        }

        private static NotificationTemplateAggregate GenericTemplate() =>
            NotificationPipelineHarness.Template(DispatchTestProducers.TemplateKey, "{{ subject }}", "{{ body }}", "subject", "body");

        private static Task<IReadOnlyList<NotificationRequestAggregate>> ListRequestsAsync(NotificationPipelineHarness harness) =>
            harness.InScopeAsync(sp => sp.GetRequiredService<INotificationRequestRepository>().ListBySiteAsync(NotificationPipelineHarness.SiteId));

        // A controllable capturing webhook + in-app provider.
        private static async Task<(NotificationPipelineHarness Harness, FakeTransportChannel Webhook, FakeTransportChannel InApp)> BuildHostAsync()
        {
            var webhook = new FakeTransportChannel(ChannelKind.Webhook, WebhookProviderKey);
            var inApp = new FakeTransportChannel(ChannelKind.InApp, InAppProviderKey);
            var harness = await NotificationPipelineHarness.StartAsync([webhook, inApp]);
            return (harness, webhook, inApp);
        }

        private static NotificationTemplateAggregate Template() =>
            NotificationPipelineHarness.Template(TemplateKey, "Subject", "Body");

        private static NotificationChannelAggregate WebhookChannel() =>
            NotificationPipelineHarness.Channel(ChannelKind.Webhook, WebhookProviderKey, "Capturing webhook");

        private static NotificationChannelAggregate InAppChannel() =>
            NotificationPipelineHarness.Channel(ChannelKind.InApp, InAppProviderKey, "Capturing in-app");

        private static async Task SeedAsync(NotificationPipelineHarness harness, NotificationTemplateAggregate template, params NotificationChannelAggregate[] channels)
        {
            await harness.AddAsync(template);
            await harness.AddAsync(channels);
        }

        private static async Task<Guid> SeedRequestAsync(NotificationPipelineHarness harness, params ChannelKind[] kinds)
        {
            var request = NotificationPipelineHarness.Request(TemplateKey, [NotificationTestData.Email()], kinds);
            await harness.AddAsync(request);
            return request.Id;
        }

        // ---- the dependency scanners ----------------------------------------------------------------------------

        /// <summary>The namespaces a Shared-only dependent must never reach: the module and its repositories.</summary>
        private static readonly string[] ForbiddenNamespaces = ["Stomme.Modules.Notifications", "Stomme.Notifications.Persistence"];

        /// <summary>Every type a member signature of <paramref name="type"/> names that lives in a forbidden namespace (generic arguments included).</summary>
        internal static IReadOnlyList<string> ForbiddenSignatureReferences(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var named = new List<(string Where, Type Type)>();
            named.AddRange(type.GetFields(all).Select(field => ($"field {field.Name}", field.FieldType)));
            named.AddRange(type.GetProperties(all).Select(property => ($"property {property.Name}", property.PropertyType)));
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                named.AddRange(method.GetParameters().Select(parameter => ($"{method.Name}({parameter.Name})", parameter.ParameterType)));
                if (method is MethodInfo info)
                {
                    named.Add(($"{method.Name} return", info.ReturnType));
                }
            }

            if (type.BaseType is not null)
            {
                named.Add(("base", type.BaseType));
            }

            named.AddRange(type.GetInterfaces().Select(implemented => ("implements", implemented)));

            return named
                .SelectMany(entry => Flatten(entry.Type).Select(inner => (entry.Where, Type: inner)))
                .Where(entry => entry.Type.Namespace is { } ns && ForbiddenNamespaces.Any(forbidden => ns == forbidden || ns.StartsWith(forbidden + ".", StringComparison.Ordinal)))
                .Select(entry => $"{entry.Where}: {entry.Type.FullName}")
                .ToList();
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.HasElementType)
            {
                foreach (var inner in Flatten(type.GetElementType()!))
                {
                    yield return inner;
                }
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
                {
                    yield return argument;
                }
            }
        }

        /// <summary>
        /// Every using directive or qualified name in <paramref name="sources"/> that reaches a forbidden namespace, and
        /// every identifier naming a module type (read from the module's namespaces by reflection) - syntax tokens only,
        /// so a comment mentioning a type is not a reference.
        /// </summary>
        internal static IReadOnlyList<string> ForbiddenSourceReferences(IEnumerable<string> sources)
        {
            var moduleTypeNames = typeof(NotificationDispatcher).Assembly.GetTypes()
                .Where(type => type.Namespace is { } ns && ForbiddenNamespaces.Any(forbidden => ns == forbidden || ns.StartsWith(forbidden + ".", StringComparison.Ordinal)))
                .Select(type => type.Name.Split('`')[0])
                .Where(name => !name.StartsWith('<'))
                .ToHashSet(StringComparer.Ordinal);

            var hits = new List<string>();
            foreach (var source in sources)
            {
                var root = CSharpSyntaxTree.ParseText(source).GetRoot();
                foreach (var name in root.DescendantNodes().OfType<QualifiedNameSyntax>().Select(node => node.ToString())
                    .Concat(root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(node => node.Name?.ToString() ?? string.Empty)))
                {
                    if (ForbiddenNamespaces.Any(forbidden => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal)))
                    {
                        hits.Add(name);
                    }
                }

                hits.AddRange(root.DescendantTokens()
                    .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && moduleTypeNames.Contains(token.ValueText))
                    .Select(token => "identifier " + token.ValueText));
            }

            return hits;
        }

        private static string FixtureDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);
            var fixture = Path.Combine(directory!.FullName, "test", "Stomme.Tests", "Fixtures", "DispatchTestModule");
            Assert.True(Directory.GetFiles(fixture, "*.cs").Length > 0, "The DispatchTestModule fixture has no source: " + fixture);
            return fixture;
        }

        /// <summary>Known-dirty planted producer (scanner validation only).</summary>
        private sealed class PlantedModuleCoupledProducer
        {
            private readonly NotificationDispatcher? _dispatcher = null;

            public NotificationDispatcher? Dispatcher => _dispatcher;
        }

        /// <summary>Known-clean planted producer (scanner validation only).</summary>
        private sealed class PlantedPortOnlyProducer
        {
            private readonly INotificationDispatcher? _dispatcher = null;

            public INotificationDispatcher? Dispatcher => _dispatcher;
        }
    }
}
