using Epixx.Data;
using Epixx.Models.DTO;
using Epixx.Models.Entities;
using Epixx.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Epixx.Controllers
{
    [Authorize]
    public class AutoController : Controller
    {
        private readonly StoreService _storeservice;
        private readonly DriverService _driverservice;
        private readonly PalletService _palletservice;
        private readonly Random _rnd = new Random();
        private readonly AppDbContext _db;
        public AutoController(StoreService storeservice, DriverService driverservice, PalletService palletstorageservice, AppDbContext context) 
        { 
            _db = context;
            _palletservice = palletstorageservice;
            _storeservice = storeservice;
            _driverservice = driverservice;
        }
        public int GetPalletsCountOnDriver()
        {
            return _driverservice.FetchAllPalletsFromDriverByStatus("PackingAreaConfirmation").Count();
        }
        public void CancelMission()
        {
            _driverservice.UnassignAllPalletsFromDriver();
        }
        [HttpPost]
        public void CheckOffPalletsFromDriver([FromBody] string status)
        {
            var pallets = _driverservice.FetchAllPalletsFromDriverByStatus(status);
            foreach(var pallet in pallets)
            {
                _palletservice.RemovePallet(pallet);
            }
        }
        [HttpGet]
        public IActionResult PalletTransferConfirmation()
        {
            var pallets = _driverservice.FetchAllPalletsFromDriverByStatus("ConfirmTransfer");
            var palletDTOs = pallets.Select(p => new PalletTransferDTO
            {
                Barcode = p.Barcode,
                Description = p.Description,
                Location = p.Location,
                Height = p.Height,
                Weight = p.Weight,
                Destination = p.Destination
            }).ToList();
            return View(palletDTOs);
        }
        [HttpGet]
        public IActionResult PackingAreaConfirmation()
        {
            List<PalletAndStoreDTO> palletDTOs = new List<PalletAndStoreDTO>();

            List<Pallet>? pallets = _driverservice.FetchAllPalletsFromDriverByStatus("PackingAreaConfirmation");
            
            int storeid = _storeservice.GetStoreIdByPalletId(pallets[0].Id);
            var store = _storeservice.GetStoreByStoreId(storeid);
            var packingareaname = _storeservice.GetPackingAreaNameByStoreId(storeid);

            foreach (var pallet in pallets)
            {
                palletDTOs.Add(new PalletAndStoreDTO
                {
                    Barcode = pallet.Barcode,
                    Description = pallet.Description,
                    Location = pallet.Location,
                    Height = pallet.Height,
                    Weight = pallet.Weight,
                    StoreName = store.Name,
                    Code = store.Code,
                    PackingAreaName = packingareaname


                });
            }
            return View(palletDTOs);
        }
        [HttpPost]
        public async Task<IActionResult> AssignPalletToQueue([FromBody] SpotDTO dto)
        {
            var pallet = _palletservice.FetchPalletByLocation(dto.Spot);

            if (pallet == null)
                return BadRequest("Pallet not found.");

            _palletservice.UpdatePalletStatus("PackingAreaConfirmation", pallet.Id);

            return Ok();
        }
        [HttpPost]
        public string ChangePalletStatusToConfirmTransfer([FromBody] string locationOrBarcode)
        {
            Pallet? pallet = _palletservice.FetchPalletByLocation(locationOrBarcode)
                 ?? _palletservice.FetchPalletByBarcode(locationOrBarcode);

            if (pallet != null)
            {
                _palletservice.UpdatePalletStatus("ConfirmTransfer", pallet.Id);
                return pallet.Barcode.ToString() == locationOrBarcode ? "Barcode" : "Location";
            }
            return string.Empty;
        }
        [HttpPost]
        public IActionResult ChangePalletLocation([FromBody] string barcode)
        {
            var pallet = _palletservice.FetchPalletByBarcode(barcode);

            if (pallet == null)
                return BadRequest("Pallet not found.");
            _palletservice.ChangePalletLocationToDestination(pallet);
            _driverservice.RemovePalletFromDriver(pallet);
   
            return Ok();
        }
        [HttpGet]
        public IActionResult ConfirmTransfer()
        {
            var pallets = _driverservice.FetchAllPalletsFromDriverByStatus("ConfirmTransfer");
            return Ok(pallets.Any());
        }
        [HttpGet]
        public IActionResult CheckIfNoPalletsAreScanned()
        {
            var anypallets =_driverservice.FetchAllPalletsFromDriverByStatus("ConfirmTransfer").Any();
            return Ok(anypallets);
        }
        [HttpGet]
        public IActionResult TwoTenCheck(int incomingpalletheight, string status)
        {
            var pallets = _driverservice.FetchAllPalletsFromDriverByStatus(status);
            if(pallets.Count() == 0 && incomingpalletheight == 210)
                return Ok(false);
            if(!pallets.Any(p => p.Height == 210) && incomingpalletheight == 210)
                return Ok(true);
            return Ok(false);
        }
        [HttpGet]
        public IActionResult PalletTransfer()
        {
            //Checks wether driver already has pallets assigned, if they do then use those, if not then fetch new pallets for transfer
            var pallets = _driverservice.FetchAllPalletsFromDriver();
            if (!pallets.Any())
                pallets = _palletservice.ClaimPalletsForTransfer();

            var palletDTOs = new List<PalletTransferDTO>();
            palletDTOs = _palletservice.AssignDestinationsToPallets(pallets);

            return View(palletDTOs);
        }
        private IActionResult? GetActiveMissionRedirect(List<string> statuses)
        {
            if (statuses.Contains("PackingAreaConfirmation"))
            {
                return RedirectToAction("PackingAreaConfirmation", "Auto");
            }

            if (statuses.Contains("ConfirmTransfer"))
            {
                return RedirectToAction("PalletTransferConfirmation", "Auto");
            }

            if (statuses.Contains("PackingAreaTransfer"))
            {
                return RedirectToAction("PackingAreaTransfer", "Auto");
            }

            if (statuses.Contains("PalletTransfer"))
            {
                return RedirectToAction("PalletTransfer", "Auto");
            }

            return null;
        }
        private IActionResult FindNewMission()
        {
            bool choosePalletTransfer = _rnd.Next(2) == 0;

            bool hasPalletTransfers =
                _palletservice.GetPalletCountByStatus("PalletTransfer") > 0;

            bool hasPackingAreaTransfers =
                _palletservice.GetPalletCountByStatus("PackingAreaTransfer") > 0;

            if (choosePalletTransfer && hasPalletTransfers)
            {
                return RedirectToAction("PalletTransfer", "Auto");
            }

            if (!choosePalletTransfer && hasPackingAreaTransfers)
            {
                return RedirectToAction("PackingAreaTransfer", "Auto");
            }

            // Fallback om den slumpade typen saknas
            if (hasPalletTransfers)
            {
                return RedirectToAction("PalletTransfer", "Auto");
            }

            if (hasPackingAreaTransfers)
            {
                return RedirectToAction("PackingAreaTransfer", "Auto");
            }

            return RedirectToAction("Index", "Home");
        }
        [HttpGet]
        public IActionResult FindAutoMission()
        {
            var driverId = _driverservice.GetDriverId();

            var statuses = _db.Pallets
                .Where(p => p.DriverId == driverId)
                .Select(p => p.Status)
                .ToList();

            var activeMission = GetActiveMissionRedirect(statuses);

            if (activeMission != null)
            {
                return activeMission;
            }

            return FindNewMission();
        }
        [HttpGet]
        public IActionResult PackingAreaTransfer()
        {
            _driverservice.SetDriverTask(DriverTask.Auto);

            var pallets = _driverservice
                .FetchAllPalletsFromDriverByStatus("PackingAreaTransfer");

            if (pallets.Count == 0)
            {
                pallets = _storeservice.GetPalletsByStoreId();
            }

            if (pallets.Count == 0)
            {
                return RedirectToAction("Index", "Home");
            }

            var storeId = _storeservice.GetStoreIdByPalletId(pallets[0].Id);

            var store = _storeservice.GetStoreByStoreId(storeId);

            if (store == null)
            {
                return RedirectToAction("Index", "Home");
            }

            var packingAreaName =
                _storeservice.GetPackingAreaNameByStoreId(storeId);

            var palletDTOs = pallets.Select(pallet => new PalletAndStoreDTO
            {
                Barcode = pallet.Barcode,
                Description = pallet.Description,
                Location = pallet.Location,
                Height = pallet.Height,
                Weight = pallet.Weight,
                StoreName = store.Name,
                PackingAreaName = packingAreaName
            }).ToList();

            return View(palletDTOs);
        }
    }
}
