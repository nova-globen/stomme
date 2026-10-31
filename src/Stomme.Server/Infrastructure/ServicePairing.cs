using System;
using System.Linq;

namespace Stomme.Infrastructure
{
    /// <summary>The convention that pairs an <c>IService</c> or <c>ITransientService</c> class with its interface.</summary>
    internal static class ServicePairing
    {
        /// <summary>
        /// The interface a convention-registered service is paired with: <c>I{Name}</c>. Found first among the interfaces the
        /// class implements - wherever they are declared, so a module's interface in its Shared assembly pairs with its Server
        /// class - then in the class's own namespace and assembly (BL-251, BL-239's A9: the pairing replaced the class name
        /// throughout the assembly-qualified name, so a class whose name also appeared in its namespace or assembly name - a
        /// class <c>Notes</c> in <c>Acme.Notes</c> - found no interface, and an interface in another assembly was never found
        /// on the server).
        /// </summary>
        public static Type InterfaceFor(Type implementationType)
        {
            var name = "I" + implementationType.Name;
            var implemented = implementationType.GetInterfaces().FirstOrDefault(candidate => candidate.Name == name);
            if (implemented != null)
            {
                return implemented;
            }

            var qualified = string.IsNullOrEmpty(implementationType.Namespace) ? name : implementationType.Namespace + "." + name;
            return implementationType.Assembly.GetType(qualified);
        }
    }
}
