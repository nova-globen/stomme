#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    /// <summary>
    /// What every RavenDB engine store shares (BL-95 increment (a)). A session per operation against the
    /// tenant's database (routed as <c>RavenRepository</c> routes it), every query waiting for a non-stale index
    /// so a read after a write is deterministic, and the engine's filters translated BY HAND onto the document
    /// fields through <c>IAsyncDocumentQuery</c> - RavenDB's LINQ provider refuses the <c>ToLower().Contains()</c>
    /// and the dynamic-LINQ clauses the engine's filters build, so an explicit translation is the honest one; the
    /// conformance suite holds it to the memory store's answers. Two documented divergences from the relational
    /// stores: a substring search is a full-text search over lower-cased TOKENS (the term is split where the
    /// analyzer splits the field, and every piece must occur somewhere in the field - broader than a substring
    /// when the pieces occur apart), and every string equality is EXACT (case-sensitive), which is what SQLite and
    /// PostgreSQL answer and what SQL Server's default collation does not.
    /// </summary>
    internal abstract class RavenEngineStore
    {
        private readonly IDocumentStore _store;
        private readonly ITenantContext _tenantContext;
        private readonly IRavenTenantDatabaseResolver _databases;
        private readonly ITenantAccessor _tenants;

        protected RavenEngineStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IRavenTenantDatabaseResolver databases,
            ITenantAccessor tenants,
            EngineEntitySerializer serializer)
        {
            _store = store;
            _tenantContext = tenantContext;
            _databases = databases;
            _tenants = tenants;
            Serializer = serializer;
        }

        protected EngineEntitySerializer Serializer { get; }

        /// <summary>The engine tenant every write is stamped with and every non-agnostic query is scoped to.</summary>
        protected string TenantId => _tenants.TenantId;

        protected IAsyncDocumentSession OpenSession()
        {
            var database = _databases.Resolve(_tenantContext.CurrentTenantId);
            return string.IsNullOrEmpty(database)
                ? _store.OpenAsyncSession()
                : _store.OpenAsyncSession(new SessionOptions { Database = database });
        }

        protected static string DocumentId<TDocument>(string id) where TDocument : EngineDocument =>
            typeof(TDocument).Name + "/" + id;

        protected IAsyncDocumentQuery<TDocument> Query<TDocument>(IAsyncDocumentSession session, bool tenantAgnostic = false)
            where TDocument : EngineDocument
        {
            var query = session.Advanced.AsyncDocumentQuery<TDocument>().WaitForNonStaleResults();
            if (!tenantAgnostic)
            {
                // Not exact: the default tenant is the EMPTY string, which RavenDB indexes through a sentinel that
                // the analysed (non-exact) equality resolves and the exact one need not; tenant ids are
                // case-insensitively unique, so nothing is lost.
                query = query.WhereEquals(nameof(EngineDocument.TenantId), TenantId);
            }

            return query;
        }

        // The engine stamps nothing itself; see EfEngineStore.Stamp.
        protected void Stamp(Entity entity) => entity.TenantId ??= TenantId;

        protected static async Task StoreAsync<TDocument>(
            IAsyncDocumentSession session, IEnumerable<TDocument> documents, bool insertOnly, CancellationToken cancellationToken)
            where TDocument : EngineDocument
        {
            foreach (var document in documents)
            {
                if (insertOnly)
                {
                    // An empty change vector means "must not exist yet": a duplicate id fails as the memory store's Add does.
                    await session.StoreAsync(document, string.Empty, DocumentId<TDocument>(document.Id), cancellationToken);
                }
                else
                {
                    await session.StoreAsync(document, DocumentId<TDocument>(document.Id), cancellationToken);
                }
            }

            await session.SaveChangesAsync(cancellationToken);
        }

        protected static async Task<long> DeleteAsync<TDocument>(
            IAsyncDocumentSession session, IAsyncDocumentQuery<TDocument> query, CancellationToken cancellationToken)
            where TDocument : EngineDocument
        {
            var documents = await query.ToListAsync(cancellationToken);
            foreach (var document in documents)
            {
                session.Delete(document);
            }

            await session.SaveChangesAsync(cancellationToken);
            return documents.Count;
        }

        /// <summary>A page: the total of the filtered set, then the ordered slice. The query is built twice because a count consumes it.</summary>
        protected static async Task<Page<TDocument>> PageAsync<TDocument>(
            Func<IAsyncDocumentQuery<TDocument>> query, PageArgs pageArgs, Action<IAsyncDocumentQuery<TDocument>> order, CancellationToken cancellationToken)
            where TDocument : EngineDocument
        {
            var count = await query().LongCountAsync(cancellationToken);
            var slice = query();
            order(slice);
            Paginate(slice, pageArgs);
            var items = await slice.ToListAsync(cancellationToken);
            return Page.Of(items, count);
        }

        protected static void Paginate<TDocument>(IAsyncDocumentQuery<TDocument> query, PageArgs? pageArgs)
        {
            if (pageArgs?.Offset is { } offset)
            {
                query.Skip(offset);
            }

            if (pageArgs?.Limit is { } limit)
            {
                query.Take(limit);
            }
        }

        /// <summary>Orders by the entity member the engine's order names; the document carries the same member.</summary>
        protected static void Order<TDocument, TEntity, TProp>(IAsyncDocumentQuery<TDocument> query, OrderDefinition<TEntity, TProp> order)
        {
            var field = MemberName(order.KeySelector);
            var ordering = OrderingOf(typeof(TProp));
            if (order.Direction == OrderDirection.Ascending)
            {
                query.OrderBy(field, ordering);
            }
            else
            {
                query.OrderByDescending(field, ordering);
            }
        }

        protected static void OrderBy<TDocument>(IAsyncDocumentQuery<TDocument> query, string field, Type type, bool descending = false)
        {
            if (descending)
            {
                query.OrderByDescending(field, OrderingOf(type));
            }
            else
            {
                query.OrderBy(field, OrderingOf(type));
            }
        }

        private static OrderingType OrderingOf(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
            {
                return OrderingType.Long;
            }

            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            {
                return OrderingType.Double;
            }

            return OrderingType.String;
        }

        private static string MemberName(LambdaExpression selector)
        {
            var body = selector.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            {
                body = convert.Operand;
            }

            return body is MemberExpression { Expression: ParameterExpression } member
                ? member.Member.Name
                : throw new NotSupportedException($"The RavenDB engine stores order by a member of the entity only; '{selector}' is not one.");
        }

        // ---- the filter vocabulary; each helper is a no-op for a null filter value, as the engine's filters are.

        // Strings are compared EXACTLY (case-sensitive, the SQLite/PostgreSQL answer); anything else (numbers,
        // booleans, enums) has no case to preserve and takes the plain comparison.
        protected static void Equal<TDocument>(IAsyncDocumentQuery<TDocument> query, string field, object? value)
        {
            if (value is not null)
            {
                query.WhereEquals(field, value, exact: value is string);
            }
        }

        protected static void NotEqual<TDocument>(IAsyncDocumentQuery<TDocument> query, string field, object? value)
        {
            if (value is not null)
            {
                query.WhereNotEquals(field, value, exact: value is string);
            }
        }

        protected static void In<TDocument, TValue>(IAsyncDocumentQuery<TDocument> query, string field, IEnumerable<TValue>? values)
        {
            if (values is not null)
            {
                query.WhereIn(field, values.Cast<object>().ToList(), exact: typeof(TValue) == typeof(string));
            }
        }

        protected static void NotIn<TDocument, TValue>(IAsyncDocumentQuery<TDocument> query, string field, IEnumerable<TValue>? values)
        {
            if (values is not null)
            {
                query.Not.WhereIn(field, values.Cast<object>().ToList(), exact: typeof(TValue) == typeof(string));
            }
        }

        protected static void Before<TDocument>(IAsyncDocumentQuery<TDocument> query, string field, DateTimeOffset? value)
        {
            if (value is not null)
            {
                query.WhereLessThan(field, value.Value.UtcDateTime);
            }
        }

        /// <summary>A case-insensitive substring: a full-text search for the lower-cased term with wildcards on both sides, every token required.</summary>
        protected static void Contains<TDocument>(IAsyncDocumentQuery<TDocument> query, string field, string term)
        {
            query.Search(field, Wildcards(term), SearchOperator.And);
        }

        /// <summary>OR of the same substring over several fields, as one grouped clause.</summary>
        protected static void ContainsAny<TDocument>(IAsyncDocumentQuery<TDocument> query, string term, params string[] fields)
        {
            var terms = Wildcards(term);
            query.OpenSubclause();
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    query.OrElse();
                }

                query.Search(fields[i], terms, SearchOperator.And);
            }

            query.CloseSubclause();
        }

        // The term is split where the index's analyzer splits the field - at whitespace AND punctuation - and every
        // piece must match some token (measured: "corr-3" is indexed as the tokens "corr" and "3", so a wildcard on
        // the whole term matched nothing). A term spanning pieces therefore matches when its pieces all occur, which
        // is broader than a substring where they occur apart; it is the documented divergence, not a defect.
        private static string Wildcards(string term)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var c in term.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return string.Join(" ", tokens.Select(token => "*" + token + "*"));
        }

        /// <summary>The single-field projection used to read ids without their documents.</summary>
        protected sealed class IdOnly
        {
            public string Id { get; set; } = null!;
        }

        protected sealed class DefinitionIdOnly
        {
            public string DefinitionId { get; set; } = null!;
        }
    }
}
