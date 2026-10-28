using Stomme.Models;

namespace Stomme.Infrastructure
{
    public interface ISiteMigration
    {
        void Up(Site site, Alias alias);
        void Down(Site site, Alias alias); // for future use (if necessary)
    }
}
