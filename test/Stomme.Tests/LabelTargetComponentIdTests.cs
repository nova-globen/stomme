using System.Collections.Generic;
using System.Threading.Tasks;
using Bunit;
using Stomme.Modules.Controls;
using Stomme.Services;
using Stomme.Tests.Help;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>LabelTargetGateTests</c> accepts a component's <c>Id</c> as a label's target, so
    /// the components a label names must put that id on the field itself. The user picker and the icon list took none.
    /// </summary>
    public sealed class LabelTargetComponentIdTests : BunitContext
    {
        public LabelTargetComponentIdTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLocalization();
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddSingleton(new Stomme.Shared.SiteState { Alias = new Stomme.Models.Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
        }

        [Fact]
        public void The_user_picker_puts_its_id_on_its_input()
        {
            var cut = Render<AutoComplete>(parameters => parameters
                .Add(picker => picker.Id, "user")
                .Add(picker => picker.OnSearch, _ => Task.FromResult(new Dictionary<string, string>())));

            Assert.Equal("input", cut.Find("#user").TagName.ToLowerInvariant());
        }

        [Fact]
        public void The_icon_list_puts_its_id_on_its_input()
        {
            var cut = Render<InputList>(parameters => parameters
                .Add(list => list.Id, "icon")
                .Add(list => list.DataList, new Dictionary<string, string> { ["oi oi-home"] = "Home" })
                .Add(list => list.ValueChanged, EventCallback.Factory.Create<string>(this, _ => { })));

            Assert.Equal("input", cut.Find("#icon").TagName.ToLowerInvariant());
        }
    }
}
