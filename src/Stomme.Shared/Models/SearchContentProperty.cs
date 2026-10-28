using System.ComponentModel.DataAnnotations;

namespace Stomme.Models
{
    public class SearchContentProperty
    {
        [Key]
        public int PropertyId { get; set; }

        public int SearchContentId { get; set; }

        public string Name { get; set; }

        public string Value { get; set; }
    }
}
