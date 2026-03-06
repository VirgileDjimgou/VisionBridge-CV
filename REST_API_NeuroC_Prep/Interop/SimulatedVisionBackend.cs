namespace REST_API_NeuroC_Prep.Interop;

/// <summary>
/// Simulationsmodus — generiert synthetische Vision-Daten ohne Kamera/DLL.
/// Simuliert ein industrielles Förderband-Szenario:
/// rote Objekte bewegen sich sinusförmig, Gesichter/Kreise variieren zyklisch.
/// </summary>
public sealed class SimulatedVisionBackend : IVisionBackend
{
    private bool _running;
    private bool _cascadeLoaded;
    private int _tick;

    private const int Width = 640;
    private const int Height = 480;
    private const int Channels = 3;
    private const int Stride = Width * Channels;

    public bool StartCamera()
    {
        _running = true;
        _tick = 0;
        return true;
    }

    public void StopCamera()
    {
        _running = false;
    }

    public bool GetFrame(out NativeInterop.DetectionResult result)
    {
        if (!_running)
        {
            result = default;
            return false;
        }

        _tick++;

        // Rotes Objekt bewegt sich sinusförmig (Förderband-Simulation)
        bool detected = _tick % 120 < 90; // 75% der Zeit sichtbar
        int x = (int)(Width / 2 + Math.Sin(_tick * 0.05) * 200);
        int y = Height / 2 + (int)(Math.Cos(_tick * 0.03) * 50);

        result = new NativeInterop.DetectionResult
        {
            detected = detected,
            x = Math.Clamp(x - 40, 0, Width - 80),
            y = Math.Clamp(y - 40, 0, Height - 80),
            width = 80,
            height = 80
        };
        return true;
    }

    public bool GetFrameInfo(out NativeInterop.FrameInfo info)
    {
        if (!_running)
        {
            info = default;
            return false;
        }

        info = new NativeInterop.FrameInfo
        {
            width = Width,
            height = Height,
            stride = Stride,
            channels = Channels,
            totalBytes = Stride * Height
        };
        return true;
    }

    public bool GetFrameBytes(byte[] buffer, int bufferSize)
    {
        if (!_running) return false;

        int needed = Stride * Height;
        if (bufferSize < needed) return false;

        // Industrieller Blau-Grau-Hintergrund mit Gradient
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Stride + x * 3;
                if (i + 2 >= bufferSize) break;

                buffer[i] = (byte)(40 + y * 20 / Height);     // B
                buffer[i + 1] = (byte)(35 + y * 15 / Height); // G
                buffer[i + 2] = (byte)(30 + y * 10 / Height); // R
            }
        }

        // Rotes Quadrat zeichnen (simuliertes erkanntes Objekt)
        if (GetFrame(out var det) && det.detected)
        {
            for (int dy = 0; dy < det.height && det.y + dy < Height; dy++)
            {
                for (int dx = 0; dx < det.width && det.x + dx < Width; dx++)
                {
                    int i = (det.y + dy) * Stride + (det.x + dx) * 3;
                    if (i + 2 >= bufferSize) continue;
                    buffer[i] = 30;       // B
                    buffer[i + 1] = 30;   // G
                    buffer[i + 2] = 200;  // R
                }
            }
        }

        return true;
    }

    public bool GetFrameBytesRgb(byte[] buffer, int bufferSize)
    {
        if (!_running) return false;

        // BGR generieren, dann BGR→RGB swap
        byte[] bgr = new byte[bufferSize];
        if (!GetFrameBytes(bgr, bufferSize)) return false;

        for (int i = 0; i + 2 < bufferSize; i += 3)
        {
            buffer[i] = bgr[i + 2];     // R
            buffer[i + 1] = bgr[i + 1]; // G
            buffer[i + 2] = bgr[i];     // B
        }
        return true;
    }

    public bool LoadFaceCascade(string cascadePath)
    {
        // Im Simulationsmodus immer erfolgreich
        _cascadeLoaded = true;
        return true;
    }

    public bool DetectFaces(out NativeInterop.MultiDetectionResult result)
    {
        if (!_running || !_cascadeLoaded)
        {
            result = default;
            return false;
        }

        // 1-3 simulierte Gesichter, Position variiert zyklisch
        int count = (_tick / 60) % 3 + 1;
        result = new NativeInterop.MultiDetectionResult
        {
            items = new NativeInterop.DetectionResult[32],
            count = count
        };

        for (int i = 0; i < count; i++)
        {
            result.items[i] = new NativeInterop.DetectionResult
            {
                detected = true,
                x = 100 + i * 180 + (int)(Math.Sin(_tick * 0.02 + i) * 20),
                y = 120 + (int)(Math.Cos(_tick * 0.03 + i) * 15),
                width = 90,
                height = 90
            };
        }

        return true;
    }

    public bool DetectEdges(byte[] outputBuffer, int bufferSize, out int outWidth, out int outHeight)
    {
        if (!_running)
        {
            outWidth = outHeight = 0;
            return false;
        }

        outWidth = Width;
        outHeight = Height;

        int needed = Width * Height;
        if (bufferSize < needed)
        {
            outWidth = outHeight = 0;
            return false;
        }

        // Simulierte Kanten: Raster-Muster (Prüffeld-Stil)
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                bool edge = (x % 40 < 2) || (y % 40 < 2);
                outputBuffer[i] = edge ? (byte)255 : (byte)0;
            }
        }
        return true;
    }

    public bool DetectCircles(out NativeInterop.MultiDetectionResult result)
    {
        if (!_running)
        {
            result = default;
            return false;
        }

        // 2-4 simulierte Kreise (Bohrlocherkennung)
        int count = (_tick / 90) % 3 + 2;
        result = new NativeInterop.MultiDetectionResult
        {
            items = new NativeInterop.DetectionResult[32],
            count = count
        };

        for (int i = 0; i < count; i++)
        {
            int cx = 80 + i * 150;
            int cy = 240 + (int)(Math.Sin(_tick * 0.01 + i * 2) * 30);
            result.items[i] = new NativeInterop.DetectionResult
            {
                detected = true,
                x = cx - 25,
                y = cy - 25,
                width = 50,
                height = 50
            };
        }
        return true;
    }
}
