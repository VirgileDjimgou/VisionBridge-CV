namespace REST_API_NeuroC_Prep.Interop;

/// <summary>
/// Abstraktion über die native Vision-Engine.
/// Ermöglicht den Wechsel zwischen echter Kamera (P/Invoke) und Simulation
/// ohne Änderung der Controller oder des VisionService.
/// </summary>
public interface IVisionBackend
{
    bool StartCamera();
    void StopCamera();
    bool GetFrame(out NativeInterop.DetectionResult result);
    bool GetFrameInfo(out NativeInterop.FrameInfo info);
    bool GetFrameBytes(byte[] buffer, int bufferSize);
    bool GetFrameBytesRgb(byte[] buffer, int bufferSize);
    bool LoadFaceCascade(string cascadePath);
    bool DetectFaces(out NativeInterop.MultiDetectionResult result);
    bool DetectEdges(byte[] outputBuffer, int bufferSize, out int outWidth, out int outHeight);
    bool DetectCircles(out NativeInterop.MultiDetectionResult result);
}
