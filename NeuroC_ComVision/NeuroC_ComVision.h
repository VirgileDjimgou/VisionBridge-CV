// Der folgende ifdef-Block ist die Standardmethode zum Erstellen von Makros, die das Exportieren
// aus einer DLL vereinfachen. Alle Dateien in dieser DLL werden mit dem NEUROCCOMVISION_EXPORTS-Symbol
// (in der Befehlszeile definiert) kompiliert. Dieses Symbol darf für kein Projekt definiert werden,
// das diese DLL verwendet. Alle anderen Projekte, deren Quelldateien diese Datei beinhalten, sehen
// NEUROCCOMVISION_API-Funktionen als aus einer DLL importiert an, während diese DLL
// mit diesem Makro definierte Symbole als exportiert ansieht.

#pragma once

#ifdef NEUROC_COMVISION_EXPORTS
#define NEUROC_API __declspec(dllexport)
#else
#define NEUROC_API __declspec(dllimport)
#endif

extern "C"
{
    // --- Erkennungsergebnis (einzeln) ---
    struct DetectionResult
    {
        int x;
        int y;
        int width;
        int height;
        bool detected;
    };

    // --- Mehrfach-Erkennung (max. 32 Objekte) ---
    struct MultiDetectionResult
    {
        DetectionResult items[32];
        int count;
    };

    // --- Frame-Metadaten ---
    struct FrameInfo
    {
        int width;
        int height;
        int stride;     // Bytes pro Zeile (width * channels, ggf. Padding)
        int channels;
        int totalBytes; // stride * height
    };

    // --- Erkennungsmodus ---
    enum DetectionMode
    {
        MODE_COLOR  = 0,
        MODE_FACE   = 1,
        MODE_EDGE   = 2,
        MODE_CIRCLE = 3
    };

    // --- Kamera-Steuerung ---
    NEUROC_API bool StartCamera();
    NEUROC_API void StopCamera();

    // --- Bestehende Farberkennung ---
    NEUROC_API bool GetFrame(DetectionResult* result);

    // --- NEU: Frame-Rohdaten ---
    NEUROC_API bool GetFrameInfo(FrameInfo* info);
    NEUROC_API bool GetFrameBytes(unsigned char* buffer, int bufferSize);
    NEUROC_API bool GetFrameBytesRgb(unsigned char* buffer, int bufferSize);

    // --- NEU: Erkennungsfunktionen ---
    NEUROC_API bool DetectFaces(MultiDetectionResult* result);
    NEUROC_API bool DetectEdges(unsigned char* outputBuffer, int bufferSize, int* outWidth, int* outHeight);
    NEUROC_API bool DetectCircles(MultiDetectionResult* result);

    // --- NEU: Cascade laden ---
    NEUROC_API bool LoadFaceCascade(const char* cascadePath);

    // --- Bottle Inspection Module ---
    // Industrial bottle inspection for beverage production lines.
    // Detects bottle presence, cap, barcode, and defects.

    enum BottleStatus
    {
        BOTTLE_NONE   = 0,  // no bottle detected
        BOTTLE_OK     = 1,
        BOTTLE_DEFECT = 2
    };

    struct BottleInspectionResult
    {
        // Bottle detection
        bool  bottleDetected;
        int   bottleX;
        int   bottleY;
        int   bottleWidth;
        int   bottleHeight;
        double bottleConfidence;

        // Cap detection
        bool  capDetected;
        int   capX;
        int   capY;
        int   capWidth;
        int   capHeight;

        // Barcode / QR detection
        bool  barcodeDetected;
        bool  qrDetected;
        char  decodedValue[256];

        // Overall status
        int   bottleStatus;     // BottleStatus enum
        int   defectCount;
    };

    NEUROC_API bool InspectBottle(BottleInspectionResult* result);
}
