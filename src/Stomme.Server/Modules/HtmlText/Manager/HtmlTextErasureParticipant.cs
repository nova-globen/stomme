#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Infrastructure.Backup;
using Stomme.Modules.Erasure.Participants;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Repository.Stores;

namespace Stomme.Modules.HtmlText.Manager
{
    /// <summary>
    /// The HtmlText module's authorship stamps: its content rows carry the author's
    /// username in <c>CreatedBy</c> / <c>ModifiedBy</c>; once the account is gone from the tenant they are replaced by
    /// the same placeholder the framework's stamps get (Anonymise - the content and its dates stay). The module owns
    /// its store and session factory (the integer-keyed seam shape), so the framework's reflective participant cannot
    /// reach it and it declares its own.
    /// </summary>
    public sealed class HtmlTextErasureParticipant(IHtmlTextStore store, IHtmlTextStoreSessionFactory sessions, IFrameworkStoreSessionFactory frameworkSessions) : ISubjectErasureParticipant
    {
        public string Name => "HtmlText authorship";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Anonymise("HtmlTextAggregate (stamps)") with { Summary = "your username on the text you edited (the text stays)" } };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var username = subject.Username;
            if (string.IsNullOrEmpty(username) || !AuthorshipStampsErasureParticipant.AccountIsGone(frameworkSessions, username))
            {
                return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Anonymise, 0) };
            }

            List<HtmlTextAggregate> rows;
            var session = sessions.OpenQuerySession();
            try
            {
                rows = session.Query<HtmlTextAggregate>().Where(h => h.CreatedBy == username || h.ModifiedBy == username).ToList();
            }
            finally
            {
                (session as IDisposable)?.Dispose();
            }

            using (AuditStampSuppression.Begin())
            {
                foreach (var row in rows)
                {
                    if (row.CreatedBy == username)
                    {
                        row.CreatedBy = AuthorshipStampsErasureParticipant.Placeholder;
                    }

                    if (row.ModifiedBy == username)
                    {
                        row.ModifiedBy = AuthorshipStampsErasureParticipant.Placeholder;
                    }

                    await store.UpdateAsync(row, ct);
                }
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Anonymise, rows.Count) };
        }
    }
}
