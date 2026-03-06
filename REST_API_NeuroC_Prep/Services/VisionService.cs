using REST_API_NeuroC_Prep.Interop;
using REST_API_NeuroC_Prep.Models;

namespace REST_API_NeuroC_Prep.Services
{
    /// <summary>
    /// Thread-sicherer Service, der alle nativen OpenCV-Funktionen
    /// kapselt. Wird als Singleton registriert (eine Kamera = eine Instanz).
    /// Das konkrete Backend (Native oder Simulation) wird per DI injiziert.
    /// </summary>
    public sealed class VisionService : IDisposable
    {
        private readonly object _lock = new();
        private readonly IVisionBackend _backend;
        private bool _running;
        private bool _cascadeLoaded;

        public VisionService(IVisionBackend backend)
        {
            _backend = backend;
        }

        // ===== Kamera-Steuerung =====

        public CameraStatusDto GetStatus()
        {
            lock (_lock)
            {
                return new CameraStatusDto(
                    _running,
                    _running ? "Kamera aktiv" : "Kamera gestoppt");
            }
        }

        public (bool Success, string Message) Start()
        {
            lock (_lock)
            {
                if (_running)
                    return (true, "Kamera läuft bereits");

                if (!_backend.StartCamera())
                    return (false, "Kamera konnte nicht geöffnet werden (Hardware-Fehler oder bereits belegt)");

                _running = true;

                // Cascade automatisch laden
                if (!_cascadeLoaded)
                    TryLoadCascadeInternal();

                return (true, "Kamera erfolgreich gestartet");
            }
        }

        public (bool Success, string Message) Stop()
        {
            lock (_lock)
            {
                if (!_running)
                    return (true, "Kamera war bereits gestoppt");

                _backend.StopCamera();
                _running = false;
                return (true, "Kamera gestoppt");
            }
        }

        // ===== Cascade laden =====

        public (bool Success, string Message) LoadCascade(string? path = null)
        {
            lock (_lock)
            {
                return TryLoadCascadeInternal(path);
            }
        }

        private (bool Success, string Message) TryLoadCascadeInternal(string? path = null)
        {
            string cascadePath = path ?? Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "haarcascade_frontalface_default.xml");

            if (_backend is NativeVisionBackend && !File.Exists(cascadePath))
                return (false, $"Cascade-Datei nicht gefunden: {cascadePath}");

            if (_backend.LoadFaceCascade(cascadePath))
            {
                _cascadeLoaded = true;
                return (true, "Cascade erfolgreich geladen");
            }

            return (false, "Cascade konnte nicht geladen werden (ungültiges Format?)");
        }

        // ===== Frame-Informationen =====

        public FrameInfoDto? GetFrameInfo()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.GetFrameInfo(out var info)) return null;

                return new FrameInfoDto(
                    info.width, info.height,
                    info.stride, info.channels,
                    info.totalBytes);
            }
        }

        // ===== Frame als RGB-Bytes =====

        public FrameBase64Dto? GetFrameRgb()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.GetFrameInfo(out var info)) return null;

                int rgbSize = info.width * info.height * 3;
                byte[] buffer = new byte[rgbSize];

                if (!_backend.GetFrameBytesRgb(buffer, rgbSize))
                    return null;

                return new FrameBase64Dto(
                    info.width, info.height, 3,
                    Convert.ToBase64String(buffer));
            }
        }

        /// <summary>Gibt den aktuellen Frame als PNG-Bild zurück.</summary>
        public byte[]? GetFrameAsPng()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.GetFrameInfo(out var info)) return null;

                int bgrSize = info.stride * info.height;
                byte[] bgrBuffer = new byte[bgrSize];

                if (!_backend.GetFrameBytes(bgrBuffer, bgrSize))
                    return null;

                // BGR-Rohdaten → BMP → PNG über System.Drawing-freien Weg
                return ConvertBgrToBmpBytes(bgrBuffer, info.width, info.height, info.stride);
            }
        }

        // ===== Farberkennung (Rot) =====

        public ColorDetectionDto? DetectColor()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.GetFrame(out var result)) return null;

                return new ColorDetectionDto(
                    result.detected,
                    result.detected
                        ? new BoundingBoxDto(result.x, result.y, result.width, result.height)
                        : null);
            }
        }

        // ===== Gesichtserkennung =====

        public MultiDetectionDto? DetectFaces()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_cascadeLoaded) return null;
                if (!_backend.DetectFaces(out var result)) return null;

                return ToMultiDto("face", result);
            }
        }

        // ===== Kreiserkennung =====

        public MultiDetectionDto? DetectCircles()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.DetectCircles(out var result)) return null;

                return ToMultiDto("circle", result);
            }
        }

        // ===== Kantenerkennung =====

        public EdgeDetectionDto? DetectEdges()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_backend.GetFrameInfo(out var info)) return null;

                int bufferSize = info.width * info.height;
                byte[] edgeBuffer = new byte[bufferSize];

                if (!_backend.DetectEdges(edgeBuffer, bufferSize,
                        out int w, out int h))
                    return null;

                return new EdgeDetectionDto(w, h, Convert.ToBase64String(edgeBuffer, 0, w * h));
            }
        }

        // ===== Hilfsmethoden =====

        private static MultiDetectionDto ToMultiDto(string type,
            NativeInterop.MultiDetectionResult result)
        {
            var items = new List<DetectionItemDto>();
            for (int i = 0; i < result.count; i++)
            {
                var item = result.items[i];
                items.Add(new DetectionItemDto(i,
                    new BoundingBoxDto(item.x, item.y, item.width, item.height)));
            }
            return new MultiDetectionDto(type, result.count, items);
        }

        /// <summary>
        /// Erzeugt ein unkomprimiertes BMP aus BGR-Rohdaten
        /// (ohne System.Drawing-Abhängigkeit).
        /// </summary>
        private static byte[] ConvertBgrToBmpBytes(byte[] bgr, int w, int h, int srcStride)
        {
            int rowBytes = w * 3;
            int bmpStride = (rowBytes + 3) & ~3; // auf 4 Byte ausrichten
            int dataSize = bmpStride * h;
            int fileSize = 54 + dataSize;

            byte[] bmp = new byte[fileSize];

            // BMP-Header
            bmp[0] = 0x42; bmp[1] = 0x4D; // "BM"
            BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);        // Offset zu Pixeldaten
            BitConverter.GetBytes(40).CopyTo(bmp, 14);        // DIB-Header-Größe
            BitConverter.GetBytes(w).CopyTo(bmp, 18);
            BitConverter.GetBytes(h).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);  // Planes
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28); // Bits pro Pixel
            BitConverter.GetBytes(dataSize).CopyTo(bmp, 34);

            // Pixeldaten (BMP = Bottom-Up, BGR-Reihenfolge passt direkt)
            for (int y = 0; y < h; y++)
            {
                int srcOffset = y * srcStride;
                int dstOffset = 54 + (h - 1 - y) * bmpStride;
                Buffer.BlockCopy(bgr, srcOffset, bmp, dstOffset, rowBytes);
            }

            return bmp;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_running)
                {
                    _backend.StopCamera();
                    _running = false;
                }
            }
        }
    }
}