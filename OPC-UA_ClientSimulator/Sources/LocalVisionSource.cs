using System.IO;
using System.Net.NetworkInformation;

namespace OPC_UA_ClientSimulator.Sources;

/// <summary>
/// P/Invoke direkt auf NeuroCComVision.dll — schnellster Zugriff, volles Video.
/// Setzt voraus, dass die DLL lokal vorhanden ist.
/// Keine Anlagen-Steuerung (kein IPlantControl).
/// </summary>
public class LocalVisionSource : IVisionSource
{
    public string Name => "Lokal (P/Invoke)";
    public bool SupportsVideo => true;
    public bool SupportsEdgeDetection => true;
    public bool SupportsDiagnostics => false;

    private byte[]? _frameBuffer;
    private bool _started;

    public bool Start()
    {
        // 1. DLL vorhanden?
        string dllPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "NeuroCComVision.dll");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                "NeuroCComVision.dll wurde nicht gefunden.\n\n" +
                "Die DLL muss im Ausgabeverzeichnis liegen,\n" +
                "zusammen mit den OpenCV-Abhängigkeiten.");

        // 2. Läuft der VisionBridge Runtime bereits? (Kamerakonflikt)
        if (IsVisionBridgeServerRunning())
            throw new InvalidOperationException(
                "Der VisionBridge Runtime (REST API / OPC-UA Server) läuft bereits\n" +
                "und belegt die Kamera.\n\n" +
                "Optionen:\n" +
                "• Server beenden und erneut versuchen\n" +
                "• Quelle \"REST API\" oder \"OPC-UA\" verwenden");

        // 3. Kamera öffnen (P/Invoke)
        try
        {
            if (!VisionInterop.StartCamera())
                throw new InvalidOperationException(
                    "Die Kamera konnte nicht geöffnet werden.\n\n" +
                    "Mögliche Ursachen:\n" +
                    "• Keine Kamera angeschlossen\n" +
                    "• Kamera wird von einer anderen Anwendung verwendet\n" +
                    "• Kameratreiber nicht installiert");
        }
        catch (DllNotFoundException)
        {
            throw new FileNotFoundException(
                "NeuroCComVision.dll konnte nicht geladen werden.\n\n" +
                "Stellen Sie sicher, dass die DLL und alle Abhängigkeiten\n" +
                "(OpenCV DLLs) im Ausgabeverzeichnis vorhanden sind.");
        }
        catch (InvalidOperationException)
        {
            throw; // eigene Exceptions weiterleiten
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Kamera-Initialisierung fehlgeschlagen:\n\n{ex.Message}");
        }

        _started = true;

        string cascadePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "haarcascade_frontalface_default.xml");

        if (File.Exists(cascadePath))
            VisionInterop.LoadFaceCascade(cascadePath);

        return true;
    }

    /// <summary>
    /// Prüft ob der VisionBridge Runtime bereits auf den bekannten Ports lauscht.
    /// Port 7158 = REST API (HTTPS), Port 4840 = OPC-UA Server.
    /// </summary>
    private static bool IsVisionBridgeServerRunning()
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners();
            return listeners.Any(ep => ep.Port is 7158 or 4840);
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        if (!_started) return;
        try { VisionInterop.StopCamera(); } catch (DllNotFoundException) { }
        _started = false;
    }

    public FrameResult? GetFrameRgb()
    {
        if (!VisionInterop.GetFrameInfo(out var info))
            return null;

        int rgbSize = info.width * info.height * 3;
        if (_frameBuffer == null || _frameBuffer.Length < rgbSize)
            _frameBuffer = new byte[rgbSize];

        if (!VisionInterop.GetFrameBytesRgb(_frameBuffer, rgbSize))
            return null;

        return new FrameResult(info.width, info.height, _frameBuffer);
    }

    public ColorResult? DetectColor()
    {
        if (!VisionInterop.GetFrame(out var result))
            return null;

        return new ColorResult(
            result.detected,
            new DetectionBox(result.x, result.y, result.width, result.height));
    }

    public MultiResult? DetectFaces()
    {
        if (!VisionInterop.DetectFaces(out var result))
            return null;

        var items = new DetectionBox[result.count];
        for (int i = 0; i < result.count; i++)
        {
            var item = result.items[i];
            items[i] = new DetectionBox(item.x, item.y, item.width, item.height);
        }
        return new MultiResult(result.count, items);
    }

    public MultiResult? DetectCircles()
    {
        if (!VisionInterop.DetectCircles(out var result))
            return null;

        var items = new DetectionBox[result.count];
        for (int i = 0; i < result.count; i++)
        {
            var item = result.items[i];
            items[i] = new DetectionBox(item.x, item.y, item.width, item.height);
        }
        return new MultiResult(result.count, items);
    }

    public EdgeResult? DetectEdges()
    {
        if (!VisionInterop.GetFrameInfo(out var info))
            return null;

        int bufferSize = info.width * info.height;
        byte[] edgeBuffer = new byte[bufferSize];

        if (!VisionInterop.DetectEdges(edgeBuffer, bufferSize, out int w, out int h))
            return null;

        return new EdgeResult(w, h, edgeBuffer);
    }

    public RuntimeDiagnostics? GetDiagnostics() => null;
    public void Dispose() => Stop();
}
