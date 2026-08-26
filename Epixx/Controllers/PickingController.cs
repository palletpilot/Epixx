using Microsoft.AspNetCore.Mvc;

namespace Epixx.Controllers
{
    public class PickingController : Controller
    {
        public IActionResult AutomaticPickingMission()
        {
            return View();
        }
    }
}
