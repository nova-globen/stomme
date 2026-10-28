#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Stomme.Erasure;
using Stomme.Metering.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Metering.Infrastructure
{
    /// <summary>
    /// Pseudonymises the site's metering rows when a user is removed from it (D102 item 9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The user id is NULLED; the row stays.</b> A consumption record is two facts joined - that something was
    /// consumed, and by whom - and only the second is personal. Deleting the row would break the first: the site's
    /// counter would no longer agree with the ledger it is repaired from, and a purge would quietly return spent
    /// capacity. So the count survives and the person does not, which is what the aggregates' <c>ForgetUser()</c>
    /// exists for.
    /// </para>
    /// <para>
    /// The AI rollups need no pass: they aggregate by site, day, provider, model and agent, and carry no user id at
    /// all - the design's "ledger AND rollup rows" line was written before the rollup's slice key was settled.
    /// </para>
    /// </remarks>
    public sealed class MeteringUserErasure : ISubjectErasureParticipant
    {
        private readonly IUsageRecordRepository _usage;
        private readonly IAiUsageRecordRepository _aiUsage;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<MeteringUserErasure> _logger;

        public MeteringUserErasure(
            IUsageRecordRepository usage,
            IAiUsageRecordRepository aiUsage,
            IUnitOfWork unitOfWork,
            ILogger<MeteringUserErasure> logger)
        {
            _usage = usage;
            _aiUsage = aiUsage;
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        public string Name => "Metering ledger pseudonymisation";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Anonymise("UsageRecordAggregate"),
            ErasureDeclaration.Anonymise("AiUsageRecordAggregate"),
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var userId = subject.UserId;
            var siteId = subject.SiteId;
            if (userId <= 0 || siteId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var usageRows = 0;
            var aiRows = 0;

            foreach (var row in await _usage.ListByUserAsync(siteId, userId, ct))
            {
                row.ForgetUser();
                await _usage.UpdateAsync(row, ct);
                usageRows++;
            }

            foreach (var row in await _aiUsage.ListByUserAsync(siteId, userId, ct))
            {
                row.ForgetUser();
                await _aiUsage.UpdateAsync(row, ct);
                aiRows++;
            }

            if (usageRows + aiRows > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation("Pseudonymised {Count} metering row(s) for user {UserId} on site {SiteId}; the counts are unchanged.", usageRows + aiRows, userId, siteId);
            }

            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Anonymise, usageRows),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Anonymise, aiRows),
            };
        }
    }
}
