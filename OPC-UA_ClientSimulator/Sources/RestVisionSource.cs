using System.Net.Http;
using System.Text.Json;

namespace OPC_UA_ClientSimulator.Sources;

/// <summary>
/// Vision-Daten über die REST API (HTTP/JSON).
/// Video via Base64-Frames (~5 FPS), alle Erkennungsmodi verfügbar.
/// Implementiert IPlantControl über die REST-Endpunkte
/// (/api/plant/*, /api/camera/*).
/// </summary>
public class RestVisionSource : IVisionSource, IPlantControl
{
    public string Name => "REST API (HTTP)";
    public bool SupportsVideo => true;
    public bool SupportsEdgeDetection => true;
    public bool SupportsDiagnostics => true;

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RestVisionSource(string baseUrl = "https://localhost:7158")
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public bool Start()
    {
        try
        {
            var response = Task.Run(() => _http.PostAsync("api/camera/start", null)).Result;
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Stop()
    {
        try { Task.Run(() => _http.PostAsync("api/camera/stop", null)).Wait(); }
        catch { }
    }

    public FrameResult? GetFrameRgb()
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync("api/frame/rgb")).Result;
            var frame = JsonSerializer.Deserialize<FrameDto>(json, JsonOpts);
            if (frame?.Base64Data == null) return null;

            byte[] rgb = Convert.FromBase64String(frame.Base64Data);
            return new FrameResult(frame.Width, frame.Height, rgb);
        }
        catch { return null; }
    }

    public ColorResult? DetectColor()
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync("api/detection/color")).Result;
            var r = JsonSerializer.Deserialize<ColorDto>(json, JsonOpts);
            if (r == null) return null;

            var b = r.BoundingBox;
            return new ColorResult(
                r.Detected,
                b != null ? new DetectionBox(b.X, b.Y, b.Width, b.Height) : default,
                r.Confidence);
        }
        catch { return null; }
    }

    public MultiResult? DetectFaces() => GetMulti("api/detection/faces");
    public MultiResult? DetectCircles() => GetMulti("api/detection/circles");

    public EdgeResult? DetectEdges()
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync("api/detection/edges")).Result;
            var r = JsonSerializer.Deserialize<EdgeDto>(json, JsonOpts);
            if (r?.Base64Data == null) return null;

            byte[] data = Convert.FromBase64String(r.Base64Data);
            return new EdgeResult(r.Width, r.Height, data);
        }
        catch { return null; }
    }

    public RuntimeDiagnostics? GetDiagnostics()
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync("api/diagnostics")).Result;
            var r = JsonSerializer.Deserialize<DiagDto>(json, JsonOpts);
            if (r == null) return null;
            return new RuntimeDiagnostics(r.Uptime, r.BackendMode, r.CameraRunning,
                r.TotalInspections, r.CurrentFps);
        }
        catch { return null; }
    }

    public BottleInspection? InspectBottle()
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync("api/bottleinspection")).Result;
            var r = JsonSerializer.Deserialize<BottleInspDto>(json, JsonOpts);
            if (r == null) return null;

            return new BottleInspection(
                r.BottleDetected,
                r.BottleBoundingBox != null
                    ? new DetectionBox(r.BottleBoundingBox.X, r.BottleBoundingBox.Y,
                        r.BottleBoundingBox.Width, r.BottleBoundingBox.Height)
                    : default,
                r.BottleConfidence,
                r.CapDetected,
                r.CapBoundingBox != null
                    ? new DetectionBox(r.CapBoundingBox.X, r.CapBoundingBox.Y,
                        r.CapBoundingBox.Width, r.CapBoundingBox.Height)
                    : default,
                r.BarcodeDetected, r.QrDetected, r.DecodedValue,
                r.BottleStatus, r.DefectCount);
        }
        catch { return null; }
    }

    // ===== IPlantControl (via REST) =====

    public void CameraStart()
    {
        try { Task.Run(() => _http.PostAsync("api/camera/start", null)).Wait(); }
        catch { }
    }

    public void CameraStop()
    {
        try { Task.Run(() => _http.PostAsync("api/camera/stop", null)).Wait(); }
        catch { }
    }

    public void SetConveyorSpeed(double speed)
    {
        try { Task.Run(() => _http.PostAsync($"api/plant/conveyor-speed?speed={speed}", null)).Wait(); }
        catch { }
    }

    public void SetInspectionEnabled(bool enabled)
    {
        try { Task.Run(() => _http.PostAsync($"api/plant/inspection?enabled={enabled.ToString().ToLower()}", null)).Wait(); }
        catch { }
    }

    public void SetRejectGateOpen(bool open)
    {
        try { Task.Run(() => _http.PostAsync($"api/plant/reject-gate?open={open.ToString().ToLower()}", null)).Wait(); }
        catch { }
    }

    public void Dispose() => _http.Dispose();

    // ===== Hilfsmethoden =====

    private MultiResult? GetMulti(string endpoint)
    {
        try
        {
            var json = Task.Run(() => _http.GetStringAsync(endpoint)).Result;
            var r = JsonSerializer.Deserialize<MultiDto>(json, JsonOpts);
            if (r == null) return null;

            var items = r.Detections?
                .Select(d => new DetectionBox(d.BoundingBox.X, d.BoundingBox.Y,
                                              d.BoundingBox.Width, d.BoundingBox.Height))
                .ToArray() ?? [];
            return new MultiResult(r.Count, items, r.Confidence);
        }
        catch { return null; }
    }

    // JSON-Antwortmodelle
    private class FrameDto { public int Width { get; set; } public int Height { get; set; } public string? Base64Data { get; set; } }
    private class ColorDto { public bool Detected { get; set; } public BoxDto? BoundingBox { get; set; } public double Confidence { get; set; } }
    private class BoxDto { public int X { get; set; } public int Y { get; set; } public int Width { get; set; } public int Height { get; set; } }
    private class MultiDto { public int Count { get; set; } public double Confidence { get; set; } public DetItemDto[]? Detections { get; set; } }
    private class DetItemDto { public BoxDto BoundingBox { get; set; } = new(); }
    private class EdgeDto { public int Width { get; set; } public int Height { get; set; } public string? Base64Data { get; set; } }
    private class DiagDto { public string Uptime { get; set; } = ""; public string BackendMode { get; set; } = ""; public bool CameraRunning { get; set; } public long TotalInspections { get; set; } public double CurrentFps { get; set; } }
    private class BottleInspDto { public bool BottleDetected { get; set; } public BoxDto? BottleBoundingBox { get; set; } public double BottleConfidence { get; set; } public bool CapDetected { get; set; } public BoxDto? CapBoundingBox { get; set; } public bool BarcodeDetected { get; set; } public bool QrDetected { get; set; } public string? DecodedValue { get; set; } public int BottleStatus { get; set; } public int DefectCount { get; set; } }
}
