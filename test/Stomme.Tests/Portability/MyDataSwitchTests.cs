using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Portability;
using Stomme.Modules.Portability.Services;
using Stomme.Portability;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// BL-212 - a person's data section on their profile asks the portability API only when the site does not say it is off.
    /// Off is the default, the API then answers 404, and a WebAssembly site's browser logged that 404 on every profile view.
    /// </summary>
    public sealed class MyDataSwitchTests : BunitContext
    {
        private readonly CountingPortabilityService _service = new();

        public MyDataSwitchTests()
        {
            Services.AddSingleton<IPortabilityService>(_service);
        }

        [Fact]
        public void With_portability_switched_off_the_section_asks_nothing_and_renders_nothing()
        {
            var cut = Render<MyData>(parameters => parameters.Add(data => data.UserId, 7).AddCascadingValue(Page("false")));

            Assert.Empty(_service.Calls);
            Assert.Equal(string.Empty, cut.Markup.Trim());
        }

        [Fact]
        public void With_portability_on_or_not_said_the_section_asks_as_before()
        {
            Render<MyData>(parameters => parameters.Add(data => data.UserId, 7).AddCascadingValue(Page("true")));
            Assert.Equal(new[] { "enabled" }, _service.Calls);

            _service.Calls.Clear();
            Render<MyData>(parameters => parameters.Add(data => data.UserId, 7)); // a page without its site: asked, as before
            Assert.Equal(new[] { "enabled" }, _service.Calls);
        }

        private static PageState Page(string portability) =>
            new() { Site = new Site { Settings = new Dictionary<string, string> { [HostSwitches.SettingName(HostSwitches.Portability)] = portability } } };

        /// <summary>Records what the section asks; portability answers "off", so nothing more is rendered or asked.</summary>
        private sealed class CountingPortabilityService : IPortabilityService
        {
            public List<string> Calls { get; } = new();

            public Task<bool> IsEnabledAsync()
            {
                Calls.Add("enabled");
                return Task.FromResult(false);
            }

            public Task<List<PortabilityPackageView>> ListAsync()
            {
                Calls.Add("list");
                return Task.FromResult(new List<PortabilityPackageView>());
            }

            public Task<PortabilityPackageView> RequestConfigAsync(PortabilityConfigRequest request) => throw new NotSupportedException();

            public Task<PortabilityPackageView> RequestSubjectAsync(PortabilitySubjectRequest request) => throw new NotSupportedException();

            public Task<PortabilityPackageView> RequestMineAsync() => throw new NotSupportedException();

            public Task<PortabilityImportResult> ImportAsync(string json, ConflictStrategy strategy, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
}
