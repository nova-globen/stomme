#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Modules.Dashboards.Application;
using Stomme.Security.Abstractions;
using FluentValidation;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>BL-96 increment 9 - the predecessor platform's DashboardCommandTests (10) by name over the port's application layer, through the module's own startup on the File store; plus the port's own: a board belongs to the request's site.</summary>
    public sealed class DashboardCommandTests
    {
        [Fact]
        public async Task Create_ThenGet_ReturnsDraft()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();

            var id = await harness.SendAsync(new CreateDashboardCommand("Sales", 8, 50));

            var view = await harness.SendAsync(new GetDashboardQuery(id));
            Assert.NotNull(view);
            Assert.Equal(("Sales", DashboardStatus.Draft, 8, 50, DashboardsApplicationHarness.Start), (view.Name, view.Status, view.Columns, view.RowHeightPx, view.CreatedUtc));
        }

        [Fact]
        public async Task Create_WithEmptyName_FailsValidation()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();

            await Assert.ThrowsAsync<ValidationException>(() => harness.SendAsync(new CreateDashboardCommand("")));
        }

        [Fact]
        public async Task Update_RenamesDashboard()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var id = await harness.CreateBoardAsync("Old");
            harness.Clock.Advance(TimeSpan.FromMinutes(1));

            await harness.SendAsync(new UpdateDashboardCommand(id, "New"));

            var view = (await harness.SendAsync(new GetDashboardQuery(id)))!;
            Assert.Equal(("New", DashboardsApplicationHarness.Start.AddMinutes(1)), (view.Name, view.UpdatedUtc));
        }

        [Fact]
        public async Task Publish_TransitionsToPublished()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var id = await harness.CreateBoardAsync("Ops");

            await harness.SendAsync(new PublishDashboardCommand(id));

            Assert.Equal(DashboardStatus.Published, (await harness.SendAsync(new GetDashboardQuery(id)))!.Status);
        }

        [Fact]
        public async Task Clone_CreatesIndependentDraftCopy()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var id = await harness.CreateBoardAsync("Source");
            await harness.SendAsync(new AddWidgetCommand(id, "Grid", WidgetKind.Grid, Guid.NewGuid(), new[] { "x" }, 0, 0, 4, 3));
            await harness.SendAsync(new PublishDashboardCommand(id));

            var cloneId = await harness.SendAsync(new CloneDashboardCommand(id, "Copy of Source"));

            Assert.NotEqual(id, cloneId);
            var clone = (await harness.SendAsync(new GetDashboardQuery(cloneId)))!;
            Assert.Equal(("Copy of Source", DashboardStatus.Draft), (clone.Name, clone.Status)); // a clone is a draft even when its source was published
            var source = (await harness.SendAsync(new GetDashboardQuery(id)))!;
            Assert.NotEqual(source.Widgets.Single().Id, clone.Widgets.Single().Id);
        }

        [Fact]
        public async Task Delete_RemovesDashboard()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var id = await harness.CreateBoardAsync("Temp");

            await harness.SendAsync(new DeleteDashboardCommand(id));

            Assert.Null(await harness.SendAsync(new GetDashboardQuery(id)));
            await harness.SendAsync(new DeleteDashboardCommand(id)); // idempotent
        }

        [Fact]
        public async Task List_OrdersByName()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            await harness.CreateBoardAsync("Zebra");
            await harness.CreateBoardAsync("alpha");

            var list = await harness.SendAsync(new ListDashboardsQuery());

            Assert.Equal(new[] { "alpha", "Zebra" }, list.Select(dashboard => dashboard.Name));
        }

        public static IEnumerable<object[]> ManageCommands() => new[]
        {
            new object[] { typeof(CreateDashboardCommand) },
            new object[] { typeof(UpdateDashboardCommand) },
            new object[] { typeof(CloneDashboardCommand) },
            new object[] { typeof(DeleteDashboardCommand) },
        };

        [Theory]
        [MemberData(nameof(ManageCommands))]
        public void ManageCommand_RequiresManagePermission(Type commandType) =>
            Assert.Contains(commandType.GetCustomAttributes(typeof(RequiresAttribute), inherit: true).Cast<RequiresAttribute>(), requires => requires.Permission == DashboardPermissions.Manage);

        [Fact]
        public void PublishCommand_RequiresPublishPermission() =>
            Assert.Contains(typeof(PublishDashboardCommand).GetCustomAttributes(typeof(RequiresAttribute), inherit: true).Cast<RequiresAttribute>(), requires => requires.Permission == DashboardPermissions.Publish);

        [Theory]
        [InlineData(typeof(ListDashboardsQuery))]
        [InlineData(typeof(GetDashboardQuery))]
        [InlineData(typeof(GetWidgetDataQuery))]
        public void Query_RequiresViewPermission(Type queryType) =>
            Assert.Contains(queryType.GetCustomAttributes(typeof(RequiresAttribute), inherit: true).Cast<RequiresAttribute>(), requires => requires.Permission == DashboardPermissions.View);

        [Fact]
        public async Task A_board_belongs_to_the_requests_site_and_none_is_made_without_one()
        {
            await using var noSite = await DashboardsApplicationHarness.StartAsync(withSite: false);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => noSite.CreateBoardAsync("Orphan"));
            Assert.Contains("site", ex.Message);
            Assert.Empty(await noSite.SendAsync(new ListDashboardsQuery()).ContinueWith(t => t.IsFaulted ? Array.Empty<DashboardView>() : t.Result.ToArray()));
        }
    }
}
