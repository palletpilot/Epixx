using Epixx.Data;
using Epixx.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Epixx.Services
{
    public class StoreService
    {
        private readonly AppDbContext _db;
        private readonly Random _rnd = new Random();
        private readonly DriverService _driverservice;
        public StoreService(AppDbContext db, DriverService driverService)
        {
            _db = db;
            _driverservice = driverService;
        }
        public int GenerateRandomLoadingDockId()
        {
            var loadingDockIds = _db.LoadingDocks.Select(ld => ld.Id).ToList();
            if (loadingDockIds.Count == 0)
            {
                throw new InvalidOperationException("No loading docks available.");
            }
            int randomIndex = _rnd.Next(loadingDockIds.Count);
            return loadingDockIds[randomIndex];
        }
        public int GenerateUniqueStoreCode()
        {
            int code;
            do
            {
                code = _rnd.Next(101, 1000);
            } while (_db.Stores.Any(s => s.Code == code));
            return code;
        }
        public List<Store> GetAllStores()
        {
            return _db.Stores
                .ToList();
        }
        public int GetStoreCount()
        {
            return _db.Stores.Count();
        }
        public bool CheckIfStoreExists(string name)
        {
            return _db.Stores.Any(s => s.Name == name);
        }
        public int GetStoreIdByPalletId(int palletId)
        {
            var storeId = _db.Pallets
                .Where(p => p.Id == palletId)
                .Select(p => p.StoreId)
                .FirstOrDefault();

            return storeId ?? -1;
        }

        public string GetPackingAreaNameByStoreId(int id)
        {
            var store = GetStoreByStoreId(id);

            if (store == null) return "";

            return _db.LoadingDocks
                .Where(ld => ld.Id == store.LoadingDockId)
                .Select(ld => ld.Name)
                .FirstOrDefault() ?? "";
        }
        public List<Pallet> GetPalletsByStoreId()
        {
            var pallets = _db.Pallets
                .FromSqlRaw(@"
                    WITH MostPallets AS (
                        SELECT TOP (1) StoreId
                        FROM Pallets WITH (ROWLOCK, READPAST, UPDLOCK)
                        WHERE StoreId IS NOT NULL
                          AND Status = 'PackingAreaTransfer'
                          AND DriverId IS NULL
                        GROUP BY StoreId
                        ORDER BY COUNT(*) DESC
                    ),
                    TopPallets AS (
                        SELECT TOP (5) Id
                        FROM Pallets WITH (ROWLOCK, READPAST, UPDLOCK)
                        WHERE StoreId = (SELECT StoreId FROM MostPallets)
                          AND Status = 'PackingAreaTransfer'
                          AND DriverId IS NULL
                        ORDER BY Id
                    )
                    UPDATE p
                    SET DriverId = {0}
                    OUTPUT inserted.*
                    FROM Pallets p
                    INNER JOIN TopPallets tp ON p.Id = tp.Id;
                ", _driverservice.GetDriverId())
                .ToList();
            return pallets;
        }

        public Store? GetStoreByStoreId(int id)
        {
            return _db.Stores
                .Where(s => s.Id == id)
                .FirstOrDefault();
        }

      
    }
}
