using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Stomme.Interfaces
{
    public interface ITokenSource
    {
        IDictionary<string, object> GetTokens();
    }
}
