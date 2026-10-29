using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Security.Abstractions;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// Ported from the predecessor platform <c>tests/Notifications.Tests/NotificationTemplateCqrsTests.cs</c> (7 Fact + 1 Theory x5 =
    /// 12 cases): save (create/update), key uniqueness, placeholder validation, preview rendering and delete-if-unreferenced,
    /// through the mediator. Harness swapped (<see cref="NotificationCqrsHarness"/>).
    /// </summary>
    /// <remarks>
    /// Per-case map: the seven facts keep their names and intent; <c>Delete_ReferencedTemplate_IsRejected</c> seeds the
    /// referencing request through the File-store repository (the predecessor platform: the InMemory repository + unit of work); the Theory's
    /// three query rows expect <c>ManageTemplates</c> - the split drops <c>notifications:view</c>, and PREVIEW in particular is
    /// template authoring. Added below the port (not the predecessor platform cases): the preview's HTML encoding, site isolation.
    /// </remarks>
    public sealed class NotificationTemplateCqrsTests
    {
        private static SaveNotificationTemplateCommand NewSave(string key = "welcome", string? body = null, IReadOnlyList<string>? placeholders = null) =>
            new(Id: null, Key: key,
                SupportedChannels: new[] { NotificationChannelKind.Email },
                Contents: new[] { new NotificationTemplateContentInput("en", "Hello {{ name }}", body ?? "Welcome, {{ name }}!") },
                Placeholders: placeholders ?? new[] { "name" });

        [Fact]
        public async Task Save_CreatesTemplate_ThenListAndGetReturnIt()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));

            var list = await harness.SendAsync(new ListNotificationTemplatesQuery());
            Assert.Contains(list, template => template.Id == id && template.Key == "welcome");

            var view = await harness.SendAsync(new GetNotificationTemplateQuery(id));
            Assert.NotNull(view);
            Assert.Equal("welcome", view!.Key);
            Assert.Equal(new[] { "name" }, view.Placeholders);
        }

        [Fact]
        public async Task Save_WithUndeclaredPlaceholder_IsRejected()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            // Body references {{ company }} but only {{ name }} is declared.
            var command = NewSave(body: "Welcome to {{ company }}, {{ name }}!", placeholders: new[] { "name" });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(command));
            Assert.Contains("company", ex.Message);
        }

        [Fact]
        public async Task Save_DuplicateKey_IsRejected()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            await harness.SendAsync(NewSave(key: "dup"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(NewSave(key: "DUP")));
            Assert.Contains("already exists", ex.Message);
        }

        [Fact]
        public async Task Save_WithExistingId_UpdatesInPlace()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));
            var updatedId = await harness.SendAsync(new SaveNotificationTemplateCommand(
                id, "welcome", new[] { NotificationChannelKind.Email, NotificationChannelKind.InApp },
                new[] { new NotificationTemplateContentInput("en", "Hi {{ name }}", "Updated {{ name }}") },
                new[] { "name" }));

            Assert.Equal(id, updatedId);
            var view = await harness.SendAsync(new GetNotificationTemplateQuery(id));
            Assert.Equal(2, view!.SupportedChannels.Count);
            Assert.Equal("Updated {{ name }}", view.Contents.Single().Body);
        }

        [Fact]
        public async Task Preview_RendersBodyWithParameters()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));

            var preview = await harness.SendAsync(new PreviewNotificationTemplateQuery(
                id, "en", new[] { new DispatchParameter("name", "Ada") }));

            Assert.Equal("en", preview.Locale);
            Assert.Equal("Hello Ada", preview.Subject);
            Assert.Equal("Welcome, Ada!", preview.Body);
            Assert.Null(preview.Error);
        }

        [Fact]
        public async Task Delete_UnreferencedTemplate_Succeeds()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));
            await harness.SendAsync(new DeleteNotificationTemplateCommand(id));

            Assert.Null(await harness.SendAsync(new GetNotificationTemplateQuery(id)));
        }

        [Fact]
        public async Task Delete_ReferencedTemplate_IsRejected()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));

            // A request was sent against the template's key (in another casing - the lookup is normalized) - deletion is refused.
            await harness.AddAsync(NotificationPipelineHarness.Request("WELCOME",
                new[] { new Recipient(RecipientKind.Email, "a@example.com") }, new[] { ChannelKind.Email }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DeleteNotificationTemplateCommand(id)));
            Assert.Contains("referenced", ex.Message);
        }

        [Theory]
        [InlineData(typeof(SaveNotificationTemplateCommand), NotificationsPermissions.ManageTemplates)]
        [InlineData(typeof(DeleteNotificationTemplateCommand), NotificationsPermissions.ManageTemplates)]
        [InlineData(typeof(ListNotificationTemplatesQuery), NotificationsPermissions.ManageTemplates)]
        [InlineData(typeof(GetNotificationTemplateQuery), NotificationsPermissions.ManageTemplates)]
        [InlineData(typeof(PreviewNotificationTemplateQuery), NotificationsPermissions.ManageTemplates)]
        public void Request_DeclaresExpectedPermission(Type requestType, string expected)
        {
            var requires = (RequiresAttribute[])requestType.GetCustomAttributes(typeof(RequiresAttribute), inherit: true);
            Assert.Contains(requires, requirement => requirement.Permission == expected);
        }

        // ---- added in Stomme (not the predecessor platform cases) ------------------------------------------------------------------

        [Fact]
        public async Task Preview_encodes_a_parameter_as_html_and_reports_a_runaway_loop_as_an_error()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var id = await harness.SendAsync(NewSave(key: "welcome"));
            var preview = await harness.SendAsync(new PreviewNotificationTemplateQuery(
                id, "en", new[] { new DispatchParameter("name", "<script>alert(1)</script>") }));
            Assert.DoesNotContain("<script>", preview.Body);
            Assert.Contains("&lt;script&gt;", preview.Body);

            // A runaway made of LEGAL ranges (two nested 10,000-item loops): the range guard lets it be saved, and it is the
            // step budget that stops it at preview. A single huge literal range is refused at save instead.
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new SaveNotificationTemplateCommand(null, "huge", new[] { NotificationChannelKind.Email },
                new[] { new NotificationTemplateContentInput("en", "s", "{% for i in (1..1000000) %}x{% endfor %}") }, Array.Empty<string>())));

            var loop = await harness.SendAsync(new SaveNotificationTemplateCommand(null, "loop", new[] { NotificationChannelKind.Email },
                new[] { new NotificationTemplateContentInput("en", "s", "{% for i in (1..10000) %}{% for j in (1..10000) %}x{% endfor %}{% endfor %}") }, Array.Empty<string>()));
            var failed = await harness.SendAsync(new PreviewNotificationTemplateQuery(loop, "en", null));
            Assert.Null(failed.Body);
            Assert.False(string.IsNullOrEmpty(failed.Error));
        }

        [Fact]
        public async Task A_template_of_another_site_is_neither_listed_nor_readable_nor_previewable()
        {
            var (harness, _, _) = await NotificationCqrsHarness.StartAsync();
            await using var _dispose = harness;

            var foreign = NotificationTemplateAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationPipelineHarness.SiteId + 1, "welcome",
                new[] { ChannelKind.Email }, new[] { new LocalizedContent(new Locale("en"), new Content("s", "b")) }, new PlaceholderSchema(Array.Empty<string>()));
            await harness.AddAsync(foreign);

            Assert.Empty(await harness.SendAsync(new ListNotificationTemplatesQuery()));
            Assert.Null(await harness.SendAsync(new GetNotificationTemplateQuery(foreign.Id)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new PreviewNotificationTemplateQuery(foreign.Id, "en", null)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DeleteNotificationTemplateCommand(foreign.Id)));

            // The same key on THIS site is not a duplicate of another site's.
            Assert.NotEqual(Guid.Empty, await harness.SendAsync(NewSave(key: "welcome")));
        }
    }
}