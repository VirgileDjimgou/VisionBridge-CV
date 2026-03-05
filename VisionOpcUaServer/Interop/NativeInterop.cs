using System.Runtime.InteropServices;

namespace VisionOpcUaServer.Interop;

/// <summary>
/// P/Invoke-Deklarationen für NeuroCComVision.dll.
/// Identisch mit den Deklarationen in VisionClientWPF und REST_API.
/// </summary>
public static class NativeInterop
{
    private const string DllName = "NeuroCComVision.dll";

    // ===== Structs =====

    [StructLayout(LayoutKind.Sequential)]
    public struct DetectionResult
    {
        public int x;
        public int y;
        public int width;
        public int height;
        [MarshalAs(UnmanagedType.I1)]
        public bool detected;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MultiDetectionResult
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public DetectionResult[] items;
        public int count;
    }

    // ===== Kamera =====

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool StartCamera();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void StopCamera();

    // ===== Farberkennung =====

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool GetFrame(out DetectionResult result);

    // ===== Gesichtserkennung =====

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool LoadFaceCascade(string cascadePath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool DetectFaces(out MultiDetectionResult result);

    // ===== Kreiserkennung =====

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool DetectCircles(out MultiDetectionResult result);
}
