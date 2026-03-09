using REST_API_NeuroC_Prep.Interop;
using REST_API_NeuroC_Prep.Models;
using REST_API_NeuroC_Prep.Services;

namespace VisionBridge.Tests;

/// <summary>
/// Unit tests for VisionService using SimulatedVisionBackend.
/// No camera or DLL required — the simulation backend generates synthetic data.
/// </summary>
public class VisionServiceTests : IDisposable
{
    private readonly VisionService _svc;

    public VisionServiceTests()
    {
        _svc = new VisionService(new SimulatedVisionBackend());
    }

    public void Dispose() => _svc.Dispose();

    // ===== Camera lifecycle =====

    [Fact]
    public void Start_WhenNotRunning_ReturnsSuccess()
    {
        var (success, _) = _svc.Start();
        Assert.True(success);
    }

    [Fact]
    public void Start_WhenAlreadyRunning_ReturnsSuccessWithMessage()
    {
        _svc.Start();
        var (success, message) = _svc.Start();
        Assert.True(success);
        Assert.Contains("bereits", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stop_WhenRunning_ReturnsSuccess()
    {
        _svc.Start();
        var (success, _) = _svc.Stop();
        Assert.True(success);
    }

    [Fact]
    public void GetStatus_WhenStopped_ReturnsFalse()
    {
        var status = _svc.GetStatus();
        Assert.False(status.Running);
    }

    [Fact]
    public void GetStatus_WhenStarted_ReturnsTrue()
    {
        _svc.Start();
        var status = _svc.GetStatus();
        Assert.True(status.Running);
    }

    // ===== Plant control =====

    [Fact]
    public void ConveyorSpeed_ClampedToRange()
    {
        _svc.ConveyorSpeed = 99.0;
        Assert.Equal(5.0, _svc.ConveyorSpeed);

        _svc.ConveyorSpeed = -1.0;
        Assert.Equal(0.0, _svc.ConveyorSpeed);
    }

    [Fact]
    public void ConveyorSpeed_ValidValue_Stored()
    {
        _svc.ConveyorSpeed = 2.5;
        Assert.Equal(2.5, _svc.ConveyorSpeed);
    }

    [Fact]
    public void GetPlantControl_ReflectsCurrentState()
    {
        _svc.ConveyorSpeed = 3.0;
        _svc.InspectionEnabled = false;
        _svc.RejectGateOpen = true;

        var dto = _svc.GetPlantControl();

        Assert.Equal(3.0, dto.ConveyorSpeed);
        Assert.False(dto.InspectionEnabled);
        Assert.True(dto.RejectGateOpen);
    }

    // ===== Detection — requires camera started =====

    [Fact]
    public void DetectColor_WhenStopped_ReturnsNull()
    {
        var result = _svc.DetectColor();
        Assert.Null(result);
    }

    [Fact]
    public void DetectColor_WhenStarted_ReturnsResult()
    {
        _svc.Start();
        var result = _svc.DetectColor();
        Assert.NotNull(result);
    }

    [Fact]
    public void DetectColor_Result_HasInspectionIdAndTimestamp()
    {
        _svc.Start();
        var result = _svc.DetectColor();
        Assert.NotNull(result);
        Assert.True(result.InspectionId > 0);
        Assert.NotNull(result.Timestamp);
    }

    [Fact]
    public void DetectColor_Confidence_InValidRange()
    {
        _svc.Start();
        // Run a few ticks so the simulated object is visible
        for (int i = 0; i < 10; i++) _svc.DetectColor();

        var result = _svc.DetectColor();
        Assert.NotNull(result);
        Assert.InRange(result.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void DetectFaces_WhenStopped_ReturnsNull()
    {
        var result = _svc.DetectFaces();
        Assert.Null(result);
    }

    [Fact]
    public void DetectFaces_WhenStarted_ReturnsResult()
    {
        _svc.Start();
        _svc.LoadCascade(); // loads in simulation mode (always succeeds)
        var result = _svc.DetectFaces();
        Assert.NotNull(result);
        Assert.Equal("face", result.Type);
    }

    [Fact]
    public void DetectCircles_WhenStarted_ReturnsResult()
    {
        _svc.Start();
        var result = _svc.DetectCircles();
        Assert.NotNull(result);
        Assert.Equal("circle", result.Type);
    }

    // ===== Bottle inspection =====

    [Fact]
    public void InspectBottle_WhenStopped_ReturnsNull()
    {
        var result = _svc.InspectBottle();
        Assert.Null(result);
    }

    [Fact]
    public void InspectBottle_WhenStarted_ReturnsResult()
    {
        _svc.Start();
        var result = _svc.InspectBottle();
        Assert.NotNull(result);
    }

    [Fact]
    public void InspectBottle_Status_IsKnownValue()
    {
        _svc.Start();
        var result = _svc.InspectBottle();
        Assert.NotNull(result);
        Assert.True(
            result.BottleStatus == BottleStatusEnum.None ||
            result.BottleStatus == BottleStatusEnum.Ok ||
            result.BottleStatus == BottleStatusEnum.Defect);
    }

    [Fact]
    public void InspectBottle_HasInspectionId()
    {
        _svc.Start();
        var result = _svc.InspectBottle();
        Assert.NotNull(result);
        Assert.True(result.InspectionId > 0);
    }

    [Fact]
    public void InspectBottle_CapBoundingBox_PresentWhenCapDetected()
    {
        _svc.Start();
        // Run multiple ticks to cycle through simulation scenarios (cap present = cycle 0 or 1)
        BottleInspectionDto? capPresent = null;
        for (int i = 0; i < 300 && capPresent == null; i++)
        {
            var r = _svc.InspectBottle();
            if (r?.CapDetected == true) capPresent = r;
            // Advance simulation tick by calling another detection
            _svc.DetectColor();
        }

        if (capPresent != null)
        {
            Assert.NotNull(capPresent.CapBoundingBox);
            Assert.True(capPresent.CapBoundingBox.Width > 0);
            Assert.True(capPresent.CapBoundingBox.Height > 0);
        }
    }

    [Fact]
    public void InspectBottle_Defect_WhenCapMissing()
    {
        _svc.Start();
        // Force simulation to a no-cap cycle (cycle 2 or 3: cap absent)
        BottleInspectionDto? defect = null;
        for (int i = 0; i < 500 && defect == null; i++)
        {
            var r = _svc.InspectBottle();
            if (r?.BottleDetected == true && !r.CapDetected)
                defect = r;
            _svc.DetectColor(); // advance tick
        }

        if (defect != null)
            Assert.Equal(BottleStatusEnum.Defect, defect.BottleStatus);
    }

    // ===== Result cache =====

    [Fact]
    public void InspectBottle_CalledTwiceRapidly_ReturnsSameInspectionId()
    {
        _svc.Start();
        var first = _svc.InspectBottle();
        var second = _svc.InspectBottle(); // within 100ms cache window

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.InspectionId, second.InspectionId);
    }

    // ===== Diagnostics =====

    [Fact]
    public void GetDiagnostics_BackendMode_IsSimulated()
    {
        var diag = _svc.GetDiagnostics();
        Assert.Equal("Simulated", diag.BackendMode);
    }

    [Fact]
    public void GetDiagnostics_TotalInspections_IncreasesOverTime()
    {
        _svc.Start();
        var before = _svc.GetDiagnostics().TotalInspections;
        _svc.DetectColor();
        _svc.DetectCircles();
        var after = _svc.GetDiagnostics().TotalInspections;
        Assert.True(after > before);
    }

    [Fact]
    public void GetDiagnostics_Uptime_IsFormattedString()
    {
        var diag = _svc.GetDiagnostics();
        // Uptime is formatted as HH:mm:ss — verify the format, not the exact value
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", diag.Uptime);
    }
}
