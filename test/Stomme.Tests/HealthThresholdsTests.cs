#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Health.Application;
using Stomme.Repository;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Thresholds = Stomme.Modules.Health.Thresholds;

namespace Stomme.Tests
{
    /// <summary>
    /// The health THRESHOLDS page (BL-83 / D104 item 12, with its correction): installation-wide Host-scope settings, so
    /// the page is HOST-ONLY - a site's administrator must not change every other site's alerting.
    /// </summary>
    /// <remarks>
    /// The server gate is the framework's own: <c>SettingController</c> lets only the Host role EDIT Host settings. This
    /// suite drives it for the health keys specifically, pins the page's restated keys and defaults to
    /// <see cref="HealthSettings"/> (the client cannot reference the server), and clicks the page.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthThresholdsTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HealthThresholdsTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void The_pages_keys_and_defaults_are_HealthSettings_own()
        {
            var razor = System.IO.File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules", "Health", "Thresholds.razor"));
            var fields = Regex.Matches(razor, "new\\(\"(\\w+)\", \"[^\"]*\", (\\d+), (\\d+)\\)")
                .Select(match => (Key: match.Groups[1].Value, Default: int.Parse(match.Groups[2].Value), Max: int.Parse(match.Groups[3].Value)))
                .ToDictionary(field => field.Key);

            var expected = new Dictionary<string, int>
            {
                [HealthSettings.DegradedLatencyKey] = HealthSettings.DefaultDegradedLatencyMs,
                [HealthSettings.ProbeTimeoutKey] = HealthSettings.DefaultProbeTimeoutMs,
                [HealthSettings.UnhealthyAlertAfterKey] = HealthSettings.DefaultUnhealthyAlertAfterMinutes,
                [HealthSettings.DegradedAlertAfterKey] = HealthSettings.DefaultDegradedAlertAfterMinutes,
                [HealthSettings.CooldownKey] = HealthSettings.DefaultCooldownMinutes,
                [HealthSettings.RetentionKey] = HealthSettings.DefaultRetentionDays,
            };

            Assert.Equal(expected.Keys.OrderBy(key => key), fields.Keys.OrderBy(key => key));
            Assert.All(expected, pair => Assert.Equal(pair.Value, fields[pair.Key].Default));
            Assert.Equal(HealthSettings.MaximumRetentionDays, fields[HealthSettings.RetentionKey].Max);
        }

        [Fact]
        public void The_page_itself_is_HOST_level_so_the_framework_routes_nobody_else_to_it()
        {
            Assert.Equal(SecurityAccessLevel.Host, new Thresholds().SecurityAccessLevel);
        }

        [Fact]
        public async Task A_site_ADMINISTRATOR_cannot_change_the_thresholds_and_the_HOST_can()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var admin = await GateDrive.AddUserAsync(app, Host, "health-admin", "Adm1n!Health$2026", "health-admin@gate.test", RoleNames.Admin);
                var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, "health-admin", "Adm1n!Health$2026", _output);

                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                static object Body(string value) => new[] { new { SettingId = 0, EntityName = EntityNames.Host, EntityId = -1, SettingName = HealthSettings.DegradedLatencyKey, SettingValue = value, IsPrivate = false } };

                // A site's Administrator holds health:manage on its site - and is still refused here: these values are the
                // whole installation's.
                var refused = await GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, admin.UserId, HttpMethod.Put, "/api/Setting/Host/-1", Body("99"), _output, "administrator saves a threshold");
                Assert.True(refused.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"an administrator got {(int)refused.Status}");
                Assert.Equal(TimeSpan.FromMilliseconds(HealthSettings.DefaultDegradedLatencyMs), (await LoadAsync(app)).DegradedLatency);

                // The Host's save lands, and it is what the health module then reads.
                var saved = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, "/api/Setting/Host/-1", Body("1500"), _output, "host saves a threshold");
                Assert.True((int)saved.Status is >= 200 and < 300, $"the host got {(int)saved.Status}");
                Assert.Equal(TimeSpan.FromMilliseconds(1500), (await LoadAsync(app)).DegradedLatency);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void The_page_loads_the_values_and_SAVE_sends_exactly_the_health_keys_clamped()
        {
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var settings = RecordingSettings.Create(new Dictionary<string, string>
            {
                [HealthSettings.DegradedLatencyKey] = "1500",
                [HealthSettings.RetentionKey] = "not a number",
                ["SomeOtherHostSetting"] = "left alone",
            });
            context.Services.AddSingleton(settings);
            context.Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            context.Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());

            var cut = context.Render<Thresholds>(parameters => parameters
                .AddCascadingValue(new PageState
                {
                    Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
                    Site = new Site { SiteId = 1, Name = "Site" },
                    Page = new Page { PageId = 5, SiteId = 1, Path = "admin/health", Name = "Health" },
                    User = new User { UserId = 1, Username = "host", Roles = ";" + RoleNames.Host + ";" },
                    QueryString = new Dictionary<string, string>(),
                    UrlParameters = "",
                })
                .AddCascadingValue(new Models.Module { ModuleId = 21, SiteId = 1, PermissionList = new List<Permission>() }));

            cut.WaitForAssertion(() => Assert.Equal("1500", cut.Find($"#{HealthSettings.DegradedLatencyKey}").GetAttribute("value")));
            Assert.True(string.IsNullOrEmpty(cut.Find($"#{HealthSettings.RetentionKey}").GetAttribute("value")));   // unusable -> empty, i.e. default

            cut.Find($"#{HealthSettings.RetentionKey}").Input("9999");
            cut.Find("#health-settings-save").Click();

            var proxy = (RecordingSettings)(object)settings;
            cut.WaitForAssertion(() => Assert.NotNull(proxy.Saved));
            Assert.Equal((EntityNames.Host, -1), (proxy.SavedEntity, proxy.SavedId));
            Assert.Equal(6, proxy.Saved!.Count);
            Assert.DoesNotContain("SomeOtherHostSetting", proxy.Saved.Keys);                // only the health keys are sent
            Assert.Equal("365", proxy.Saved[HealthSettings.RetentionKey]);                  // clamped to the maximum
            Assert.Equal("1500", proxy.Saved[HealthSettings.DegradedLatencyKey]);
            Assert.Equal("", proxy.Saved[HealthSettings.CooldownKey]);                       // empty = the default
        }

        private static Task<HealthSettings> LoadAsync(Microsoft.AspNetCore.Builder.WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) => HealthSettings.LoadAsync(sp.GetRequiredService<ISettingRepository>()));

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        /// <summary>An <see cref="ISettingService"/> that answers the two calls the page makes and records the save.</summary>
        public class RecordingSettings : DispatchProxy
        {
            public Dictionary<string, string> Stored = new();
            public Dictionary<string, string>? Saved;
            public string? SavedEntity;
            public int SavedId;

            public static ISettingService Create(Dictionary<string, string> stored)
            {
                var proxy = DispatchProxy.Create<ISettingService, RecordingSettings>();
                ((RecordingSettings)(object)proxy).Stored = stored;
                return proxy;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                switch (method!.Name)
                {
                    case nameof(ISettingService.GetSettingsAsync) when method.ReturnType == typeof(Task<Dictionary<string, string>>):
                        return Task.FromResult(new Dictionary<string, string>(Stored));
                    case nameof(ISettingService.UpdateSettingsAsync):
                        Saved = new Dictionary<string, string>((Dictionary<string, string>)args![0]!);
                        SavedEntity = (string)args[1]!;
                        SavedId = (int)args[2]!;
                        return Task.CompletedTask;
                    default:
                        throw new NotSupportedException(method.Name + " is not something the thresholds page should call.");
                }
            }
        }
    }
}
