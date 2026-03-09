namespace REST_API_NeuroC_Prep.Models
{
    // ===== Gemeinsame Typen =====

    /// <summary>Bounding-Box eines erkannten Objekts.</summary>
    public record BoundingBoxDto(int X, int Y, int Width, int Height);

    /// <summary>Standard-Fehler/Info-Antwort.</summary>
    public record MessageDto(string Message);

    // ===== Kamera =====

    public record CameraStatusDto(bool Running, string Status);

    // ===== Frame-Informationen =====

    public record FrameInfoDto(int Width, int Height, int Stride, int Channels, int TotalBytes);

    // ===== Farberkennung =====

    public record ColorDetectionDto(
        bool Detected,
        BoundingBoxDto? BoundingBox,
        long InspectionId = 0,
        DateTime? Timestamp = null,
        double Confidence = 0);

    // ===== Mehrfach-Erkennung (Gesichter, Kreise) =====

    public record DetectionItemDto(int Index, BoundingBoxDto BoundingBox, double Confidence = 0);

    public record MultiDetectionDto(
        string Type,
        int Count,
        List<DetectionItemDto> Detections,
        long InspectionId = 0,
        DateTime? Timestamp = null,
        double Confidence = 0);

    // ===== Frame als Bild =====

    /// <summary>RGB-Frame als Base64-kodierter String.</summary>
    public record FrameBase64Dto(int Width, int Height, int Channels, string Base64Data);

    // ===== Kanten-Bild =====

    /// <summary>Canny-Kantenbild als Base64-kodierter Graustufenstring.</summary>
    public record EdgeDetectionDto(int Width, int Height, string Base64Data);

    // ===== Anlagen-Steuerung (beschreibbar via OPC-UA / REST) =====

    /// <summary>Zustand der simulierten Förderband-Anlage.</summary>
    public record PlantControlDto(
        double ConveyorSpeed,
        bool InspectionEnabled,
        bool RejectGateOpen);

    // ===== Diagnose / Health Check =====

    /// <summary>Letzte erfolgreiche Erkennung — Typ, Zeitpunkt, Confidence, Latenz.</summary>
    public record LastDetectionDto(
        string Type,
        DateTime Timestamp,
        double Confidence,
        double LatencyMs);

    /// <summary>Runtime-Diagnose — Uptime, FPS, Backend, Anlagenzustand.</summary>
    public record DiagnosticsDto(
        string Uptime,
        string BackendMode,
        bool CameraRunning,
        bool CascadeLoaded,
        long TotalInspections,
        double CurrentFps,
        PlantControlDto PlantControl,
        LastDetectionDto? LastDetection);

    // ===== Bottle Inspection =====

    /// <summary>Overall inspection verdict.</summary>
    public enum BottleStatusEnum { None = 0, Ok = 1, Defect = 2 }

    /// <summary>Complete bottle inspection result from the vision engine.</summary>
    public record BottleInspectionDto(
        bool BottleDetected,
        BoundingBoxDto? BottleBoundingBox,
        double BottleConfidence,
        bool CapDetected,
        BoundingBoxDto? CapBoundingBox,
        bool BarcodeDetected,
        bool QrDetected,
        string? DecodedValue,
        BottleStatusEnum BottleStatus,
        int DefectCount,
        long InspectionId = 0,
        DateTime? Timestamp = null);
}