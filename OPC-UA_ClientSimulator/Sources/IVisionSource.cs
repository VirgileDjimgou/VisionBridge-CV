namespace OPC_UA_ClientSimulator.Sources;

// Gemeinsame Ergebnistypen für alle Vision-Quellen

public readonly record struct DetectionBox(int X, int Y, int Width, int Height);

public record FrameResult(int Width, int Height, byte[] RgbData);

public record ColorResult(bool Detected, DetectionBox Box, double Confidence = 0);

public record MultiResult(int Count, DetectionBox[] Items, double Confidence = 0);

public record EdgeResult(int Width, int Height, byte[] Data);

public record RuntimeDiagnostics(
    string Uptime, string BackendMode, bool CameraRunning,
    long TotalInspections, double CurrentFps);

/// <summary>
/// Abstraktion über verschiedene Vision-Datenquellen.
/// Lokal (P/Invoke), REST API (HTTP) oder OPC-UA.
/// </summary>
public interface IVisionSource : IDisposable
{
    string Name { get; }
    bool SupportsVideo { get; }
    bool SupportsEdgeDetection { get; }
    bool SupportsDiagnostics { get; }

    bool Start();
    void Stop();

    FrameResult? GetFrameRgb();
    ColorResult? DetectColor();
    MultiResult? DetectFaces();
    MultiResult? DetectCircles();
    EdgeResult? DetectEdges();
    RuntimeDiagnostics? GetDiagnostics();
}

/// <summary>
/// Optionale Schnittstelle für Anlagen-Steuerung.
/// Implementiert von OPC-UA- und REST-Quellen.
/// Erlaubt Kamera-Steuerung, Förderband-Geschwindigkeit,
/// Inspektions-Toggle und Ausschleusweiche.
/// </summary>
public interface IPlantControl
{
    void SetConveyorSpeed(double speed);
    void SetInspectionEnabled(bool enabled);
    void SetRejectGateOpen(bool open);
    void CameraStart();
    void CameraStop();
}
