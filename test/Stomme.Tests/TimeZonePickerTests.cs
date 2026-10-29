using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Controls;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The registration form's time zone (infra, for the owner, 2026-10-29): it starts from the visitor's own zone, as the
    /// browser reports it, and the list of over 400 zones is searched by typing rather than scrolled.
    /// </summary>
    public class TimeZonePickerTests : BunitContext
    {
        private static readonly List<Models.TimeZone> Zones = new TimeZoneService(new EchoLocalizer<TimeZoneResources>()).GetTimeZones();

        [Theory]
        [InlineData("Europe/Stockholm", "Europe/Stockholm")]
        [InlineData("europe/stockholm", "Europe/Stockholm")]
        [InlineData(" Europe/Stockholm ", "Europe/Stockholm")]
        [InlineData("Asia/Calcutta", "Asia/Kolkata")]
        [InlineData("Stockholm", "Europe/Stockholm")]
        [InlineData("new york", "America/New_York")]
        [InlineData("toky", "Asia/Tokyo")]
        [InlineData("new", null)]
        [InlineData("Etc/UTC", null)]
        [InlineData("Mars/Olympus_Mons", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void A_zone_name_finds_its_entry_in_the_list(string name, string expected)
        {
            Assert.Equal(expected, TimeZoneMatcher.Match(name, Zones));
        }

        [Fact]
        public void The_list_is_the_long_one_the_owner_saw()
        {
            Assert.True(Zones.Count > 300, $"{Zones.Count} zones");
            Assert.Contains(Zones, zone => zone.Id == "Europe/Stockholm");
        }

        [Fact]
        public void The_browser_zone_replaces_the_site_default()
        {
            JSInterop.Setup<string>("Stomme.Interop.getTimeZone").SetResult("Asia/Calcutta");
            string bound = null;

            var cut = RenderPicker("Europe/London", true, value => bound = value);

            cut.WaitForAssertion(() => Assert.Equal("Asia/Kolkata", cut.Find("input").GetAttribute("value")));
            Assert.Equal("Asia/Kolkata", bound);
        }

        [Fact]
        public void A_browser_zone_not_in_the_list_keeps_the_site_default()
        {
            JSInterop.Setup<string>("Stomme.Interop.getTimeZone").SetResult("Etc/UTC");
            string bound = null;

            var cut = RenderPicker("Europe/London", true, value => bound = value);

            Assert.Equal("Europe/London", cut.Find("input").GetAttribute("value"));
            Assert.Null(bound);
        }

        [Fact]
        public void Without_the_switch_the_browser_is_not_asked()
        {
            JSInterop.Mode = JSRuntimeMode.Strict;

            var cut = RenderPicker("Europe/London", false, _ => { });

            Assert.Equal("Europe/London", cut.Find("input").GetAttribute("value"));
            Assert.Empty(JSInterop.Invocations);
        }

        [Fact]
        public void Every_zone_is_offered_for_type_to_filter()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = RenderPicker(string.Empty, false, _ => { });

            var input = cut.Find("input#timezone");
            var options = cut.FindAll($"datalist#{input.GetAttribute("list")} option");
            Assert.Equal(Zones.Count, options.Count);
            Assert.Contains(options, option => option.GetAttribute("value") == "Europe/Stockholm" && option.TextContent.Contains("Stockholm"));
        }

        [Fact]
        public void What_is_typed_becomes_the_entry_it_names_or_stays_as_typed()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            string bound = null;
            var cut = RenderPicker(string.Empty, false, value => bound = value);

            cut.Find("input").Change("stockholm");
            Assert.Equal("Europe/Stockholm", bound);

            cut.Find("input").Change("Atlantis");
            Assert.Equal("Atlantis", bound);
        }

        private IRenderedComponent<TimeZonePicker> RenderPicker(string value, bool useBrowser, Action<string> changed)
        {
            Services.AddSingleton<ITimeZoneService>(new TimeZoneService(new EchoLocalizer<TimeZoneResources>()));
            Services.AddSingleton(new SiteState());
            Services.AddSingleton<ILogService>(new ThrowingLogService());

            var pageState = new PageState
            {
                Alias = new Alias { BaseUrl = "" },
                Site = new Site { Settings = new Dictionary<string, string>() },
                QueryString = new Dictionary<string, string>(),
                RenderMode = RenderModes.Interactive,
            };
            var moduleState = new Module
            {
                Settings = new Dictionary<string, string>(),
                PermissionList = new List<Permission>(),
                RenderMode = RenderModes.Interactive,
            };

            return Render<TimeZonePicker>(parameters => parameters
                .Add(p => p.Id, "timezone")
                .Add(p => p.Value, value)
                .Add(p => p.UseBrowserTimeZone, useBrowser)
                .Add(p => p.ValueChanged, changed)
                .AddCascadingValue(pageState)
                .AddCascadingValue(moduleState));
        }

        private sealed class EchoLocalizer<T> : IStringLocalizer<T>
        {
            public LocalizedString this[string name] => new(name, name);
            public LocalizedString this[string name, params object[] arguments] => new(name, name);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }

        private sealed class ThrowingLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => throw NotExpected();
            public Task<Log> GetLogAsync(int logId) => throw NotExpected();
            public Task DeleteLogsAsync(int siteId) => throw NotExpected();
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotExpected();

            private static NotSupportedException NotExpected() => new("The time zone picker logs nothing.");
        }
    }
}
