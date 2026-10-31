#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Erasure;
using Stomme.Models.Erasure;

namespace Stomme.Modules.Erasure.Application
{
    /// <summary>
    /// What deleting an account does on this site, read from the participants' declarations (BL-276) - the list the
    /// profile page shows before the person confirms. It is built from the same declarations the erasure run acts on, so
    /// a module that adds a participant is listed with no page change, and the list cannot claim what the run does not do.
    /// Deleted first, then what is kept without the person's name, then what is kept for a time and why; a line two
    /// participants declare alike is listed once.
    /// </summary>
    public static class ErasureSummary
    {
        public static List<ErasureSummaryItem> Build(IEnumerable<ISubjectErasureParticipant> participants) =>
            participants
                .SelectMany(participant => participant.Declarations.Select(declaration => new ErasureSummaryItem
                {
                    Description = string.IsNullOrWhiteSpace(declaration.Summary) ? participant.Name : declaration.Summary!,
                    Handling = declaration.Handling.ToString(),
                    Reason = declaration.Reason?.ToString(),
                    RetentionDays = declaration.Period is { } period ? (int)Math.Ceiling(period.TotalDays) : null,
                }))
                .GroupBy(item => (item.Description, item.Handling, item.Reason, item.RetentionDays))
                .Select(group => group.First())
                .OrderBy(item => Enum.Parse<ErasureHandling>(item.Handling))
                .ThenBy(item => item.RetentionDays ?? 0)
                .ThenBy(item => item.Description, StringComparer.Ordinal)
                .ToList();
    }
}
