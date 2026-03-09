using System.Runtime.InteropServices;

namespace REST_API_NeuroC_Prep.Interop
{
    /// <summary>
    /// P/Invoke-Deklarationen für alle exportierten Funktionen
    /// aus NeuroCComVision.dll (C++ / OpenCV).
    /// </summary>
    public static class NativeInterop
    {
        private const string DllName = "NeuroCComVision.dll";

        // ===== Structs (Spiegel der C-Strukturen) =====

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

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameInfo
        {
            public int width;
            public int height;
            public int stride;
            public int channels;
            public int totalBytes;
        }

        // ===== Kamera-Steuerung =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool StartCamera();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StopCamera();

        // ===== Farberkennung =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool GetFrame(out DetectionResult result);

        // ===== Frame-Rohdaten =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool GetFrameInfo(out FrameInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool GetFrameBytes(byte[] buffer, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool GetFrameBytesRgb(byte[] buffer, int bufferSize);

        // ===== Gesichtserkennung =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern bool LoadFaceCascade(string cascadePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool DetectFaces(out MultiDetectionResult result);

        // ===== Kantenerkennung =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool DetectEdges(byte[] outputBuffer, int bufferSize,
            out int outWidth, out int outHeight);

        // ===== Kreiserkennung =====

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool DetectCircles(out MultiDetectionResult result);

        // ===== Bottle Inspection =====

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct BottleInspectionResult
        {
            [MarshalAs(UnmanagedType.I1)] public bool bottleDetected;
            public int bottleX, bottleY, bottleWidth, bottleHeight;
            public double bottleConfidence;

            [MarshalAs(UnmanagedType.I1)] public bool capDetected;
            public int capX, capY, capWidth, capHeight;

            [MarshalAs(UnmanagedType.I1)] public bool barcodeDetected;
            [MarshalAs(UnmanagedType.I1)] public bool qrDetected;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string decodedValue;

            public int bottleStatus;
            public int defectCount;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool InspectBottle(out BottleInspectionResult result);
    }
}