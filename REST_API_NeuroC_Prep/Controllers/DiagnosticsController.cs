using Microsoft.AspNetCore.Mvc;
using REST_API_NeuroC_Prep.Models;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.Controllers
{
    /// <summary>
    /// Health Check und Runtime-Metriken.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class DiagnosticsController : ControllerBase
    {
        private readonly VisionService _vision;

        public DiagnosticsController(VisionService vision) => _vision = vision;

        /// <summary>
        /// Runtime-Diagnose: Uptime, Backend-Modus, FPS, Inspektionszähler,
        /// Anlagenzustand und letzte Erkennung.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(DiagnosticsDto), 200)]
        public IActionResult GetDiagnostics()
        {
            return Ok(_vision.GetDiagnostics());
        }

        /// <summary>Einfacher Health-Check — 200 wenn der Service läuft.</summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            var diag = _vision.GetDiagnostics();
            return Ok(new
            {
                status = "healthy",
                camera = diag.CameraRunning,
                backend = diag.BackendMode,
                uptime = diag.Uptime
            });
        }
    }
}
