#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Modules.Portability.Application;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Modules.Portability.Contributions;
using Stomme.Modules.Portability.Domain;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability;
using Stomme.Portability.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// BL-82 (refutations 9, 11, 13) - the SHIPPED contributors on a really installed site: profile
    /// definitions travel to a second site and back by name, private never becomes public; a person's data package holds
    /// their account, settings, site roles, notifications' metadata, sessions and audit lines - and none of their secrets:
    /// no password hash, no security stamp, no private setting, no notification body (the framework writes live reset
    /// tokens into bodies).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PortabilityContributorDriveTests
    {
        private const string Host = "localhost";
        private const string SecondSiteHost = "portability-b.localhost";

        private readonly ITestOutputHelper _output;

        public PortabilityContributorDriveTests(ITestOutputHelper output) => _output = output;

        private static JsonObject Enabled() => new() { ["Portability"] = new JsonObject { ["Enabled"] = true } };

        private async Task<WebApplication> InstallAsync(ScratchGateInstall scratch)
        {
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            return app;
        }

        [Fact]
        public async Task Profile_definitions_travel_by_name_and_private_never_becomes_public()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Enabled());
            var app = await InstallAsync(scratch);
            try
            {
                var siteB = await CreateSecondSiteAsync(app);

                // Site A: a public field and a private one. Site B already has the public one's NAME, differently cased, private.
                var siteA = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var profiles = sp.GetRequiredService<IProfileRepository>();
                    await profiles.AddProfileAsync(Profile(alias.SiteId, "Favourite Colour", isPrivate: false));
                    await profiles.AddProfileAsync(Profile(alias.SiteId, "Shoe Size", isPrivate: true));
                    return alias.SiteId;
                });
                await GateDrive.InTenantAsync(app, SecondSiteHost, async (sp, alias) =>
                    await sp.GetRequiredService<IProfileRepository>().AddProfileAsync(Profile(alias.SiteId, "favourite colour", isPrivate: true)));

                var package = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var entities = await sp.GetRequiredService<PortabilityExporter>().ExportConfigAsync(alias.SiteId,
                        new Dictionary<string, IReadOnlyCollection<string>?> { [ProfileDefinitionContributor.Type] = null });
                    return new PortabilityBundleSerializer().Serialize(PortabilityBundleDocument.Create(
                        PortabilityPackageKinds.ConfigTransfer, $"x:{alias.SiteId}", null, new PortabilityBundleProvenance(1, DateTime.UtcNow, null), entities));
                });
                Assert.Contains("Favourite Colour", package, StringComparison.Ordinal);
                Assert.Contains("Shoe Size", package, StringComparison.Ordinal);

                async Task<ImportReport> ImportOnBAsync(ConflictStrategy strategy) => await GateDrive.InTenantAsync(app, SecondSiteHost, (sp, alias) =>
                    sp.GetRequiredService<PortabilityImporter>().ImportAsync(package, 10_000_000, new ImportContext(alias.SiteId, strategy, new PortableIdMap())));
                async Task<List<Profile>> BProfilesAsync() => await GateDrive.InTenantAsync(app, SecondSiteHost, async (sp, alias) =>
                    (await sp.GetRequiredService<IProfileRepository>().GetProfilesAsync(alias.SiteId)).Where(p => p.SiteId == alias.SiteId).ToList());

                // Skip: the existing name (matched case-insensitively) is left alone; the new one arrives.
                var skip = await ImportOnBAsync(ConflictStrategy.Skip);
                Assert.True(skip.Accepted, string.Join("; ", skip.Refusals));
                Assert.Equal(EntityImportResult.Skipped, Assert.Single(skip.Lines, l => l.DisplayName == "Favourite Colour").Result);
                Assert.Equal(EntityImportResult.Imported, Assert.Single(skip.Lines, l => l.DisplayName == "Shoe Size").Result);
                var afterSkip = await BProfilesAsync();
                Assert.True(Assert.Single(afterSkip, p => p.Name == "favourite colour").IsPrivate);
                Assert.True(Assert.Single(afterSkip, p => p.Name == "Shoe Size").IsPrivate);

                // Overwrite: would make B's private field public - the WHOLE package is refused, nothing changes.
                var overwrite = await ImportOnBAsync(ConflictStrategy.Overwrite);
                Assert.False(overwrite.Accepted);
                Assert.Contains(overwrite.Refusals, r => r.Contains("never public", StringComparison.Ordinal));
                var afterRefusal = await BProfilesAsync();
                Assert.Equal(afterSkip.Count, afterRefusal.Count);
                Assert.True(Assert.Single(afterRefusal, p => p.Name == "favourite colour").IsPrivate);

                // Rename: both arrive beside what is there.
                var rename = await ImportOnBAsync(ConflictStrategy.Rename);
                Assert.True(rename.Accepted, string.Join("; ", rename.Refusals));
                var afterRename = await BProfilesAsync();
                Assert.Contains(afterRename, p => p.Name == "Favourite Colour2" && !p.IsPrivate);
                Assert.Contains(afterRename, p => p.Name == "Shoe Size2" && p.IsPrivate);

                // Site A was never touched.
                var aNames = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IProfileRepository>().GetProfilesAsync(siteA)).Where(p => p.SiteId == siteA).Select(p => p.Name).ToList());
                Assert.DoesNotContain("Favourite Colour2", aNames);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_persons_data_package_holds_their_data_and_none_of_their_secrets()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: Enabled());
            var app = await GateDrive.InstallAsync(scratch, _output); // started: the person signs in over HTTP
            try
            {
                const string Username = "bl82-subject";
                const string Password = "Bl82!Subject$2026";
                var user = await GateDrive.AddUserAsync(app, Host, Username, Password, "bl82-subject@gate.test");

                // A sign-in (a session row, an audit line), a public and a private setting, a password reset (a LIVE
                // token in a notification body) and a planted notification whose body is a marker.
                using (var client = GateDrive.NewClient(app, Host))
                {
                    await GateDrive.LoginAsync(app, client, Host, Username, Password, _output);
                }

                var (bodies, stamp, hash) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    await settings.AddSettingAsync(new Setting { EntityName = EntityNames.User, EntityId = user.UserId, SettingName = "Theme", SettingValue = "dark", IsPrivate = false });
                    await settings.AddSettingAsync(new Setting { EntityName = EntityNames.User, EntityId = user.UserId, SettingName = "ApiToken", SettingValue = "PRIVATE-SETTING-VALUE-123", IsPrivate = true });

                    Assert.True(await sp.GetRequiredService<IUserManager>().ForgotPassword(Username));
                    var notifications = sp.GetRequiredService<INotificationRepository>();
                    await notifications.AddNotificationAsync(new Notification
                    {
                        SiteId = alias.SiteId, ToUserId = user.UserId, ToEmail = "bl82-subject@gate.test", ToDisplayName = Username,
                        FromDisplayName = "Gate", FromEmail = "gate@gate.test", Subject = "Planted Subject", Body = "BODY-MARKER-XYZ",
                        CreatedOn = DateTime.UtcNow, IsDelivered = false,
                    });

                    // EVERY framework notification to them - the reset, and the welcome/verification mails - may carry a token.
                    var framework = (await notifications.GetNotificationsAsync(alias.SiteId, -1, user.UserId)).Where(n => n.Subject != "Planted Subject").Select(n => n.Body).ToList();
                    var identityUser = (await sp.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync(Username))!;
                    return (framework, identityUser.SecurityStamp, identityUser.PasswordHash);
                });

                // Built by the REAL job, as a requester would get it.
                var packageId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var package = PortabilityPackageAggregate.StartBuild(sp.GetRequiredService<ITenantContext>().CurrentTenantId!, alias.SiteId,
                        PortabilityPackageKind.SubjectData, 1, user.UserId, DateTime.UtcNow);
                    await sp.GetRequiredService<IPortabilityPackageRepository>().AddAsync(package);
                    return package.Id;
                });
                await PortabilityDrive.BuildQueuedAsync(app, Host, _output);

                var json = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var package = (await sp.GetRequiredService<IPortabilityPackageRepository>().FindForSiteAsync(alias.SiteId, packageId))!;
                    Assert.Equal(PortabilityPackageStatus.Ready, package.Status);
                    return System.Text.Encoding.UTF8.GetString((await sp.GetRequiredService<PortabilityArtifactStore>().ReadAsync(package))!);
                });

                // ---- their data ------------------------------------------------------------------------------------------
                foreach (var expected in new[] { Username, "bl82-subject@gate.test", "Theme", "dark", RoleNames.Registered, "Planted Subject", "\"session\"", "\"identity-audit\"", "\"user-account\"" })
                {
                    Assert.Contains(expected, json, StringComparison.Ordinal);
                }

                // ---- none of their secrets ---------------------------------------------------------------------------------
                Assert.DoesNotContain("PRIVATE-SETTING-VALUE-123", json, StringComparison.Ordinal);
                Assert.DoesNotContain("BODY-MARKER-XYZ", json, StringComparison.Ordinal);
                Assert.NotEmpty(bodies);                                            // the reset at least
                Assert.Contains(bodies, body => TokensIn(body).Any());               // and it really carries a token
                foreach (var body in bodies)
                {
                    Assert.DoesNotContain(body, json, StringComparison.Ordinal);
                    foreach (var token in TokensIn(body))
                    {
                        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
                    }
                }

                Assert.DoesNotContain(stamp!, json, StringComparison.Ordinal);
                Assert.DoesNotContain(hash!, json, StringComparison.Ordinal);
                Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Every query-string value in a body - where the framework puts a token.</summary>
        private static IEnumerable<string> TokensIn(string body) =>
            System.Text.RegularExpressions.Regex.Matches(body, @"[?&][A-Za-z]+=([^&\s""'<]+)")
                .Select(match => match.Groups[1].Value)
                .Where(value => value.Length >= 16);

        private static Profile Profile(int siteId, string name, bool isPrivate) => new()
        {
            SiteId = siteId, Name = name, Title = name, Description = string.Empty, Category = "Gate", ViewOrder = 99, MaxLength = 50,
            DefaultValue = string.Empty, IsRequired = false, IsPrivate = isPrivate, Options = string.Empty, Validation = string.Empty, Rows = 1,
            Autocomplete = string.Empty,
        };

        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Portability Site B", TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme, DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer, SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static, Runtime = Runtimes.Server, Prerender = false, Hybrid = false,
                    EnhancedNavigation = true, CultureCode = "en", AllowRegistration = false, CaptureBrokenUrls = false,
                    VisitorTracking = false, PwaIsEnabled = false,
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = SecondSiteHost, TenantId = alias.TenantId, SiteId = site.SiteId, IsDefault = true,
                });

                return site;
            });
    }
}
