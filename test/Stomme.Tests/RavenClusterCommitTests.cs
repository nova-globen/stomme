using System;
using System.Threading.Tasks;
using Stomme.Persistence.RavenDb;
using Raven.Client.Exceptions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A cluster operation that timed out waiting for its commit notification is rechecked - done means done, not
    /// done means one more attempt - and any other failure propagates untouched.
    /// </summary>
    public sealed class RavenClusterCommitTests
    {
        private const string LostWakeup = "Waited for 00:00:15 for task with index 38787 to complete. Last commit index is: 38789. Number of errors is: 0";

        [Fact]
        public async Task A_successful_operation_is_not_rechecked()
        {
            var runs = 0;
            var checks = 0;

            await RavenClusterCommit.RunAsync(() => { runs++; return Task.CompletedTask; }, () => { checks++; return Task.FromResult(true); });

            Assert.Equal((1, 0), (runs, checks));
        }

        [Fact]
        public async Task A_timeout_whose_effect_is_present_succeeds_without_running_again()
        {
            var runs = 0;

            await RavenClusterCommit.RunAsync(
                () => { runs++; throw new RavenTimeoutException(LostWakeup); },
                () => Task.FromResult(true));

            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task A_timeout_whose_effect_is_absent_runs_once_more_and_a_second_failure_propagates()
        {
            var runs = 0;

            await Assert.ThrowsAsync<RavenTimeoutException>(() => RavenClusterCommit.RunAsync(
                () => { runs++; throw new RavenTimeoutException(LostWakeup); },
                () => Task.FromResult(false)));

            Assert.Equal(2, runs);
        }

        [Fact]
        public async Task A_timeout_whose_effect_is_absent_succeeds_when_the_second_run_does()
        {
            var runs = 0;

            await RavenClusterCommit.RunAsync(
                () => ++runs == 1 ? throw new RavenTimeoutException(LostWakeup) : Task.CompletedTask,
                () => Task.FromResult(false));

            Assert.Equal(2, runs);
        }

        [Fact]
        public async Task Any_other_failure_propagates_without_a_recheck()
        {
            var checks = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(() => RavenClusterCommit.RunAsync(
                () => throw new InvalidOperationException("refused"),
                () => { checks++; return Task.FromResult(true); }));

            Assert.Equal(0, checks);
        }

        [Fact]
        public void A_timeout_wrapped_by_another_exception_is_recognised()
        {
            Assert.True(RavenClusterCommit.IsCommitWaitTimeout(new RavenException("outer", new TimeoutException(LostWakeup))));
            Assert.False(RavenClusterCommit.IsCommitWaitTimeout(new RavenException("outer", new InvalidOperationException())));
        }
    }
}
