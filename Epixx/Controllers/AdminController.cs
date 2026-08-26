using Epixx.Data;
using Epixx.Migrations;
using Epixx.Models.DTO;
using Epixx.Models.Entities;
using Epixx.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Epixx.Controllers
{
    [Authorize]

    public class AdminController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly PalletService _palletservice;
        private readonly AppDbContext _db;
        private readonly StoreService _storeservice;

        public AdminController(UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager, PalletService palletService, StoreService storeService, AppDbContext context)
        {
            _palletservice = palletService;
            _storeservice = storeService;
            _userManager = userManager;
            _roleManager = roleManager;
            _db = context;
        }
        private List<PalletCountDTO> GetPalletCountsBasedOnStatus(string status)
        {
            return _db.Pallets
                .Where(p => p.Status == status)
                .GroupBy(p => p.Description)
                .Select(g => new PalletCountDTO
                {
                    Description = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToList();
        }
        [HttpPost]
        public async Task<IActionResult> DispatchPalletOrders([FromBody] List<PalletCountNullableStoreDTO> transfers, [FromQuery] string type)
        {
            List<Pallet> palletsToUpdate = new List<Pallet>();
            foreach(var transfer in transfers)
            {
                var pallets = _db.Pallets.Where(p => p.Description == transfer.Description && p.Status == type).Take(transfer.Count).ToList();
                palletsToUpdate.AddRange(pallets);
            }
            var status = type.Split(' ')[1];
            foreach (var pallet in palletsToUpdate)
            {
                pallet.Status = status;
                pallet.TransferDate = null;
                pallet.StorageDate = null;
            }
            await _db.SaveChangesAsync();
            return Ok();
        }
        public async Task<IActionResult> FetchPalletsBasedUponDateAndMission(string mission, DateTime date)
        {
            List<PalletAndStoreDTO> DTOs = new List<PalletAndStoreDTO>();
            if (mission != "Awaiting PalletTransfer" && mission != "Awaiting PackingAreaTransfer")
            {
                return BadRequest("Invalid mission type");
            }
            var pallets = _db.Pallets
                 .Where(p =>
                     p.TransferDate.HasValue &&
                     p.TransferDate.Value.Date == date.Date &&
                     p.Status == mission)
                 .GroupBy(p => new
                 {
                     p.Description,
                     p.StoreId
                 })
                 .Select(g => new PalletCountNullableStoreDTO
                 {
                     Description = g.Key.Description,
                     Count = g.Count(),
                     StoreName = _db.Stores
                         .Where(s => s.Id == g.Key.StoreId)
                         .Select(s => s.Name)
                         .FirstOrDefault()
                 })
                 .OrderByDescending(x => x.Count)
                 .ToList();

            if (!pallets.Any())
            {
                return Ok(new List<PalletCountNullableStoreDTO>());
            }
           
            return Ok(pallets);
        }
        public IActionResult CreateStoreAndLoadingDock()
        {
            LoadingdockAndStoreDTO model = new LoadingdockAndStoreDTO
            {
                LoadingDocks = _db.LoadingDocks.ToList(),
                Stores = _storeservice.GetAllStores()
            };
            return View(model);
        }
        [HttpPost]
        public async Task<IActionResult> CreateStore(string name)
        {
            if(_db.LoadingDocks.Count() == 0)
            {
                TempData["Notification"] = "Du måste skapa en lastkaj innan du kan skapa en butik.";
                TempData["NotificationType"] = "error";
                return RedirectToAction("CreateStoreAndLoadingDock");
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Notification"] = "Du måste ange ett namn.";
                TempData["NotificationType"] = "error";

                return RedirectToAction("CreateStoreAndLoadingDock");
            }
            if(_storeservice.CheckIfStoreExists(name))
            {
                TempData["Notification"] = "En butik med det namnet finns redan.";
                TempData["NotificationType"] = "error";

                return RedirectToAction("CreateStoreAndLoadingDock");
            }
            var store = new Store
            {
                Name = name.Trim(),
                Code = _storeservice.GenerateUniqueStoreCode(),
                LoadingDockId = _storeservice.GenerateRandomLoadingDockId()

            };

            _db.Stores.Add(store);
            await _db.SaveChangesAsync();

            TempData["Notification"] = "Butiken " + name + " skapades!";
            TempData["NotificationType"] = "success";

            return RedirectToAction("CreateStoreAndLoadingDock");
        }
        [HttpPost]
        public async Task<IActionResult> CreateLoadingDock(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Notification"] = "Du måste ange ett namn.";
                TempData["NotificationType"] = "error";

                return RedirectToAction("CreateStoreAndLoadingDock");
            }
            if (_storeservice.CheckIfStoreExists(name))
            {
                TempData["Notification"] = "En loading dock med det namnet finns redan.";
                TempData["NotificationType"] = "error";

                return RedirectToAction("CreateStoreAndLoadingDock");
            }
            var loadingdock = new LoadingDock
            {
                Name = name.Trim()
            };

            _db.LoadingDocks.Add(loadingdock);
            await _db.SaveChangesAsync();

            TempData["Notification"] = "Loading dock " + name + " skapades!";
            TempData["NotificationType"] = "success";

            return RedirectToAction("CreateStoreAndLoadingDock");
        }
        [HttpPost]
        public IActionResult PalletsAwaitingStorage(InboundPalletDTO model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            var palletType = _palletservice.GetPalletTypeByDescription(model.Description);
            for (int i = 0; i < model.AmountOfPallets; i++)
            {
                var pallet = new Pallet
                {
                   Description = palletType.Description,
                   Width = palletType.Width,
                   Height = (int)palletType.Height,
                   Weight = (int)palletType.Weight,
                   Amount = (int)palletType.Amount,
                   Category = palletType.Category,
                   Barcode = _palletservice.GenerateUniqueBarcodeForPallet(),
                   Status = "AwaitingStorage"
                };
                _palletservice.AddPallet(pallet);
            }
            return RedirectToAction("Index");
        }
        [HttpGet]
        public IActionResult GetPalletsForDate(DateTime date, string type, int storeId)
        {
            List <PalletCountDTO> p = new List<PalletCountDTO>();
            if(storeId == -1)
            {
                 var result = _db.Pallets
                .Where(p => p.TransferDate.HasValue && p.TransferDate.Value.Date == date.Date && p.Status == type)
                .GroupBy(p => p.Description)
                .Select(g => new
                {
                    Description = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToList();
                for (int i = 0; i < result.Count; i++)
                {
                    p.Add(new PalletCountDTO
                    {
                        Description = result[i].Description,
                        Count = result[i].Count
                    });
                }
            }
            else
            {
                var result = _db.Pallets
                .Where(p => p.TransferDate.HasValue && p.TransferDate.Value.Date == date.Date && p.Status == type && p.StoreId == storeId)
                .GroupBy(p => p.Description)
                .Select(g => new
                {
                    Description = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToList();
                for (int i = 0; i < result.Count; i++)
                {
                    p.Add(new PalletCountDTO
                    {
                        Description = result[i].Description,
                        Count = result[i].Count
                    });
                }
            }
            
            
            return Ok(p);
        }
        [HttpPost]
        public async Task<IActionResult> CreateTransfer([FromBody] List<PalletCountDTO> transfers, [FromQuery] string type, [FromQuery] int storeId)
        {
            if (transfers.Any(t => t.Count <= 0))
            {
                return BadRequest("Transfer quantity must be greater than zero.");
            }
            if (transfers == null || transfers.Count == 0)
            {
                return BadRequest("No transfer data provided.");
            }
            for (int i = 0; i < transfers.Count; i++)
            {
                var palletsToTransfer = _db.Pallets
                    .Where(p => p.Description == transfers[i].Description && p.Status == "Stored")
                    .Take(transfers[i].Count)
                    .ToList();
                foreach (var pallet in palletsToTransfer)
                {
                    pallet.Status = type;
                    pallet.TransferDate = transfers[i].TransferDate;
                    if (storeId != -1)
                        pallet.StoreId = storeId;

                }   

            }
            await _db.SaveChangesAsync();
            return Ok(new { success = true });
        }
        public IActionResult CreatePalletTransferRequests()
        {
            var result = GetPalletCountsBasedOnStatus("Stored");
            return View(result);
        }
        public IActionResult SendOutboundTransferRequests()
        {
            return View();
        }
        public IActionResult CreatePalletPackingAreaTransferRequests()
        {
            PalletCountAndStoreListDTO model = new PalletCountAndStoreListDTO
            {
                PalletCount = GetPalletCountsBasedOnStatus("Stored"),
                Stores = _storeservice.GetAllStores()
            };
            return View(model);
        }
        [HttpGet]
        public IActionResult GetAvailablePallets()
        {
            return Ok(GetPalletCountsBasedOnStatus("Stored"));
        }
        public IActionResult PalletsAwaitingStorage()
        {
            ViewBag.PalletTypes = _palletservice.GetAllPalletTypes();
            return View();
        }
        [HttpPost]
        public async Task<IActionResult> PalletTypeCreation(PalletType model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var description = model.Description?.Trim();

            if (await _db.PalletTypes
                .AnyAsync(p => p.Description == description))
            {
                ModelState.AddModelError("Description", "Denna palltyp finns redan");
                return View(model);
            }

            model.Description = description;

            try
            {
                _palletservice.CreateNewPalletType(model);
            }
            catch (DbUpdateException)
            {
                // Fångar race condition (2 användare samtidigt)
                ModelState.AddModelError("Description", "Denna palltyp finns redan");
                return View(model);
            }

            return RedirectToAction("Index");
        }
        public async Task<IActionResult> PalletTypeCreation()
        {
            return View();
        }
        public IActionResult Index()
        {
            return View();
        }
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Admin()
        {
            var users = _userManager.Users.ToList();
            var userViewModels = new List<UserDTO>();

            foreach (var user in users)
            {
                var roles = await _userManager.GetRolesAsync(user); // returns List<string>
                userViewModels.Add(new UserDTO
                {
                    UserName = user.UserName,
                    Email = user.Email,
                    Role = roles.FirstOrDefault() ?? "Ingen roll"
                });
            }

            return View(userViewModels);
        }
        // --- Create new user (GET) ---
        [HttpGet]
        public IActionResult CreateUser()
        {
            ViewBag.Roles = _roleManager.Roles.ToList();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser(string username, string password, string role)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(role))
            {
                ModelState.AddModelError("", "Alla fält krävs");
                ViewBag.Roles = _roleManager.Roles.ToList();
                return View();
            }
            var existingUser = await _userManager.FindByNameAsync(username);
            if (existingUser != null)
            {
                ModelState.AddModelError("", "Användaren finns redan");
                ViewBag.Roles = _roleManager.Roles.ToList();
                return View();
            }
            var user = new IdentityUser { UserName = username, Email = username, EmailConfirmed = true };
            var result = await _userManager.CreateAsync(user, password);

            if (!result.Succeeded)
            {
                ModelState.AddModelError("", string.Join(", ", result.Errors.Select(e => e.Description)));
                ViewBag.Roles = _roleManager.Roles.ToList();
                return View();
            }

            // Assign role
            if (!await _roleManager.RoleExistsAsync(role))
                await _roleManager.CreateAsync(new IdentityRole(role));

            await _userManager.AddToRoleAsync(user, role);

            return RedirectToAction("Index");
        }
    }
}
