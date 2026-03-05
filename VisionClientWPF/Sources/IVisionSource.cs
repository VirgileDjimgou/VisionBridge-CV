namespace VisionClientWPF.Sources;

// Gemeinsame Ergebnistypen für alle Vision-Quellen

public readonly record struct DetectionBox(int X, int Y, int Width, int Height);

public record FrameResult(int Width, int Height, byte[] RgbData);

public record ColorResult(bool Detected, DetectionBox Box);

public record MultiResult(int Count, DetectionBox[] Items);

public record EdgeResult(int Width, int Height, byte[] Data);

/// <summary>
/// Abstraktion über verschiedene Vision-Datenquellen.
/// Lokal (P/Invoke), REST API (HTTP) oder OPC-UA.
/// </summary>
public interface IVisionSource : IDisposable
{
    string Name { get; }
    bool SupportsVideo { get; }
    bool SupportsEdgeDetection { get; }

    bool Start();
    void Stop();

    FrameResult? GetFrameRgb();
    ColorResult? DetectColor();
    MultiResult? DetectFaces();
    MultiResult? DetectCircles();
    EdgeResult? DetectEdges();
}
