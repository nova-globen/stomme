#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Infrastructure.Backup;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Erasure.Participants
{
    /// <summary>
    /// The authorship stamps (BL-116 increment 5, found by increment 3's gate): <c>CreatedBy</c> / <c>ModifiedBy</c> /
    /// <c>DeletedBy</c> hold the author's USERNAME on every auditable framework row - the site's pages, modules, files,
    /// settings, roles - and after every other participant has run they still name the person. Once the account is
    /// gone from the tenant, the username is replaced by an unlinkable placeholder on every row that carries it, in
    /// the tenant's stores and the installation-wide (master) ones; the rows and their dates stay (Anonymise).
    /// </summary>
    /// <remarks>
    /// <para>Discovery is reflective over the seam's closed store contracts - every <c>IXxxStore : IRepository&lt;TAgg,int&gt;</c>
    /// whose aggregate carries a string stamp - so a new framework store is covered with no edit here; the gate that
    /// would notice a missed one is <c>SubjectErasureCoverageGateTests</c>. The rows are read through the session
    /// (D19: a store tracks what it loads) and written through the store one distinct row at a time - the per-write
    /// hazard is one KEY attached twice, which distinct rows cannot produce. The audit stamping is suppressed for the
    /// write so the placeholder is what lands on every provider (EF would otherwise re-stamp <c>ModifiedBy</c> with the
    /// job's empty name). Exact match on the username: the stamp is written from the signed-in name, which is the
    /// username as stored.</para>
    /// <para>The guard: a tenant's users are shared by its sites and an erasure is per site, so the stamps are touched
    /// only when NO account with that username remains in the tenant - the erasure that removes the person's last site
    /// is the one that anonymises them; earlier receipts report 0, truthfully.</para>
    /// </remarks>
    public sealed class AuthorshipStampsErasureParticipant(IServiceProvider services, IFrameworkStoreSessionFactory tenantSessions, IMasterStoreSessionFactory masterSessions) : ISubjectErasureParticipant
    {
        public const string Store = "Authorship stamps";

        /// <summary>What an erased author's name becomes. Not a possible username (brackets), so it never collides.</summary>
        public const string Placeholder = "[erased]";

        private static readonly string[] StampNames = { nameof(IAuditable.CreatedBy), nameof(IAuditable.ModifiedBy), nameof(IDeletable.DeletedBy) };

        /// <summary>The stores bound to MasterDBContext (FrameworkStoreServiceExtensions), read through the master session factory.</summary>
        private static readonly HashSet<string> MasterStores = new(StringComparer.Ordinal) { "IMasterSettingStore", "IJobStore", "IJobLogStore", "IThemeStore", "IModuleDefinitionStore", "ITenantStore", "IAliasStore" };

        public string Name => "Authorship stamps";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Anonymise("Authorship stamps") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(subject.Username) || !AccountIsGone(tenantSessions, subject.Username))
            {
                return new[] { new ErasureOutcome(Store, ErasureHandling.Anonymise, 0) };
            }

            // One read session per side (tenant, master) for every aggregate, then the writes: a session per aggregate
            // would open two dozen connections for a question that is usually answered "none".
            var stamped = new List<(Type Store, Type Aggregate, PropertyInfo[] Stamps, List<object> Rows)>();
            var stores = StampedStores();
            foreach (var side in new[] { false, true })
            {
                var sessions = side ? (IDocumentSessionFactory)masterSessions : tenantSessions;
                var session = sessions.OpenQuerySession();
                try
                {
                    foreach (var (storeType, aggregateType, stamps) in stores.Where(s => MasterStores.Contains(s.Store.Name) == side))
                    {
                        ct.ThrowIfCancellationRequested();
                        var rows = ReadStamped(session, aggregateType, stamps, subject.Username);
                        if (rows.Count > 0)
                        {
                            stamped.Add((storeType, aggregateType, stamps, rows));
                        }
                    }
                }
                finally
                {
                    (session as IDisposable)?.Dispose();
                }
            }

            var affected = 0;
            foreach (var (storeType, aggregateType, stamps, rows) in stamped)
            {
                var store = services.GetRequiredService(storeType);
                var update = typeof(IRepository<,>).MakeGenericType(aggregateType, typeof(int)).GetMethod("UpdateAsync")!;
                using (AuditStampSuppression.Begin())
                {
                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var stamp in stamps)
                        {
                            if (string.Equals(stamp.GetValue(row) as string, subject.Username, StringComparison.Ordinal))
                            {
                                stamp.SetValue(row, Placeholder);
                            }
                        }

                        await (Task)update.Invoke(store, new[] { row, ct })!;
                        affected++;
                    }
                }
            }

            return new[] { new ErasureOutcome(Store, ErasureHandling.Anonymise, affected) };
        }

        /// <summary>True when no account with this username remains in the tenant (the person's last site is being erased).</summary>
        public static bool AccountIsGone(IFrameworkStoreSessionFactory sessions, string username) =>
            !FrameworkStoreReads.Query(sessions, session => session.Query<UserAggregate>().Any(u => u.Username == username));

        /// <summary>Every closed seam store whose aggregate carries a string stamp, with the stamp properties.</summary>
        public static IReadOnlyList<(Type Store, Type Aggregate, PropertyInfo[] Stamps)> StampedStores()
        {
            var result = new List<(Type, Type, PropertyInfo[])>();
            foreach (var storeType in typeof(ISiteStore).Assembly.GetTypes().Where(t => t.IsInterface && t.Namespace == typeof(ISiteStore).Namespace))
            {
                var closed = storeType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRepository<,>) && i.GetGenericArguments()[1] == typeof(int));
                if (closed is null)
                {
                    continue;
                }

                var aggregate = closed.GetGenericArguments()[0];
                var stamps = aggregate.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => StampNames.Contains(p.Name) && p.PropertyType == typeof(string) && p.CanWrite)
                    .ToArray();
                if (stamps.Length > 0)
                {
                    result.Add((storeType, aggregate, stamps));
                }
            }

            return result;
        }

        /// <summary>The rows of one aggregate stamped with the username, read detached through the session.</summary>
        public static List<object> ReadStamped(IQuerySession session, Type aggregateType, PropertyInfo[] stamps, string username)
        {
            var source = (IQueryable)typeof(IQuerySession).GetMethod(nameof(IQuerySession.Query))!.MakeGenericMethod(aggregateType).Invoke(session, null)!;
            var row = Expression.Parameter(aggregateType, "row");
            Expression? any = null;
            foreach (var stamp in stamps)
            {
                var test = Expression.Equal(Expression.Property(row, stamp), Expression.Constant(username, typeof(string)));
                any = any is null ? test : Expression.OrElse(any, test);
            }

            var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), new[] { aggregateType }, source.Expression, Expression.Lambda(any!, row));
            var rows = new List<object>();
            foreach (var item in (IEnumerable)source.Provider.CreateQuery(where))
            {
                rows.Add(item);
            }

            return rows;
        }
    }
}
