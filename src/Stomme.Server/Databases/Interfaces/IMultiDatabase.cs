using Stomme.Databases.Interfaces;

namespace Stomme.Repository.Databases.Interfaces
{
    public interface IMultiDatabase
    {
        public IDatabase ActiveDatabase { get; }
    }
}
