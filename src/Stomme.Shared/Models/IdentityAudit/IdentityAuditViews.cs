using System;

namespace Stomme.Models.IdentityAudit
{
    /// <summary>What happened to an account (BL-120). The numbers are stored - append, never renumber.</summary>
    public enum IdentityAuditEvent
    {
        SignIn = 0,
        SignInFailed = 1,
        LockedOut = 2,
        SignInDenied = 3,
        PasswordReset = 4,
        PasswordResetFailed = 5,
        EmailVerified = 6,
        ExternalLoginLinked = 7,
        UserCreated = 8,
        UserDeleted = 9,
        RoleGranted = 10,
        RoleRevoked = 11,
        SessionRevoked = 12,
    }

    /// <summary>One line of the identity audit ledger, as the admin page shows it.</summary>
    /// <param name="ActorUserId">Who did it: the signed-in user, or null for an anonymous request (a sign-in attempt).</param>
    /// <param name="SubjectUserId">Whose account it happened to, when the account is known.</param>
    /// <param name="SubjectName">The account's user name when known - never the text typed for an UNKNOWN one.</param>
    public sealed record IdentityAuditEntryView(
        Guid Id,
        DateTime OccurredOnUtc,
        IdentityAuditEvent Event,
        int? ActorUserId,
        int? SubjectUserId,
        string SubjectName,
        string RemoteAddress,
        string Detail);
}
