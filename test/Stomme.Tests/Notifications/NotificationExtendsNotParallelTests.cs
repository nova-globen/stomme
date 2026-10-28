using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// BL-78 step 4 - <c>NotificationExtendsNotParallelTests</c> (D100 points 1-3): the Notifications module EXTENDS the
    /// inherited notification path, it does not run a parallel one. BEHAVIOURAL: an e-mail dispatch's row is returned by
    /// the EXISTING queue predicate (<c>INotificationRepository.GetNotificationsAsync(siteId, -1, -1)</c>) and marked
    /// delivered by the EXISTING <see cref="NotificationJob"/> against an in-test loopback SMTP responder (F7: no SMTP fake
    /// existed, <c>NotificationJob</c> is <c>IsEnabled=false</c> and needs the site's SMTP settings, so its
    /// <c>ExecuteJobAsync</c> is invoked directly); an in-app row is never in that queue. REFLECTIVE: no module type
    /// reaches an SMTP client, no second notification store exists, no <c>DbContext</c>/<c>DbSet</c> under the module -
    /// each scanner validated both directions against planted types.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class NotificationExtendsNotParallelTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public NotificationExtendsNotParallelTests(ITestOutputHelper output) => _output = output;

        // ---- behavioural --------------------------------------------------------------------------------------

        [Fact]
        public async Task An_email_dispatch_rides_the_existing_queue_and_the_existing_job_delivers_it_while_in_app_never_enters_the_queue()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            // The hosted services start EXCEPT the jobs: this test runs both jobs itself, so the per-minute dispatch job
            // must not race it.
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            await using var smtp = new LoopbackSmtpResponder();
            try
            {
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var marker = "extends-" + Guid.NewGuid().ToString("N");

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    foreach (var (name, value) in new[]
                    {
                        (NotificationSiteSettingNames.ExternalRecipients, "True"),
                        ("SMTPHost", "127.0.0.1"),
                        ("SMTPPort", smtp.Port.ToString(CultureInfo.InvariantCulture)),
                        ("SMTPSender", "no-reply@gate.test"),
                        ("SMTPSSL", "None"),
                    })
                    {
                        await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = name, SettingValue = value, IsPrivate = true });
                    }

                    var channels = sp.GetRequiredService<INotificationChannelRepository>();
                    await channels.AddAsync(NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, alias.SiteId, ChannelKind.Email, "Queue",
                        new TransportSettings(ChannelKind.Email, null, null, [])));
                    await channels.AddAsync(NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, alias.SiteId, ChannelKind.InApp, "Inbox",
                        new TransportSettings(ChannelKind.InApp, null, null, [])));
                    return true;
                });

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var dispatcher = sp.GetRequiredService<INotificationDispatcher>();
                    await dispatcher.DispatchAsync(new NotificationDispatchRequest("generic.message",
                        [new DispatchRecipient(NotificationRecipientKind.Email, "ops@example.com")], [NotificationChannelKind.Email],
                        [new DispatchParameter("subject", "Mail " + marker), new DispatchParameter("body", "Queued body")]));
                    await dispatcher.DispatchAsync(new NotificationDispatchRequest("generic.message",
                        [new DispatchRecipient(NotificationRecipientKind.User, hostUserId.ToString(CultureInfo.InvariantCulture))], [NotificationChannelKind.InApp],
                        [new DispatchParameter("subject", "Inbox " + marker), new DispatchParameter("body", "Inbox body")]));
                    return true;
                });

                _output.WriteLine("dispatch job: " + await InstalledNotificationDispatchJobTests.RunJobAsync(app));

                // The EXISTING queue predicate returns the e-mail row - and never the in-app row.
                var queue = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(alias.SiteId, -1, -1)).ToList());
                _output.WriteLine("queue: " + string.Join(" | ", queue.Select(row => $"{row.NotificationId}:{row.Subject}:{row.ToEmail}:delivered={row.IsDelivered}")));
                var mail = Assert.Single(queue, row => row.Subject == "Mail " + marker);
                Assert.Equal("ops@example.com", mail.ToEmail);
                Assert.False(mail.IsDelivered);
                Assert.DoesNotContain(queue, row => row.Subject == "Inbox " + marker);

                // The EXISTING NotificationJob sends it and marks it delivered.
                var notes = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var tenant = await sp.GetRequiredService<ITenantManager>().GetTenantAsync();
                    using var jobScope = app.Services.CreateScope();
                    jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    return await new NotificationJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider);
                });
                _output.WriteLine("notification job: " + notes);

                var received = Assert.Single(smtp.Messages);
                Assert.Contains("Mail " + marker, received.Data);
                Assert.Contains("ops@example.com", received.Recipients.Single());

                var delivered = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<INotificationRepository>().GetNotificationAsync(mail.NotificationId));
                Assert.True(delivered.IsDelivered);
                Assert.NotNull(delivered.DeliveredOn);

                var queueAfter = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(alias.SiteId, -1, -1)).ToList());
                Assert.DoesNotContain(queueAfter, row => row.Subject == "Mail " + marker);

                var inbox = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(alias.SiteId, -1, hostUserId)).ToList());
                Assert.Single(inbox, row => row.Subject == "Inbox " + marker && row.IsDelivered);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- reflective -----------------------------------------------------------------------------------------

        [Fact]
        public void No_module_type_references_an_smtp_client()
        {
            Assert.Empty(SmtpReferences(ModuleTypes()));
            Assert.Empty(ForbiddenIdentifiers(ModuleSources(), "SmtpClient", "MailKit"));
        }

        [Fact]
        public void There_is_no_second_notification_store()
        {
            Assert.Empty(SecondStores(ModuleTypes()));
        }

        [Fact]
        public void There_is_no_DbContext_or_DbSet_under_the_module()
        {
            Assert.Empty(DbReferences(ModuleTypes()));
            Assert.Empty(ForbiddenIdentifiers(ModuleSources(), "DbContext", "DbSet"));
        }

        [Fact]
        public void The_reflective_scanners_catch_planted_violations_and_pass_clean_types()
        {
            // Known-dirty.
            Assert.NotEmpty(SmtpReferences([typeof(PlantedSmtpUser)]));
            Assert.NotEmpty(SecondStores([typeof(IPlantedNotificationStore)]));
            Assert.NotEmpty(SecondStores([typeof(IPlantedRowReader)]));
            Assert.NotEmpty(SecondStores([typeof(PlantedSecondRepository)]));
            Assert.NotEmpty(DbReferences([typeof(PlantedContext)]));
            Assert.NotEmpty(DbReferences([typeof(PlantedDbSetHolder)]));
            Assert.NotEmpty(ForbiddenIdentifiers(["class P { System.Net.Mail.SmtpClient c; }"], "SmtpClient", "MailKit"));
            Assert.NotEmpty(ForbiddenIdentifiers(["using MailKit.Net.Smtp; class P { }"], "SmtpClient", "MailKit"));
            Assert.NotEmpty(ForbiddenIdentifiers(["class P : Microsoft.EntityFrameworkCore.DbContext { }"], "DbContext", "DbSet"));

            // Known-clean - including a COMMENT that names the forbidden types, which is not a reference.
            Assert.Empty(SmtpReferences([typeof(PlantedCleanType)]));
            Assert.Empty(SecondStores([typeof(PlantedCleanType)]));
            Assert.Empty(DbReferences([typeof(PlantedCleanType)]));
            Assert.Empty(ForbiddenIdentifiers(["// no SmtpClient, no DbContext, no DbSet, no MailKit here\nclass P { }"], "SmtpClient", "MailKit", "DbContext", "DbSet"));

            // Vacuity: the module scan sees the module.
            Assert.Contains(ModuleTypes(), type => type == typeof(NotificationDispatcher));
            Assert.True(ModuleSources().Count() > 10, "The module source scan found too few files.");
        }

        // ---- scanners ------------------------------------------------------------------------------------------

        private static readonly string[] ModuleNamespaces = ["Stomme.Modules.Notifications", "Stomme.Notifications.Persistence"];

        internal static IReadOnlyList<Type> ModuleTypes() =>
            typeof(NotificationDispatcher).Assembly.GetTypes()
                .Where(type => type.Namespace is { } ns && ModuleNamespaces.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal)))
                .ToList();

        private static IEnumerable<string> ModuleSources()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return Directory.GetFiles(Path.Combine(directory!.FullName, "src", "Stomme.Server", "Modules", "Notifications"), "*.cs", SearchOption.AllDirectories)
                .Select(System.IO.File.ReadAllText)
                .ToList();
        }

        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static IEnumerable<(string Where, Type Type)> SignatureTypes(Type type)
        {
            foreach (var field in type.GetFields(AllMembers))
            {
                yield return ($"{type.Name}.{field.Name}", field.FieldType);
            }

            foreach (var property in type.GetProperties(AllMembers))
            {
                yield return ($"{type.Name}.{property.Name}", property.PropertyType);
            }

            foreach (var method in type.GetMethods(AllMembers).Cast<MethodBase>().Concat(type.GetConstructors(AllMembers)))
            {
                foreach (var parameter in method.GetParameters())
                {
                    yield return ($"{type.Name}.{method.Name}({parameter.Name})", parameter.ParameterType);
                }

                if (method is MethodInfo info)
                {
                    yield return ($"{type.Name}.{method.Name} return", info.ReturnType);
                }
            }

            if (type.BaseType is not null)
            {
                yield return ($"{type.Name} base", type.BaseType);
            }
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.HasElementType)
            {
                foreach (var inner in Flatten(type.GetElementType()!))
                {
                    yield return inner;
                }
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
                {
                    yield return argument;
                }
            }
        }

        private static IEnumerable<(string Where, Type Type)> Referenced(IEnumerable<Type> types) =>
            types.SelectMany(SignatureTypes).SelectMany(entry => Flatten(entry.Type).Select(inner => (entry.Where, inner)));

        internal static IReadOnlyList<string> SmtpReferences(IEnumerable<Type> types) =>
            Referenced(types)
                .Where(entry => entry.Type.FullName is { } name
                    && (name.StartsWith("MailKit", StringComparison.Ordinal) || name == "System.Net.Mail.SmtpClient"))
                .Select(entry => $"{entry.Where}: {entry.Type.FullName}")
                .ToList();

        internal static IReadOnlyList<string> SecondStores(IEnumerable<Type> types)
        {
            var hits = new List<string>();
            foreach (var type in types)
            {
                if (type.IsInterface && type.Name.StartsWith('I') && type.Name.EndsWith("Store", StringComparison.Ordinal))
                {
                    hits.Add("store interface " + type.FullName);
                }

                if (!type.IsInterface && typeof(INotificationRepository).IsAssignableFrom(type))
                {
                    hits.Add("second INotificationRepository implementation " + type.FullName);
                }

                if (type.IsInterface && SignatureTypes(type).SelectMany(entry => Flatten(entry.Type)).Any(inner => inner == typeof(Notification)))
                {
                    hits.Add("interface over the Notification row " + type.FullName);
                }
            }

            return hits;
        }

        internal static IReadOnlyList<string> DbReferences(IEnumerable<Type> types)
        {
            var hits = types.Where(type => typeof(DbContext).IsAssignableFrom(type)).Select(type => "DbContext " + type.FullName).ToList();
            hits.AddRange(Referenced(types)
                .Where(entry => entry.Type.IsGenericType && entry.Type.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(entry => $"DbSet {entry.Where}"));
            return hits;
        }

        /// <summary>Identifier tokens only - never comments, never documentation trivia.</summary>
        internal static IReadOnlyList<string> ForbiddenIdentifiers(IEnumerable<string> sources, params string[] names) =>
            sources
                .SelectMany(source => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens())
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && names.Contains(token.ValueText, StringComparer.Ordinal))
                .Select(token => token.ValueText)
                .ToList();

        // ---- planted types (scanner validation only) ------------------------------------------------------------

        private sealed class PlantedSmtpUser
        {
#pragma warning disable CS0169, SYSLIB0006
            private System.Net.Mail.SmtpClient? _client;
#pragma warning restore CS0169, SYSLIB0006
        }

        private interface IPlantedNotificationStore
        {
        }

        private interface IPlantedRowReader
        {
            Task<Notification> ReadAsync(int id);
        }

        private abstract class PlantedSecondRepository : INotificationRepository
        {
            public abstract Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId);
            public abstract Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId, int count, bool isRead);
            public abstract Task<int> GetNotificationCountAsync(int siteId, int fromUserId, int toUserId, bool isRead);
            public abstract Task<Notification> AddNotificationAsync(Notification notification);
            public abstract Task<Notification> UpdateNotificationAsync(Notification notification);
            public abstract Task<Notification> GetNotificationAsync(int notificationId);
            public abstract Task<Notification> GetNotificationAsync(int notificationId, bool tracking);
            public abstract Task DeleteNotificationAsync(int notificationId);
            public abstract Task<int> DeleteNotificationsAsync(int siteId, int age);
        }

        private sealed class PlantedContext : DbContext
        {
        }

        private sealed class PlantedDbSetHolder
        {
            public DbSet<Notification>? Rows { get; set; }
        }

        private sealed class PlantedCleanType
        {
            public INotificationDispatcher? Dispatcher { get; set; }
        }
    }

    /// <summary>
    /// A minimal SMTP responder on a raw loopback <see cref="TcpListener"/> (no package, nothing through the licence gate):
    /// 220 greeting, 250 to EHLO/HELO/MAIL/RCPT/RSET/NOOP, 354 to DATA then 250 after the terminating dot, 221 to QUIT.
    /// Records each message's envelope recipients and DATA. No TLS, no AUTH - the site is configured <c>SMTPSSL=None</c>
    /// with no credentials.
    /// </summary>
    internal sealed class LoopbackSmtpResponder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackSmtpResponder()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<ReceivedMail> Messages { get; } = new();

        public sealed record ReceivedMail(IReadOnlyList<string> Recipients, string Data);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => SessionAsync(client));
            }
        }

        private async Task SessionAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 localhost loopback responder ready");

                    var recipients = new List<string>();
                    while (!_stop.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(_stop.Token);
                        if (line is null)
                        {
                            return;
                        }

                        var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                        switch (verb)
                        {
                            case "EHLO":
                            case "HELO":
                                await writer.WriteLineAsync("250 localhost");
                                break;
                            case "RCPT":
                                recipients.Add(line);
                                await writer.WriteLineAsync("250 OK");
                                break;
                            case "DATA":
                                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                                var data = new StringBuilder();
                                string? dataLine;
                                while ((dataLine = await reader.ReadLineAsync(_stop.Token)) is not null && dataLine != ".")
                                {
                                    data.AppendLine(dataLine);
                                }

                                Messages.Enqueue(new ReceivedMail(recipients.ToList(), data.ToString()));
                                recipients.Clear();
                                await writer.WriteLineAsync("250 OK queued");
                                break;
                            case "QUIT":
                                await writer.WriteLineAsync("221 bye");
                                return;
                            default:
                                await writer.WriteLineAsync("250 OK");
                                break;
                        }
                    }
                }
                catch (Exception)
                {
                    // a broken session is reported by the asserting test, not here
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // stopping
            }

            _stop.Dispose();
        }
    }
}
