using Epixx.Models.Entities;

namespace Epixx.Models.DTO
{
    public class PalletCountAndStoreListDTO
    {
        public List<PalletCountDTO> PalletCount { get; set; }
        public List<Store> Stores { get; set; }
    }
}
