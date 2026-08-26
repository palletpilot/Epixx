using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Epixx.Models.Entities
{
    public class Pallet
    {
        public int Id { get; set; }

        [Required]
        public string Description { get; set; }

        [Required]
        public long Barcode { get; set; }
        public Category Category { get; set; }
        public int Width { get; set; } = 80;
        public int Height { get; set; }
        public double Weight { get; set; }
        [Required]
        public int Amount { get; set; }

        [Required]
        public string Status { get; set; }
        public string? Destination { get; set; }
        public string? Location { get; set; }
        public DateTime? StorageDate { get; set; }
        public DateTime? TransferDate { get; set; }
        public int? StoreId { get; set; }
        [ForeignKey("StoreId")]
        public int? DriverId { get; set; }
        [ForeignKey("DriverId")]
        public Driver? Driver { get; set; }      // Navigation property
        [Timestamp]
        public byte[] RowVersion { get; set; }
    }
}
