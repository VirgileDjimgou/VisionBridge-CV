using Microsoft.AspNetCore.Mvc;
using REST_API_NeuroC_Prep.Models;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.Controllers
{
    /// <summary>
    /// Anlagen-Steuerung: Förderband-Geschwindigkeit, Inspektion, Ausschleusweiche.
    /// Beschreibbar via REST — spiegelt den OPC-UA Control-Ordner.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class PlantController : ControllerBase
    {
        private readonly VisionService _vision;

        public PlantController(VisionService vision) => _vision = vision;

        /// <summary>Aktuellen Anlagenzustand abrufen.</summary>
        [HttpGet]
        [ProducesResponseType(typeof(PlantControlDto), 200)]
        public IActionResult GetPlantControl()
        {
            return Ok(_vision.GetPlantControl());
        }

        /// <summary>Förderband-Geschwindigkeit setzen (0–5 m/s).</summary>
        [HttpPost("conveyor-speed")]
        [ProducesResponseType(typeof(PlantControlDto), 200)]
        public IActionResult SetConveyorSpeed([FromQuery] double speed)
        {
            _vision.ConveyorSpeed = speed;
            return Ok(_vision.GetPlantControl());
        }

        /// <summary>Inspektion aktivieren / deaktivieren.</summary>
        [HttpPost("inspection")]
        [ProducesResponseType(typeof(PlantControlDto), 200)]
        public IActionResult SetInspectionEnabled([FromQuery] bool enabled)
        {
            _vision.InspectionEnabled = enabled;
            return Ok(_vision.GetPlantControl());
        }

        /// <summary>Ausschleusweiche öffnen / schließen.</summary>
        [HttpPost("reject-gate")]
        [ProducesResponseType(typeof(PlantControlDto), 200)]
        public IActionResult SetRejectGate([FromQuery] bool open)
        {
            _vision.RejectGateOpen = open;
            return Ok(_vision.GetPlantControl());
        }
    }
}
