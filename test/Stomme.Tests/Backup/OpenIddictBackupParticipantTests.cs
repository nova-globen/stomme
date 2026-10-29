#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Modules.Backup.Application.Archive;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// The OpenIddict authority's registrations, on an installed host with <c>OpenIddict:Enabled</c>,
    /// SQLite and RavenDB, through OpenIddict's own managers: a client is registered and the part taken; afterwards a second
    /// client is registered and a token issued to the first; the restore brings back exactly the first client (its hashed
    /// secret intact) and NO token - a restored installation honours no token issued after the snapshot.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class OpenIddictBackupParticipantTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        protected OpenIddictBackupParticipantTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        [Fact]
        public async Task A_restore_brings_back_the_clients_and_revokes_every_token()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["OpenIddict"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var scope = new BackupScope("gate", await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<Stomme.Repository.ITenantRepository>().GetTenantsAsync()).Select(t => t.TenantId).ToList()));
                await InAuthorityAsync(app, (applications, _) => applications.CreateAsync(Client("kept-client")).AsTask());

                var participant = Participant(app);
                using var part = new MemoryStream();
                await participant.ExportAsync(new ArchivePartWriter(participant.Name, part), scope, CancellationToken.None);
                var bytes = part.ToArray();

                await InAuthorityAsync(app, async (applications, tokens) =>
                {
                    await applications.CreateAsync(Client("later-client"));
                    var kept = await applications.FindByClientIdAsync("kept-client");
                    await tokens.CreateAsync(new OpenIddictTokenDescriptor
                    {
                        ApplicationId = await applications.GetIdAsync(kept!), Subject = "1", Type = OpenIddictConstants.TokenTypeHints.AccessToken,
                        Status = OpenIddictConstants.Statuses.Valid, CreationDate = DateTimeOffset.UtcNow, ExpirationDate = DateTimeOffset.UtcNow.AddHours(1),
                    });
                });
                Assert.Equal(1L, await InAuthorityAsync(app, (_, tokens) => tokens.CountAsync().AsTask()));

                var verdict = await participant.ValidateAsync(new BytesPart(bytes), scope, CancellationToken.None);
                Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));

                // The same registrations filed under a tenant this installation does not have are refused.
                var misfiled = await Relabel(bytes, scope.TenantIds[0], 7);
                Assert.Contains((await participant.ValidateAsync(new BytesPart(misfiled), scope, CancellationToken.None)).Problems, p => p.Contains("not this installation", StringComparison.Ordinal));
                await Participant(app).ReplaceAsync(new BytesPart(bytes), scope, CancellationToken.None);

                var clients = await InAuthorityAsync(app, async (applications, _) =>
                {
                    var ids = new List<string>();
                    await foreach (var application in applications.ListAsync())
                    {
                        ids.Add((await applications.GetClientIdAsync(application))!);
                    }

                    return ids;
                });
                Assert.Equal(new[] { "kept-client" }, clients);
                Assert.Equal(0L, await InAuthorityAsync(app, (_, tokens) => tokens.CountAsync().AsTask()));
                Assert.True(await InAuthorityAsync(app, async (applications, _) =>
                    await applications.ValidateClientSecretAsync((await applications.FindByClientIdAsync("kept-client"))!, "kept-client-secret-2026")));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // An authority no request has reached yet has no store; the part must still be taken - provisioned first, then empty.
        [Fact]
        public async Task An_authority_never_used_is_provisioned_and_exported_empty()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["OpenIddict"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var scope = new BackupScope("gate", await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<Stomme.Repository.ITenantRepository>().GetTenantsAsync()).Select(t => t.TenantId).ToList()));
                var participant = Participant(app);
                using var part = new MemoryStream();
                await participant.ExportAsync(new ArchivePartWriter(participant.Name, part), scope, CancellationToken.None);
                Assert.True((await participant.ValidateAsync(new BytesPart(part.ToArray()), scope, CancellationToken.None)).IsValid);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<byte[]> Relabel(byte[] part, int from, int to)
        {
            using var output = new MemoryStream();
            await foreach (var (home, json) in Stomme.Infrastructure.Backup.BackupRecords.ReadAsync(new MemoryStream(part), CancellationToken.None))
            {
                await Stomme.Infrastructure.Backup.BackupRecords.WriteAsync(output, home, json == $"{{\"$tenant\":{from}}}" ? $"{{\"$tenant\":{to}}}" : json, CancellationToken.None);
            }

            return output.ToArray();
        }

        private static OpenIddictApplicationDescriptor Client(string clientId)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientSecret = clientId + "-secret-2026", DisplayName = clientId, ClientType = OpenIddictConstants.ClientTypes.Confidential,
            };
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
            return descriptor;
        }

        private static IBackupParticipant Participant(WebApplication app) =>
            app.Services.CreateScope().ServiceProvider.GetServices<IBackupParticipant>().Single(p => p.Name == OpenIddictBackupParticipant.PartName);

        private static Task<T> InAuthorityAsync<T>(WebApplication app, Func<IOpenIddictApplicationManager, IOpenIddictTokenManager, Task<T>> work) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await sp.GetRequiredService<IOpenIddictStoreProvisioner>().EnsureProvisionedAsync();
                return await work(sp.GetRequiredService<IOpenIddictApplicationManager>(), sp.GetRequiredService<IOpenIddictTokenManager>());
            });

        private static Task InAuthorityAsync(WebApplication app, Func<IOpenIddictApplicationManager, IOpenIddictTokenManager, Task> work) =>
            InAuthorityAsync(app, async (applications, tokens) => { await work(applications, tokens); return 0; });

        private sealed class BytesPart(byte[] bytes) : IBackupPartReader
        {
            public string PartName => OpenIddictBackupParticipant.PartName;

            public string SchemaVersion => "1";

            public long Length => bytes.Length;

            public Stream OpenRead() => new MemoryStream(bytes, writable: false);
        }
    }

    public sealed class SqliteOpenIddictBackupParticipantTests(ITestOutputHelper output) : OpenIddictBackupParticipantTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenOpenIddictBackupParticipantTests(ITestOutputHelper output) : OpenIddictBackupParticipantTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
