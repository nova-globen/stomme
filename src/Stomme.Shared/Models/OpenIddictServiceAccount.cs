namespace Stomme.Models
{
    /// <summary>
    /// A client-credentials application's service account on this site - the Stomme user its tokens sign in on
    /// <c>api/*</c>, granted roles and permission rows like a person. Its name is derived from the client id; it exists when
    /// the server made it (locked out of every sign-in for good). <see cref="NameTaken"/> means an account of that name
    /// exists that the server did NOT make - a person's, which is never used for an application.
    /// </summary>
    public class OpenIddictServiceAccount
    {
        public string ClientId { get; set; }

        public string Username { get; set; }

        public bool Exists { get; set; }

        public int? UserId { get; set; }

        public bool NameTaken { get; set; }
    }
}
