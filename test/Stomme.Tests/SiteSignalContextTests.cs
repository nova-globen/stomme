#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Dashboards;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// An in-process site signal calls each subscriber in the execution context IT subscribed in, never
    /// the raiser's (<see cref="Stomme.Transport.Realtime.SiteSignal{TMessage}"/>). A circuit's page reads its user through
    /// the ambient HttpContext, which rides that context: invoked on the raiser's thread, the page's work ran as the user whose
    /// request raised the message - driven with two signed-in editors (one editor's panel re-read the lock as the other and
    /// said she had lost it), and the same path re-read a dashboard viewer's widgets as whoever refreshed the dataset. The
    /// ambient user is modelled here as an AsyncLocal, which is what IHttpContextAccessor is. Mutation-checked: invoking the
    /// handlers directly on the raiser's thread fails the first two cases.
    /// </summary>
    public sealed class SiteSignalContextTests
    {
        private static readonly AsyncLocal<string?> AmbientUser = new();

        [Fact]
        public async Task A_collaboration_notice_reaches_each_page_as_that_pages_own_user()
        {
            var signal = new CollaborationSignal();
            var seen = new List<string>();
            await SubscribeAsAsync("ada", () => signal.Changed += (_, _) => seen.Add("ada's page ran as " + AmbientUser.Value));
            await SubscribeAsAsync("cy", () => signal.Changed += (_, _) => seen.Add("cy's page ran as " + AmbientUser.Value));

            await RunAsAsync("bo", () => signal.Notify("1:1", new CollaborationNotice("pipeline", "p1", CollaborationNoticeKinds.Saved, 2)));

            Assert.Equal(new[] { "ada's page ran as ada", "cy's page ran as cy" }, seen);
        }

        [Fact]
        public async Task A_dataset_change_reaches_a_dashboard_viewer_as_the_viewer_not_as_the_refresher()
        {
            var signal = new DashboardDataSignal();
            string? ranAs = "nobody";
            await SubscribeAsAsync("viewer", () => signal.DatasetChanged += (_, _) => ranAs = AmbientUser.Value);

            await RunAsAsync("refresher", () => signal.NotifyDatasetChanged("1:1", new DashboardDatasetChange(Guid.NewGuid(), "Refreshed")));

            Assert.Equal("viewer", ranAs);
        }

        [Fact]
        public void A_page_whose_handler_throws_neither_stops_the_others_nor_fails_the_raiser()
        {
            var signal = new CollaborationSignal();
            var told = 0;
            signal.Changed += (_, _) => throw new InvalidOperationException("a broken page");
            signal.Changed += (_, _) => told++;

            signal.Notify("1:1", new CollaborationNotice("dashboard", "d1", CollaborationNoticeKinds.Lock, 1));

            Assert.Equal(1, told);
        }

        [Fact]
        public void An_unsubscribed_page_is_told_nothing_more()
        {
            var signal = new DashboardDataSignal();
            var told = 0;
            Action<string, DashboardDatasetChange> handler = (_, _) => told++;
            signal.DatasetChanged += handler;
            signal.NotifyDatasetChanged("1:1", new DashboardDatasetChange(Guid.NewGuid(), "Refreshed"));
            signal.DatasetChanged -= handler;
            signal.NotifyDatasetChanged("1:1", new DashboardDatasetChange(Guid.NewGuid(), "Refreshed"));

            Assert.Equal((1, 0), (told, signal.SubscriberCount));
        }

        // Each "page" subscribes on its own flow with its own ambient user, as a circuit does.
        private static Task SubscribeAsAsync(string user, Action subscribe) => Task.Run(() =>
        {
            AmbientUser.Value = user;
            subscribe();
        });

        private static Task RunAsAsync(string user, Action raise) => Task.Run(() =>
        {
            AmbientUser.Value = user;
            raise();
        });
    }
}
