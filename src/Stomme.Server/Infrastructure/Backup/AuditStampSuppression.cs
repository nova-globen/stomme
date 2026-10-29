using System;
using System.Threading;

namespace Stomme.Infrastructure.Backup
{
    /// <summary>
    /// While a restore writes rows back, the framework contexts must NOT stamp <c>CreatedOn</c>/<c>ModifiedOn</c>/
    /// <c>CreatedBy</c>/<c>ModifiedBy</c> on them: <c>DbContextUtils.SaveChanges</c> overwrites those on every added row, and a
    /// restored row's audit trail is part of what was restored. Ambient (an <see cref="AsyncLocal{T}"/>) and scoped to the
    /// restoring flow only - every other save stamps as it always has.
    /// </summary>
    public static class AuditStampSuppression
    {
        private static readonly AsyncLocal<bool> Suppressed = new();

        public static bool IsActive => Suppressed.Value;

        public static IDisposable Begin()
        {
            var previous = Suppressed.Value;
            Suppressed.Value = true;
            return new Restore(previous);
        }

        private sealed class Restore(bool previous) : IDisposable
        {
            public void Dispose() => Suppressed.Value = previous;
        }
    }
}
