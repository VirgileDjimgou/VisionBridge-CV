using System.Diagnostics;
using REST_API_NeuroC_Prep.Interop;
using REST_API_NeuroC_Prep.Models;

namespace REST_API_NeuroC_Prep.Services
{
    /// <summary>
    /// Thread-sicherer Service, der alle nativen OpenCV-Funktionen
    /// kapselt. Wird als Singleton registriert (eine Kamera = eine Instanz).
    /// Das konkrete Backend (Native oder Simulation) wird per DI injiziert.
    ///
    /// Erweitert um:
    ///   - Inspektions-ID + Timestamp + Confidence auf allen Erkennungen
    ///   - Anlagen-Steuerung (ConveyorSpeed, InspectionEnabled, RejectGateOpen)
    ///   - Runtime-Metriken (Uptime, FPS, Inspektionszähler)
    /// </summary>
    public sealed class VisionService : IDisposable
    {
        private readonly object _lock = new();
        private readonly IVisionBackend _backend;
        private bool _running;
        private bool _cascadeLoaded;

        // ===== Metriken =====
        private readonly DateTime _startTime = DateTime.UtcNow;
        private long _inspectionCounter;
        private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
        private int _fpsFrameCount;
        private double _currentFps;
        private LastDetectionDto? _lastDetection;

        // ===== Bottle inspection cache (100 ms) =====
        private BottleInspectionDto? _bottleCache;
        private DateTime _bottleCacheTime = DateTime.MinValue;
        private static readonly TimeSpan BottleCacheTtl = TimeSpan.FromMilliseconds(100);

        // ===== Anlagen-Steuerung (beschreibbar via OPC-UA oder REST) =====
        private double _conveyorSpeed = 1.2;        // m/s
        private bool _inspectionEnabled = true;
        private bool _rejectGateOpen;

        public VisionService(IVisionBackend backend)
        {
            _backend = backend;
        }

        // ===== Anlagen-Steuerung =====

        public double ConveyorSpeed
        {
            get { lock (_lock) return _conveyorSpeed; }
            set { lock (_lock) _conveyorSpeed = Math.Clamp(value, 0, 5.0); }
        }

        public bool InspectionEnabled
        {
            get { lock (_lock) return _inspectionEnabled; }
            set { lock (_lock) _inspectionEnabled = value; }
        }

        public bool RejectGateOpen
        {
            get { lock (_lock) return _rejectGateOpen; }
            set { lock (_lock) _rejectGateOpen = value; }
        }

        public PlantControlDto GetPlantControl()
        {
            lock (_lock)
                return new PlantControlDto(_conveyorSpeed, _inspectionEnabled, _rejectGateOpen);
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

        // ===== Diagnose / Health Check =====

        public DiagnosticsDto GetDiagnostics()
        {
            lock (_lock)
            {
                var uptime = DateTime.UtcNow - _startTime;
                return new DiagnosticsDto(
                    $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                    _backend is SimulatedVisionBackend ? "Simulated" : "Native",
                    _running,
                    _cascadeLoaded,
                    _inspectionCounter,
                    _currentFps,
                    new PlantControlDto(_conveyorSpeed, _inspectionEnabled, _rejectGateOpen),
                    _lastDetection);
            }
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

                TrackFrame();

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

                TrackFrame();

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

                var sw = Stopwatch.StartNew();
                if (!_backend.GetFrame(out var result)) return null;
                sw.Stop();

                TrackFrame();
                long id = ++_inspectionCounter;
                var now = DateTime.UtcNow;

                // Confidence: größere Fläche → höheres Vertrauen
                double confidence = result.detected
                    ? Math.Clamp((result.width * result.height) / 10000.0, 0.1, 1.0)
                    : 0;

                if (result.detected)
                    _lastDetection = new LastDetectionDto("color", now, confidence, sw.Elapsed.TotalMilliseconds);

                return new ColorDetectionDto(
                    result.detected,
                    result.detected
                        ? new BoundingBoxDto(result.x, result.y, result.width, result.height)
                        : null,
                    id, now, confidence);
            }
        }

        // ===== Gesichtserkennung =====

        public MultiDetectionDto? DetectFaces()
        {
            lock (_lock)
            {
                if (!_running) return null;
                if (!_cascadeLoaded) return null;

                var sw = Stopwatch.StartNew();
                if (!_backend.DetectFaces(out var result)) return null;
                sw.Stop();

                TrackFrame();
                long id = ++_inspectionCounter;
                var now = DateTime.UtcNow;

                double confidence = result.count > 0
                    ? Math.Clamp(result.count * 0.4, 0.1, 1.0) : 0;

                if (result.count > 0)
                    _lastDetection = new LastDetectionDto("face", now, confidence, sw.Elapsed.TotalMilliseconds);

                return ToMultiDto("face", result, id, now, confidence);
            }
        }

        // ===== Kreiserkennung =====

        public MultiDetectionDto? DetectCircles()
        {
            lock (_lock)
            {
                if (!_running) return null;

                var sw = Stopwatch.StartNew();
                if (!_backend.DetectCircles(out var result)) return null;
                sw.Stop();

                TrackFrame();
                long id = ++_inspectionCounter;
                var now = DateTime.UtcNow;

                double confidence = result.count > 0
                    ? Math.Clamp(result.count * 0.3, 0.1, 1.0) : 0;

                if (result.count > 0)
                    _lastDetection = new LastDetectionDto("circle", now, confidence, sw.Elapsed.TotalMilliseconds);

                return ToMultiDto("circle", result, id, now, confidence);
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

                TrackFrame();

                return new EdgeDetectionDto(w, h, Convert.ToBase64String(edgeBuffer, 0, w * h));
            }
        }

        // ===== Bottle Inspection =====

        public BottleInspectionDto? InspectBottle()
        {
            lock (_lock)
            {
                if (!_running) return null;

                // Return cached result if it is still fresh
                if (_bottleCache != null &&
                    (DateTime.UtcNow - _bottleCacheTime) < BottleCacheTtl)
                    return _bottleCache;

                var sw = Stopwatch.StartNew();
                if (!_backend.InspectBottle(out var result)) return null;
                sw.Stop();

                TrackFrame();
                long id = ++_inspectionCounter;
                var now = DateTime.UtcNow;

                if (result.bottleDetected)
                    _lastDetection = new LastDetectionDto("bottle", now,
                        result.bottleConfidence, sw.Elapsed.TotalMilliseconds);

                var dto = new BottleInspectionDto(
                    result.bottleDetected,
                    result.bottleDetected
                        ? new BoundingBoxDto(result.bottleX, result.bottleY,
                            result.bottleWidth, result.bottleHeight)
                        : null,
                    result.bottleConfidence,
                    result.capDetected,
                    result.capDetected
                        ? new BoundingBoxDto(result.capX, result.capY,
                            result.capWidth, result.capHeight)
                        : null,
                    result.barcodeDetected,
                    result.qrDetected,
                    string.IsNullOrEmpty(result.decodedValue) ? null : result.decodedValue,
                    (BottleStatusEnum)result.bottleStatus,
                    result.defectCount,
                    id, now);

                _bottleCache = dto;
                _bottleCacheTime = now;
                return dto;
            }
        }

        // ===== Hilfsmethoden =====

        private void TrackFrame()
        {
            _fpsFrameCount++;
            if (_fpsWatch.ElapsedMilliseconds >= 1000)
            {
                _currentFps = _fpsFrameCount / (_fpsWatch.ElapsedMilliseconds / 1000.0);
                _fpsFrameCount = 0;
                _fpsWatch.Restart();
            }
        }

        private static MultiDetectionDto ToMultiDto(string type,
            NativeInterop.MultiDetectionResult result,
            long inspectionId, DateTime timestamp, double overallConfidence)
        {
            var items = new List<DetectionItemDto>();
            for (int i = 0; i < result.count; i++)
            {
                var item = result.items[i];
                // Per-Item Confidence: größere Box → höheres Vertrauen
                double itemConf = Math.Clamp((item.width * item.height) / 8000.0, 0.1, 1.0);
                items.Add(new DetectionItemDto(i,
                    new BoundingBoxDto(item.x, item.y, item.width, item.height),
                    itemConf));
            }
            return new MultiDetectionDto(type, result.count, items,
                inspectionId, timestamp, overallConfidence);
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