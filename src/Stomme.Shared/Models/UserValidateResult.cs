using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Stomme.Models
{
    public class UserValidateResult
    {
        public bool Succeeded {  get; set; }

        public IDictionary<string, string> Errors { get; set; } = new Dictionary<string, string>();
    }
}
