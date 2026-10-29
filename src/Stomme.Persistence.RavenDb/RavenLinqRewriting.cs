using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Raven.Client.Documents.Linq;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Adapts the standard LINQ the seam's callers write to the dialect RavenDB's query provider understands.
/// <para>
/// <b>Why this has to exist.</b> <see cref="Persistence.Abstractions.IQuerySession.Query{TEntity}"/> hands back
/// a bare <see cref="IQueryable{T}"/>, and D7 says the code composing it must not know which provider is
/// underneath. So the framework store writes the shape every LINQ provider defines —
/// <c>roleIds.Contains(item.RoleId)</c>, which EF Core translates to <c>WHERE RoleId IN (...)</c>. RavenDB's
/// LINQ provider does NOT implement it: it throws
/// <see cref="NotSupportedException"/> ("Could not understand expression"), and its own spelling for the same
/// predicate is the RavenDB-specific extension <c>RavenQueryableExtensions.In</c>. Writing <c>.In(...)</c> in
/// the repository would bind that read to RavenDB and break it on the relational provider — the exact coupling
/// D7 forbids. So the translation belongs HERE, in the provider, which is what a provider is for.
/// </para>
/// <para>
/// <b>This is not a theoretical shape.</b> <c>UserRoleRepository</c> issues it four times, including
/// <c>GetUserRoles(userId, siteId)</c> — the read on the authorization path. Without this rewrite the
/// Users/Roles cluster does not run on RavenDB at all.
/// </para>
/// </summary>
internal static class RavenLinqRewriting
{
    private static readonly MethodInfo InMethod = typeof(RavenQueryableExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(RavenQueryableExtensions.In)
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    /// <summary>
    /// Rewrites a PREDICATE into RavenDB's dialect.
    /// </summary>
    /// <remarks>
    /// <see cref="RavenRepository{TAggregate,TId}.FindAsync"/> composes <c>.Where(predicate)</c> on RavenDB's own
    /// queryable, so it never travels through <see cref="Rewriting{T}"/>'s provider - and the very shape that
    /// wrapper exists for, <c>ids.Contains(item.Property)</c>, therefore threw "Could not understand expression"
    /// on the module seam while working on the framework seam. Wrapping the queryable would not do: the wrapper is
    /// not a Raven queryable, so <c>ToListAsync</c> would not accept it. The predicate is rewritten instead.
    /// </remarks>
    public static Expression<Func<T, bool>> RewritePredicate<T>(Expression<Func<T, bool>> predicate) =>
        (Expression<Func<T, bool>>)new ContainsToInVisitor().Visit(predicate);
    /// <summary>Wraps <paramref name="query"/> so every composed expression is rewritten before RavenDB sees it.</summary>
    public static IQueryable<T> Rewriting<T>(this IQueryable<T> query) => new RewritingQueryable<T>(query);

    /// <summary>
    /// A queryable that keeps RavenDB's own expression as its root but hands out a rewriting provider.
    /// <para>
    /// Exposing the INNER expression is what keeps this simple: when <c>Queryable.Where</c> composes, it builds
    /// its call around <c>source.Expression</c> — RavenDB's expression, not a reference to this wrapper — and
    /// then asks <c>source.Provider</c> to create the query. So the tree that reaches
    /// <see cref="RewritingQueryProvider"/> contains no wrapper node to unpick; it only needs rewriting.
    /// </para>
    /// </summary>
    private sealed class RewritingQueryable<T>(IQueryable<T> inner) : IOrderedQueryable<T>
    {
        public Type ElementType => typeof(T);

        public Expression Expression => inner.Expression;

        public IQueryProvider Provider => new RewritingQueryProvider(inner.Provider);

        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RewritingQueryProvider(IQueryProvider inner) : IQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(Rewrite(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new RewritingQueryable<TElement>(inner.CreateQuery<TElement>(Rewrite(expression)));

        public object? Execute(Expression expression) => inner.Execute(Rewrite(expression));

        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(Rewrite(expression));

        private static Expression Rewrite(Expression expression) => new ContainsToInVisitor().Visit(expression);
    }

    /// <summary>
    /// Rewrites <c>values.Contains(document.Field)</c> to RavenDB's <c>document.Field.In(values)</c>.
    /// </summary>
    private sealed class ContainsToInVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (TryGetContainsOperands(node, out var values, out var field)
                // Only when the tested value is a DOCUMENT field and the collection is not. The mirror image —
                // document.Tags.Contains("admin"), a collection FIELD containing a constant — is a different
                // predicate that RavenDB implements natively, and rewriting it would silently invert the
                // query's meaning.
                && ReferencesParameter(field!)
                && !ReferencesParameter(values!))
            {
                var elementType = field!.Type;
                var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
                if (enumerableType.IsAssignableFrom(values!.Type))
                {
                    return Expression.Call(
                        InMethod.MakeGenericMethod(elementType),
                        field,
                        Expression.Convert(values, enumerableType));
                }
            }

            return base.VisitMethodCall(node);
        }

        // Contains has two spellings: the instance method on a collection (List<T>.Contains(value) — what a
        // captured List produces) and the static LINQ operator (Enumerable.Contains(source, value) — what an
        // array or a query produces). Both mean the same thing here.
        private static bool TryGetContainsOperands(MethodCallExpression node, out Expression? values, out Expression? field)
        {
            values = null;
            field = null;

            if (node.Method.Name != nameof(Enumerable.Contains))
            {
                return false;
            }

            if (node.Object is not null && node.Arguments.Count == 1)
            {
                values = Unwrap(node.Object);
                field = node.Arguments[0];
                return true;
            }

            if (node.Object is null && node.Arguments.Count == 2)
            {
                values = Unwrap(node.Arguments[0]);
                field = node.Arguments[1];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Strips a conversion the COMPILER inserted around the collection.
        /// </summary>
        /// <remarks>
        /// An ARRAY is the ordinary way to write "any of these", and on .NET 10 <c>array.Contains(x)</c> no longer
        /// binds to <c>Enumerable.Contains</c>: it binds to the span overload, through an implicit conversion
        /// (<c>op_Implicit</c>) from the array to <c>ReadOnlySpan&lt;T&gt;</c>. The operand is still the array, but
        /// the node the visitor sees is the conversion - whose type is NOT an <c>IEnumerable&lt;T&gt;</c>, so the
        /// rewrite silently did not apply and RavenDB answered "Could not understand expression". Measured on a
        /// live server: a List worked, the identical array did not.
        /// </remarks>
        private static Expression Unwrap(Expression expression) => expression switch
        {
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => Unwrap(convert.Operand),
            MethodCallExpression { Method.IsSpecialName: true, Method.Name: "op_Implicit" or "op_Explicit", Arguments.Count: 1, Object: null } conversion => Unwrap(conversion.Arguments[0]),
            _ => expression
        };

        private static bool ReferencesParameter(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder.Found;
        }

        private sealed class ParameterFinder : ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                Found = true;
                return base.VisitParameter(node);
            }
        }
    }
}
