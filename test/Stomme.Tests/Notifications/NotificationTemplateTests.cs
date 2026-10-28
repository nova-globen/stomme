using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// Ported from the predecessor platform <c>tests/Notifications.Tests/NotificationTemplateTests.cs</c> (7 facts, BL-78): the
    /// <see cref="NotificationTemplateAggregate"/> - at least one channel + locale, distinct locales, the
    /// <see cref="PlaceholderSchema"/> declaration set, the saved event, and round-trip persistence of the localized
    /// content graph (harness swap: the module's File store).
    /// </summary>
    public sealed class NotificationTemplateTests
    {
        [Fact]
        public void Create_RaisesSavedEvent_AndDeclaresPlaceholders()
        {
            var template = NotificationTestData.Template();

            Assert.Single(template.DomainEvents.OfType<NotificationTemplateSavedEvent>());
            Assert.True(template.Placeholders.Declares("runId"));
            Assert.False(template.Placeholders.Declares("unknown"));
        }

        [Fact]
        public void Create_WithoutChannels_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "k", supportedChannels: [],
                contents: [new LocalizedContent(new Locale("en"), new Content("s", "b"))],
                placeholders: new PlaceholderSchema([])));
            Assert.Equal("supportedChannels", ex.ParamName);
        }

        [Fact]
        public void Create_WithoutContent_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "k", supportedChannels: [ChannelKind.Email],
                contents: [], placeholders: new PlaceholderSchema([])));
            Assert.Equal("contents", ex.ParamName);
        }

        [Fact]
        public void Create_WithDuplicateLocale_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "k", supportedChannels: [ChannelKind.Email],
                contents:
                [
                    new LocalizedContent(new Locale("en"), new Content("s", "b1")),
                    new LocalizedContent(new Locale("EN"), new Content("s", "b2")),
                ],
                placeholders: new PlaceholderSchema([])));
            Assert.Equal("contents", ex.ParamName);
        }

        [Fact]
        public void PlaceholderSchema_WithDuplicateName_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => new PlaceholderSchema(["name", "Name"]));
            Assert.Equal("placeholders", ex.ParamName);
        }

        [Fact]
        public void Update_ReplacesContentAndRaisesSavedEvent()
        {
            var template = NotificationTestData.Template();
            template.ClearDomainEvents();

            template.Update(
                supportedChannels: [ChannelKind.Webhook],
                contents: [new LocalizedContent(new Locale("fr"), new Content("Échec", "Le run a échoué."))],
                placeholders: new PlaceholderSchema(["runId"]));

            Assert.Equal([ChannelKind.Webhook], template.SupportedChannels);
            Assert.Equal("fr", template.Contents.Single().Locale.Code);
            Assert.Single(template.DomainEvents.OfType<NotificationTemplateSavedEvent>());
        }

        [Fact]
        public async Task RoundTrips_ThroughRepository_PreservingLocalizedContent()
        {
            await using var store = await NotificationFileStore.StartAsync();

            var template = NotificationTestData.Template();
            await store.AddAsync(template);

            var loaded = await store.GetByIdAsync<NotificationTemplateAggregate>(template.Id);

            Assert.NotNull(loaded);
            Assert.Equal("pipeline.failed", loaded!.Key);
            Assert.Equal(template.Contents.Single().Content, loaded.Contents.Single().Content);
            Assert.Equal(template.Placeholders, loaded.Placeholders);
        }
    }
}
