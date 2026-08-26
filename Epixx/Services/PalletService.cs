using Epixx.Data;
using Epixx.Models.DTO;
using Epixx.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net.NetworkInformation;

namespace Epixx.Services
{
    public class PalletService
    {
        private readonly DriverService _driver;
        private readonly AppDbContext _db;
        private readonly Random _rnd = new Random();

        public PalletService(
            AppDbContext db,
            DriverService driver)
        {
            _db = db;
            _driver = driver;
        }
        public void CreateNewPalletType(PalletType palletType)
        {
            _db.PalletTypes.Add(palletType);
            _db.SaveChanges();
        }
        public List<PalletType> GetAllPalletTypes()
        {
            return _db.PalletTypes.ToList();
        }
        public void RemovePallet(Pallet pallet)
        {
            _db.Pallets.Remove(pallet);
            _db.SaveChanges();
        }
        public int GetPalletCountByStatus(string status)
        {
            return _db.Pallets.Count(p => p.Status == status);
        }
        public List<Pallet> ClaimPalletsForTransfer()
        {
            var pallets = _db.Pallets
            .FromSqlRaw(@"
                WITH Candidates AS (
                    SELECT TOP (20) *
                    FROM Pallets WITH (ROWLOCK, READPAST, UPDLOCK)
                    WHERE Status = 'PalletTransfer'
                      AND DriverId IS NULL
                      AND Location IS NOT NULL
                ),
                RowCounts AS (
                    SELECT LEFT(Location, 2) AS RowKey, COUNT(*) AS Cnt
                    FROM Candidates
                    GROUP BY LEFT(Location, 2)
                ),
                BestRow AS (
                    SELECT TOP (1) RowKey
                    FROM RowCounts
                    ORDER BY Cnt DESC
                ),
                TopPallets AS (
                    SELECT TOP (10) c.Id
                    FROM Candidates c
                    WHERE LEFT(c.Location, 2) = (SELECT RowKey FROM BestRow)
                    ORDER BY c.Id
                )
                UPDATE p
                SET DriverId = {0}
                OUTPUT inserted.*
                FROM Pallets p
                INNER JOIN TopPallets tp ON p.Id = tp.Id;
            ", _driver.GetDriverId())
            .AsEnumerable()
            .ToList();
            return pallets;
        }

        public List<Pallet> GetPalletsByStatus(string status)
        {
            return _db.Pallets.Where(p => p.Status == status).ToList();
        }

        public void UpdatePalletStatus(string status, int palletId)
        {
            var pallet = _db.Pallets.SingleOrDefault(p => p.Id == palletId);

            if (pallet == null)
                return;

            pallet.Status = status;
            _db.SaveChanges();
        }
        public Pallet? FetchPalletByBarcode(string barcode)
        {
            long code = long.Parse(barcode);
            return _db.Pallets.AsNoTracking().FirstOrDefault(p => p.Barcode == code);
        }
        public Pallet? FetchPalletByLocation(string location)
        {
            var pallet = _db.Pallets.FirstOrDefault(p => p.Location == location);
            return pallet;
        }
        public void ChangePalletLocationToDestination(Pallet pallet)
        {
            var oldSpot = _db.PalletSpots.Include(s => s.CurrentPallet).SingleOrDefault(s => s.CurrentPalletId == pallet.Id);
            var newSpot = _db.PalletSpots.Include(s => s.CurrentPallet).SingleOrDefault(s => s.Location == pallet.Destination);
            var p = _db.Pallets.SingleOrDefault(p => p.Id == pallet.Id);
            oldSpot.CurrentPalletId = null;
            oldSpot.CurrentPallet = null;
            newSpot.CurrentPalletId = pallet.Id;
            newSpot.CurrentPallet = pallet;
            p.Location = pallet.Destination;
            p.Destination = null;
            p.Status = "Pickable";
            _db.SaveChanges();
        }
        public void PlacePalletInWarehouse(int palletId, string location, string status)
        {
            var pallet = _db.Pallets.SingleOrDefault(p => p.Id == palletId);
            var spot = _db.PalletSpots.Include(s => s.CurrentPallet).SingleOrDefault(s => s.Location == location);

            if (pallet == null || spot == null) return;

           
            // Assign new spot
            spot.CurrentPalletId = pallet.Id;
            spot.CurrentPallet = pallet;
            spot.ReservedUntil = null;
            spot.ReservedByDriverId = null;
            pallet.Status = status;
            pallet.Location = location;
            pallet.Destination = null;
            pallet.StorageDate = DateTime.UtcNow;
            _db.SaveChanges();
         

        }

        public Pallet SetPalletDestination(int id, string destination)
        {
            var pallet = _db.Pallets.FirstOrDefault(p => p.Id == id);
            if (pallet == null)
                return null;

            pallet.Destination = destination;
            _db.SaveChanges();
            return pallet;
        }

        public List<PalletTransferDTO> AssignDestinationsToPallets(List<Pallet> pallets)
        {
            var dtoList = new List<PalletTransferDTO>();
            var now = DateTime.UtcNow;
            var driverId = _driver.GetDriverId();
            PalletSpot? candidateSpot = null;
            foreach (var pallet in pallets)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    now = DateTime.UtcNow;
                    candidateSpot = _db.PalletSpots
                        .FromSqlRaw(@"SELECT TOP (1) * 
                            FROM PalletSpots WITH (UPDLOCK, READPAST, ROWLOCK) 
                            WHERE Height >= {0} 
                            AND CurrentPalletId IS NULL 
                            AND (ReservedUntil IS NULL OR ReservedUntil < {1}) 
                            AND Category = {2} 
                            AND Location LIKE '%1' 
                            ORDER BY Id", pallet.Height, now, pallet.Category)
                        .FirstOrDefault();

                    if (candidateSpot == null)
                    {
                        candidateSpot = _db.PalletSpots
                            .FromSqlRaw(@"SELECT TOP (1) * 
                                FROM PalletSpots WITH (UPDLOCK, READPAST, ROWLOCK) 
                                WHERE Height >= {0} 
                                AND CurrentPalletId IS NULL 
                                AND (ReservedUntil IS NULL OR ReservedUntil < {1}) 
                                AND Location LIKE '%1' 
                                ORDER BY Id", pallet.Height, now)
                            .FirstOrDefault();
                    }

                    if (candidateSpot == null)
                        break;
                    candidateSpot.ReservedByDriverId = driverId;
                    candidateSpot.ReservedUntil = now.AddMinutes(300);
                    pallet.Destination = candidateSpot.Location;
                    try
                    {
                        using var transaction = _db.Database.BeginTransaction();
                        _db.SaveChanges();
                        transaction.Commit();
                        break; 
                    }
                    catch
                    {
                        _db.Entry(candidateSpot).State = EntityState.Detached;
                        candidateSpot = null;
                        Thread.Sleep(50);
                    }
                }

                if (candidateSpot == null)
                    continue;


                

                dtoList.Add(new PalletTransferDTO
                {
                    Barcode = pallet.Barcode,
                    Description = pallet.Description,
                    Height = pallet.Height,
                    Weight = pallet.Weight,
                    Location = pallet.Location,
                    Destination = candidateSpot.Location,
                });
            }

            return dtoList;
        }
        public long GenerateUniqueBarcodeForPallet()
        {
            long barcode;
            do
            {
                barcode = (long)(_rnd.NextDouble() * 9_000_000_000L + 1_000_000_000L);
            } while (_db.Pallets.Any(p => p.Barcode == barcode));

            return barcode;
        }
        public void AddPallet(Pallet pallet)
        {
            _db.Pallets.Add(pallet);
            _db.SaveChanges();
        } 
        public PalletType GetPalletTypeByDescription(string description)
        {
            var palletType = _db.PalletTypes.FirstOrDefault(pt => pt.Description == description);
            return palletType;
        }
        public Pallet FindPalletSpotLocation(Pallet pallet)
        {
            if (pallet == null)
                return null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var now = DateTime.UtcNow;
                var driverId = _driver.GetDriverId();

                using var transaction = _db.Database.BeginTransaction();

                try
                {

                    var spot = _db.PalletSpots
                      .FromSqlRaw(@"
                        WITH cte AS (
                            SELECT TOP (1) *
                            FROM PalletSpots WITH (ROWLOCK, READPAST, UPDLOCK)
                            WHERE CurrentPalletId IS NULL
                                AND Height >= {0}
                                AND Location NOT LIKE '%1'
                                AND (ReservedUntil IS NULL OR ReservedUntil < {1})
                            ORDER BY 
                                CASE WHEN Category = {2} THEN 0 ELSE 1 END,
                                Id
                        )
                        UPDATE cte
                        SET 
                            ReservedByDriverId = {3},
                            ReservedUntil = DATEADD(MINUTE, 300, {1})
                        OUTPUT inserted.*;",
                          pallet.Height, now, pallet.Category, driverId)
                      .AsEnumerable()
                      .FirstOrDefault();

                    if (spot == null)
                    {
                        transaction.Rollback();
                        continue; 
                    }

                    _db.Attach(spot);
                    pallet.Destination = spot.Location;
                    _db.Update(pallet);

                    _db.SaveChanges();
                    transaction.Commit();

                    return pallet;
                }
                catch (DbUpdateConcurrencyException)
                {
                    transaction.Rollback();
                }
            }

            return null;
        }

    }
}
