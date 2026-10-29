using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The container's HEALTHCHECK is LIVENESS (<c>/healthz</c>) and never readiness.
    /// </summary>
    /// <remarks>
    /// Measured on a genuinely uninstalled host running the Production environment: <c>/healthz</c> = 200 "alive", <c>/readyz</c> = 503.
    /// A readiness HEALTHCHECK would therefore restart-loop every fresh deployment before its web installer could run. The
    /// probe command itself was driven in bash against a live 200 (exit 0), a live 503 (exit 1) and a closed port (exit 1);
    /// no Docker exists on the build machine, so the image wiring is held to its source here.
    /// </remarks>
    public class DockerHealthcheckContractTests
    {
        [Fact]
        public void The_Dockerfile_checks_liveness_at_healthz_and_never_readyz()
        {
            var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot(), "setup", "Dockerfile"));
            Assert.True(HealthcheckTargetsLivenessOnly(dockerfile), "setup/Dockerfile must carry a HEALTHCHECK against /healthz and never /readyz");
        }

        [Theory]
        [InlineData("FROM x\nENTRYPOINT [\"run\"]", false)]                                              // no HEALTHCHECK at all
        [InlineData("HEALTHCHECK CMD bash -c 'printf \"GET /readyz HTTP/1.1\"'", false)]                // readiness
        [InlineData("HEALTHCHECK --interval=30s \\\n  CMD bash -c 'printf \"GET /healthz HTTP/1.1\"'", true)]
        public void The_contract_check_itself_can_go_red(string dockerfile, bool expected)
        {
            // Validated both ways: an oracle that cannot fail proves nothing.
            Assert.Equal(expected, HealthcheckTargetsLivenessOnly(dockerfile));
        }

        /// <summary>The HEALTHCHECK instruction, with its line continuations joined, names /healthz and not /readyz.</summary>
        private static bool HealthcheckTargetsLivenessOnly(string dockerfile)
        {
            var joined = Regex.Replace(dockerfile, @"\\\r?\n", " ");
            var healthcheck = joined.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith("HEALTHCHECK", StringComparison.Ordinal));
            return healthcheck is not null
                   && healthcheck.Contains("/healthz", StringComparison.Ordinal)
                   && !healthcheck.Contains("/readyz", StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
