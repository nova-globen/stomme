namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The one spelling of "normalized" the framework store uses for case-insensitive lookup columns —
    /// <see cref="FolderAggregate.PathNormalized"/> and <see cref="FileAggregate.NameNormalized"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a normalized column exists at all.</b> A provider disagreement was measured, with no portable
    /// spelling: <c>x.ToLower() == v</c> translates on the relational providers and <b>throws</b>
    /// <c>NotSupportedException</c> on RavenDB, while the portable <c>x == v</c> is case-SENSITIVE on
    /// relational and case-INSENSITIVE on RavenDB. One expression, two semantics — so the File cluster could
    /// not cross the seam. The answer is to remove the disagreement from the DATA instead of translating the QUERY: store
    /// a lowercase copy, then filter it with plain <c>==</c>. Both providers then agree, because for two
    /// lowercase strings ordinal equality and case-insensitive equality are the SAME relation. That
    /// equivalence is the whole argument, and it is asserted on both live providers by
    /// <c>A_normalized_column_filter_is_identical_on_every_provider</c>.
    /// </para>
    /// <para>
    /// This is ASP.NET Identity's split, which this codebase already runs
    /// (<c>TenantDBContext : IdentityUserContext&lt;IdentityUser&gt;</c>): <c>UserName</c> is what was typed and
    /// what is displayed, <c>NormalizedUserName</c> is what is matched. The stored column keeps the author's
    /// case; the normalized column is the lookup key and is never shown.
    /// </para>
    /// <para>
    /// <b><see cref="string.ToLowerInvariant"/>, not <see cref="string.ToLower()"/>, and the choice is
    /// deliberate.</b> The backfill in <c>10020003_AddFolderFileNormalizedColumns</c> is SQL <c>LOWER()</c>,
    /// which is the database's own collation, so the C# side must not vary with the server's thread culture —
    /// a Turkish-culture host lowercasing "I" to "ı" would write a value no SQL <c>LOWER()</c> ever produces
    /// and the row would stop resolving. The harvested code used culture-sensitive <c>.ToLower()</c> on the
    /// argument side against a database-side <c>lower()</c>, so this is strictly MORE consistent than what it
    /// replaces, not less. For the ASCII paths and filenames this actually stores, the two agree.
    /// </para>
    /// </remarks>
    public static class NormalizedText
    {
        /// <summary>
        /// The lookup form of <paramref name="value"/>. Null in, null out — a nullable column stays nullable
        /// rather than becoming an empty string, which would make a null path collide with a root folder's
        /// <c>""</c>.
        /// </summary>
        public static string Normalize(string value)
        {
            return value?.ToLowerInvariant();
        }
    }
}
