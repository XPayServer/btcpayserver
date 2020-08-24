using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Zammad
{
    public class ZammadOptions
    {
        public string Endpoint { get; set; }
        public string APIKey { get; set; }
        public bool Configured { get; set; }

        [Display(Name = "Which group put new server tickets in?")]
        public int? ServerTicketsGroupId { get; set; }
    }
}