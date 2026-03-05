using System.IO;

namespace VisionClientWPF.Sources;

/// <summary>
/// P/Invoke direkt auf NeuroCComVision.dll — schnellster Zugriff, volles Video.
/// Setzt voraus, dass die DLL lokal vorhanden ist.
/// </summary>
public class LocalVisionSource : IVisionSource
{
    public string Name => "Lokal (P/Invoke)";
    public bool SupportsVideo => true;
    public bool SupportsEdgeDetection => true;

    private byte[]? _frameBuffer;

    public bool Start()
    {
        if (!VisionInterop.StartCamera())
            return false;

        string cascadePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "haarcascade_frontalface_default.xml");

        if (File.Exists(cascadePath))
            VisionInterop.LoadFaceCascade(cascadePath);

        return true;
    }

    public void Stop() => VisionInterop.StopCamera();

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

    public void Dispose() => Stop();
}
