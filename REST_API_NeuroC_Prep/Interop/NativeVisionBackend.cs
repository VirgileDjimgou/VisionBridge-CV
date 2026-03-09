namespace REST_API_NeuroC_Prep.Interop;

/// <summary>
/// Produktiv-Implementierung — leitet 1:1 an die C++ DLL weiter.
/// Keine Logikänderung, nur die statischen P/Invoke-Aufrufe gewrappt.
/// </summary>
public sealed class NativeVisionBackend : IVisionBackend
{
    public bool StartCamera() => NativeInterop.StartCamera();
    public void StopCamera() => NativeInterop.StopCamera();
    public bool GetFrame(out NativeInterop.DetectionResult result) => NativeInterop.GetFrame(out result);
    public bool GetFrameInfo(out NativeInterop.FrameInfo info) => NativeInterop.GetFrameInfo(out info);
    public bool GetFrameBytes(byte[] buffer, int bufferSize) => NativeInterop.GetFrameBytes(buffer, bufferSize);
    public bool GetFrameBytesRgb(byte[] buffer, int bufferSize) => NativeInterop.GetFrameBytesRgb(buffer, bufferSize);
    public bool LoadFaceCascade(string cascadePath) => NativeInterop.LoadFaceCascade(cascadePath);
    public bool DetectFaces(out NativeInterop.MultiDetectionResult result) => NativeInterop.DetectFaces(out result);
    public bool DetectEdges(byte[] outputBuffer, int bufferSize, out int outWidth, out int outHeight) => NativeInterop.DetectEdges(outputBuffer, bufferSize, out outWidth, out outHeight);
    public bool DetectCircles(out NativeInterop.MultiDetectionResult result) => NativeInterop.DetectCircles(out result);
    public bool InspectBottle(out NativeInterop.BottleInspectionResult result) => NativeInterop.InspectBottle(out result);
}
