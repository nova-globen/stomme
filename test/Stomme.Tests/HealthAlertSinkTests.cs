#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Models.Notifications;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Notifications;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What a health alert is allowed to say, and to whom (D104 items 6 and 7).
    /// </summary>
    public class HealthAlertSinkTests
    {
        private readonly ITestOutputHelper _output;

        public HealthAlertSinkTests(ITestOutputHelper output) => _output = output;

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        private const string Secret = "server=db01;user=app;password=hunter2";

        private sealed class FakeDispatcher : INotificationDispatcher
        {
            public List<NotificationDispatchRequest> Sent { get; } = new();

            public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
            {
                Sent.Add(request);
                return Task.FromResult(Guid.NewGuid());
            }
        }

        [Fact]
        public async Task An_alert_carries_the_exception_type_and_never_the_exception_message()
        {
            var dispatcher = new FakeDispatcher();
            var sink = new NotificationHealthAlertSink(
                new StubUserRoles(userIds: new[] { 5, 5, 9 }), NullLogger<NotificationHealthAlertSink>.Instance, dispatcher);

            await sink.RaisedAsync(Notice(HealthProbeScope.Site, siteId: 7));

            var request = Assert.Single(dispatcher.Sent);
            var parameters = request.Parameters.ToDictionary(p => p.Name, p => p.Value);
            _output.WriteLine("[parameters] " + string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}")));

            Assert.Equal("SocketException", parameters["exceptionType"]);
            Assert.Equal(nameof(HealthState.Unhealthy), parameters["state"]);

            // THE assertion: nothing anywhere in the dispatch may contain the raw message.
            var everything = string.Join("|", parameters.Values) + "|" + request.TemplateKey + "|" + request.Topic;
            Assert.DoesNotContain("hunter2", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("db01", everything, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Recipients_are_de_duplicated_and_addressed_by_user_id()
        {
            var dispatcher = new FakeDispatcher();
            var sink = new NotificationHealthAlertSink(
                new StubUserRoles(userIds: new[] { 5, 5, 9 }), NullLogger<NotificationHealthAlertSink>.Instance, dispatcher);

            await sink.RaisedAsync(Notice(HealthProbeScope.Site, siteId: 7));

            var request = Assert.Single(dispatcher.Sent);
            _output.WriteLine("[recipients] " + string.Join(", ", request.Recipients.Select(r => $"{r.Kind}:{r.Value}")));

            // One person holding two roles is one recipient - and never an e-mail address.
            Assert.Equal(2, request.Recipients.Count);
            Assert.All(request.Recipients, recipient => Assert.Equal(NotificationRecipientKind.User, recipient.Kind));
            Assert.Contains(request.Recipients, recipient => recipient.Value == "5");
            Assert.Contains(request.Recipients, recipient => recipient.Value == "9");
        }

        [Fact]
        public async Task A_shared_dependency_goes_to_the_operator_and_a_site_dependency_to_the_site()
        {
            // The refinement of D104 item 7: telling every site's administrators that the SHARED database is down
            // would be one alert per site for one fault.
            var roles = new StubUserRoles(userIds: new[] { 1 });
            var dispatcher = new FakeDispatcher();
            var sink = new NotificationHealthAlertSink(roles, NullLogger<NotificationHealthAlertSink>.Instance, dispatcher);

            await sink.RaisedAsync(Notice(HealthProbeScope.Site, siteId: 7));
            await sink.RaisedAsync(Notice(HealthProbeScope.Installation, siteId: 0));

            _output.WriteLine("[asked for] " + string.Join(", ", roles.Asked.Select(a => $"{a.Role}@site {a.SiteId}")));

            // The site's operators for the site's fault - its administrators AND the Host Users, since a fresh install's site
            // has no administrator at all (BL-177) - and the Host Users alone for the shared one.
            Assert.Equal(3, roles.Asked.Count);
            Assert.Equal(("Administrators", 7), roles.Asked[0]);
            Assert.Equal(("Host Users", 7), roles.Asked[1]);
            Assert.Equal(("Host Users", -1), roles.Asked[2]);
        }

        [Fact]
        public async Task With_no_dispatcher_registered_the_sink_is_silent_rather_than_broken()
        {
            // Health depends on the producer PORT, never on the Notifications module: a host composed without one
            // must still MONITOR.
            var sink = new NotificationHealthAlertSink(
                new StubUserRoles(userIds: new[] { 1 }), NullLogger<NotificationHealthAlertSink>.Instance, dispatcher: null);

            await sink.RaisedAsync(Notice(HealthProbeScope.Site, siteId: 7));
            await sink.RecoveredAsync(Notice(HealthProbeScope.Site, siteId: 7));
        }

        [Fact]
        public async Task The_no_op_sink_tells_nobody()
        {
            var sink = new NoOpHealthAlertSink();
            await sink.RaisedAsync(Notice(HealthProbeScope.Site, siteId: 7));
            await sink.RecoveredAsync(Notice(HealthProbeScope.Site, siteId: 7));
        }

        private static HealthAlertNotice Notice(HealthProbeScope scope, int siteId)
        {
            var subject = HealthSubject.For("stomme", "store");
            var row = HealthAlertStateAggregate.For(Tenant, scope, siteId, subject, DateTime.UtcNow);
            row.Observe(HealthState.Unhealthy, "PostgreSQL is unreachable: " + Secret, DateTime.UtcNow);

            var result = new HealthProbeResult(
                "store", "Module Store", HealthState.Unhealthy,
                "PostgreSQL is unreachable: " + Secret,
                TimeSpan.FromMilliseconds(42),
                new Dictionary<string, string> { ["latencyMs"] = "42", ["exceptionType"] = "SocketException" });

            return new HealthAlertNotice(scope, siteId, subject, HealthState.Unhealthy, HealthState.Healthy, result, row);
        }

        /// <summary>
        /// Answers the ONE query the sink makes, and records what it was asked. Every other member throws: a
        /// stub that silently answers a call the subject should never make hides exactly the defect worth
        /// catching.
        /// </summary>
        private sealed class StubUserRoles : Stomme.Repository.IUserRoleRepository
        {
            private readonly int[] _userIds;

            public StubUserRoles(int[] userIds) => _userIds = userIds;

            public List<(string Role, int SiteId)> Asked { get; } = new();

            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(string roleName, int siteId)
            {
                Asked.Add((roleName, siteId));
                return Task.FromResult<IEnumerable<Stomme.Models.UserRole>>(
                    _userIds.Select(id => new Stomme.Models.UserRole { UserId = id }).ToList());
            }

            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(int siteId) => throw new NotSupportedException();
            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(int userId, int siteId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> AddUserRoleAsync(Stomme.Models.UserRole userRole) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> UpdateUserRoleAsync(Stomme.Models.UserRole userRole) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userRoleId, bool tracking) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userId, int roleId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking) => throw new NotSupportedException();
            public Task DeleteUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task DeleteUserRolesAsync(int userId) => throw new NotSupportedException();
        }
    }
}