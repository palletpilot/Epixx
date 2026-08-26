using Epixx.Models.Entities;

namespace Epixx.Models.DTO
{
    public class PalletCountDTO
    {
        public int Count { get; set; }
        public string Description { get; set; }
        public DateTime? TransferDate  { get; set; }
    }
}
