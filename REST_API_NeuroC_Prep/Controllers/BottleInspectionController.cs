using Microsoft.AspNetCore.Mvc;
using REST_API_NeuroC_Prep.Models;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.Controllers;

/// <summary>
/// Industrial bottle inspection endpoint.
/// Detects bottle presence, cap, barcode/QR, and overall defect status.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BottleInspectionController : ControllerBase
{
    private readonly VisionService _vision;

    public BottleInspectionController(VisionService vision) => _vision = vision;

    /// <summary>
    /// Run a full bottle inspection on the current camera frame.
    /// Returns bottle detection, cap status, barcode, and overall verdict.
    /// </summary>
    [HttpGet]
    public ActionResult<BottleInspectionDto> Inspect()
    {
        var result = _vision.InspectBottle();
        if (result == null)
            return StatusCode(503, new MessageDto("Kamera nicht aktiv"));
        return Ok(result);
    }
}
