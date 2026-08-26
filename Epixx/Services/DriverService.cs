using Epixx.Data;
using Epixx.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Epixx.Services
{
    public class DriverService
    {
        private readonly IHttpContextAccessor _http;
        private readonly AppDbContext _db;

        public DriverService(AppDbContext db, IHttpContextAccessor http)
        {
            _db = db;
            _http = http;
        }

        private string? Username => _http.HttpContext?.Session.GetString("User");

        private Driver? GetDriverWithPallets() =>
            _db.Drivers
               .Include(d => d.pallets)
               .FirstOrDefault(d => d.Name == Username);

        // ---------------------------------------------------------
        // TASKS
        // ---------------------------------------------------------
        public DriverTask? GetDriverTask()
        {
            return GetDriverWithPallets()?.CurrentTask;
        }
        public Driver GetDriver()
        {
            var driver = _db.Drivers.FirstOrDefault(d => d.Name == Username);
            if (driver == null)
                return new Driver();
            return driver;
        }
        public void SetDriverTask(DriverTask task)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) throw new InvalidOperationException("Driver not found.");

            driver.CurrentTask = task;
            _db.SaveChanges();
        }
        public List<Pallet> FetchAllPalletsFromDriverByStatus(string status)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return new();
            var pallets =  _db.Pallets
                .Where(p => p.DriverId == driver.Id && p.Status == status)
                .ToList();
            return pallets;
        }

        // ---------------------------------------------------------
        // REMOVE PALLET BY BARCODE
        // ---------------------------------------------------------
        public Pallet? RemovePalletFromDriverByBarcode(string barcode)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return null;

            long parsed = long.Parse(barcode);
            var pallet = driver.pallets.FirstOrDefault(p => p.Barcode == parsed);
            if (pallet == null) return null;

            driver.pallets.Remove(pallet);

            _db.SaveChanges();
            return pallet;
        }

        // ---------------------------------------------------------
        // REMOVE PALLET (by pallet.Id)
        // ---------------------------------------------------------
        public void RemovePalletFromDriver(Pallet pallet)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return;

            var p = driver.pallets.FirstOrDefault(x => x.Id == pallet.Id);
            if (p == null) return;
            var dbPallet = _db.Pallets.FirstOrDefault(x => x.Id == pallet.Id);
            var palletSpot =  _db.PalletSpots.FirstOrDefault(x => x.CurrentPalletId == pallet.Id);
            dbPallet.DriverId = null;
            if (palletSpot != null)
            {
                palletSpot.ReservedByDriverId = null;
                palletSpot.ReservedUntil = null;
            }
            driver.pallets.Remove(p);

            _db.SaveChanges();
        }
        public int GetDriverId()
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return -1;
            return driver.Id;

        }
        // ---------------------------------------------------------
        // CHECK OWNERSHIP
        // ---------------------------------------------------------
        public bool CheckIfPalletIsAssignedToDriver(Pallet pallet)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return false;

            return driver.pallets.Any(p => p.Id == pallet.Id);
        }

        // ---------------------------------------------------------
        // FETCH PALLETS
        // ---------------------------------------------------------
        public List<Pallet> FetchAllPalletsFromDriver()
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return new();

            return _db.Pallets
                .Where(p => p.DriverId == driver.Id)
                .ToList();
        }

        public Pallet? FetchSpecificPalletByBarcode(string barcode)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return null;

            long parsed = long.Parse(barcode);
            return driver.pallets.FirstOrDefault(p => p.Barcode == parsed);
        }

        // ---------------------------------------------------------
        // ADD PALLET
        // ---------------------------------------------------------
        public void AddPalletToDriver(Pallet pallet)
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return;

            var dbPallet = _db.Pallets.FirstOrDefault(p => p.Id == pallet.Id);
            if (dbPallet == null) return;

            dbPallet.Location = pallet.Location;
            dbPallet.DriverId = driver.Id;

            if (!driver.pallets.Any(p => p.Id == dbPallet.Id))
                driver.pallets.Add(dbPallet);

            _db.SaveChanges();
        }

        public List<Pallet> GetAllPalletsFromDriver()
        {
            var driver = GetDriverWithPallets();
            if (driver == null) return new();

            return _db.Pallets
                .Where(p => p.DriverId == driver.Id)
                .ToList();
        }

        public void UnassignAllPalletsFromDriver()
        {
           var driver = GetDriver();
            _db.Pallets.Where(p => p.DriverId == driver.Id).ToList().ForEach(p =>
            {
                p.DriverId = null;
                p.Destination = null;
            });
            _db.PalletSpots.Where(p => p.ReservedByDriverId == driver.Id).ToList().ForEach(p =>
            {
                p.ReservedByDriverId = null;
                p.ReservedUntil = null;
            });
            _db.SaveChanges();
        }

        // ---------------------------------------------------------
        // CREATE DRIVER
        // ---------------------------------------------------------
        public string GetOrCreateDriver(string name)
        {
            var d = _db.Drivers.FirstOrDefault(x => x.Name == name);
            if (d == null)
            {
                d = new Driver { Name = name };
                _db.Drivers.Add(d);
                _db.SaveChanges();
            }
            return d.Name;
        }
    }
}
