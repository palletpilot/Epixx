namespace Epixx.Models.DTO
{
    public class PalletCountNullableStoreDTO
    {
        public required int Count { get; set; }
        public string? StoreName { get; set; }
        public int? StoreId { get; set; }
        public required string Description { get; set; }
    }
}
