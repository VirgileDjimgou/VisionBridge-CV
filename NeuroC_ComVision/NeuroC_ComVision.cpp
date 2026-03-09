// NeuroC_ComVision.cpp : Hiermit werden die exportierten Funktionen für die DLL definiert.
//

#include "pch.h"
#include "NeuroC_ComVision.h"

#include <opencv2/opencv.hpp>
#include <opencv2/objdetect.hpp>
#include <thread>
#include <atomic>
#include <mutex>
#include <cstring>

// ========== Globaler Zustand ==========
static cv::VideoCapture cap;
static std::atomic<bool> running(false);
static cv::Mat currentFrame;
static std::mutex frameMutex;
static cv::CascadeClassifier faceCascade;

// ========== Kamera-Steuerung ==========

bool StartCamera()
{
    cap.open(0);
    if (!cap.isOpened())
        return false;

    running = true;

    std::thread([]()
    {
        while (running)
        {
            cv::Mat frame;
            cap >> frame;
            if (!frame.empty())
            {
                std::lock_guard<std::mutex> lock(frameMutex);
                currentFrame = frame.clone();
            }
        }
    }).detach();

    return true;
}

void StopCamera()
{
    running = false;
    if (cap.isOpened())
        cap.release();
}

// ========== Bestehende Farberkennung ==========

bool GetFrame(DetectionResult* result)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    cv::Mat hsv;
    cv::cvtColor(currentFrame, hsv, cv::COLOR_BGR2HSV);

    cv::Mat mask;
    cv::inRange(hsv, cv::Scalar(0, 120, 70), cv::Scalar(10, 255, 255), mask);

    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(mask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);

    if (!contours.empty())
    {
        auto rect = cv::boundingRect(contours[0]);
        result->x = rect.x;
        result->y = rect.y;
        result->width = rect.width;
        result->height = rect.height;
        result->detected = true;
    }
    else
    {
        result->detected = false;
    }
    return true;
}

// ========== NEU: Frame-Rohdaten ==========

bool GetFrameInfo(FrameInfo* info)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    info->width    = currentFrame.cols;
    info->height   = currentFrame.rows;
    info->channels = currentFrame.channels();
    info->stride   = static_cast<int>(currentFrame.step[0]);
    info->totalBytes = info->stride * info->height;
    return true;
}

// BGR-Rohdaten (nativ für OpenCV)
bool GetFrameBytes(unsigned char* buffer, int bufferSize)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    int needed = static_cast<int>(currentFrame.step[0]) * currentFrame.rows;
    if (bufferSize < needed)
        return false;

    memcpy(buffer, currentFrame.data, needed);
    return true;
}

// RGB-Rohdaten (für WPF BitmapSource Rgb24)
bool GetFrameBytesRgb(unsigned char* buffer, int bufferSize)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    cv::Mat rgb;
    cv::cvtColor(currentFrame, rgb, cv::COLOR_BGR2RGB);

    // Fortlaufende Bytes sicherstellen
    if (!rgb.isContinuous())
        rgb = rgb.clone();

    int needed = rgb.cols * rgb.rows * rgb.channels();
    if (bufferSize < needed)
        return false;

    memcpy(buffer, rgb.data, needed);
    return true;
}

// ========== NEU: Gesichtserkennung ==========

bool LoadFaceCascade(const char* cascadePath)
{
    return faceCascade.load(cascadePath);
}

bool DetectFaces(MultiDetectionResult* result)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty() || faceCascade.empty())
        return false;

    cv::Mat gray;
    cv::cvtColor(currentFrame, gray, cv::COLOR_BGR2GRAY);
    cv::equalizeHist(gray, gray);

    std::vector<cv::Rect> faces;
    faceCascade.detectMultiScale(gray, faces, 1.1, 5,
        cv::CASCADE_SCALE_IMAGE, cv::Size(30, 30));

    result->count = 0;
    for (size_t i = 0; i < faces.size() && i < 32; i++)
    {
        result->items[i].x       = faces[i].x;
        result->items[i].y       = faces[i].y;
        result->items[i].width   = faces[i].width;
        result->items[i].height  = faces[i].height;
        result->items[i].detected = true;
        result->count++;
    }
    return true;
}

// ========== NEU: Kantenerkennung (Canny) ==========

bool DetectEdges(unsigned char* outputBuffer, int bufferSize,
                 int* outWidth, int* outHeight)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    cv::Mat gray, edges;
    cv::cvtColor(currentFrame, gray, cv::COLOR_BGR2GRAY);
    cv::GaussianBlur(gray, gray, cv::Size(5, 5), 1.4);
    cv::Canny(gray, edges, 50, 150);

    *outWidth  = edges.cols;
    *outHeight = edges.rows;

    int needed = edges.cols * edges.rows; // 1 Kanal
    if (bufferSize < needed)
        return false;

    if (!edges.isContinuous())
        edges = edges.clone();

    memcpy(outputBuffer, edges.data, needed);
    return true;
}

// ========== NEU: Kreiserkennung (HoughCircles) ==========

bool DetectCircles(MultiDetectionResult* result)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    cv::Mat gray;
    cv::cvtColor(currentFrame, gray, cv::COLOR_BGR2GRAY);
    cv::GaussianBlur(gray, gray, cv::Size(9, 9), 2);

    std::vector<cv::Vec3f> circles;
    cv::HoughCircles(gray, circles, cv::HOUGH_GRADIENT,
        1,               // dp
        gray.rows / 8,   // minDist
        100,             // param1 (Canny-Schwelle)
        40,              // param2 (Akkumulator-Schwelle)
        20,              // minRadius
        200              // maxRadius
    );

    result->count = 0;
    for (size_t i = 0; i < circles.size() && i < 32; i++)
    {
        int cx = cvRound(circles[i][0]);
        int cy = cvRound(circles[i][1]);
        int r  = cvRound(circles[i][2]);

        // Bounding-Box um den Kreis
        result->items[i].x       = cx - r;
        result->items[i].y       = cy - r;
        result->items[i].width   = r * 2;
        result->items[i].height  = r * 2;
        result->items[i].detected = true;
        result->count++;
    }
    return true;
}

// ==========================================================================
// Bottle Inspection Module — Volvic 1.5L PET
// ==========================================================================
// Multi-signal approach tuned for transparent Volvic bottles:
//   Signal 1: Green cap detection (HSV) — strongest anchor point
//   Signal 2: White label detection — confirms bottle body position
//   Signal 3: Contour analysis — refines bounding box on transparent body
//   Signal 4: QR/Barcode — OpenCV QRCodeDetector
// ==========================================================================

// --- Helper: detect the green Volvic cap ---
// The green cap (H: 35-85, high S, medium V) is the strongest color signal.
// Returns true if a green blob of plausible cap size is found.
static bool FindGreenCap(const cv::Mat& frame,
    cv::Rect& outCapRect, double& outCapConfidence)
{
    cv::Mat hsv;
    cv::cvtColor(frame, hsv, cv::COLOR_BGR2HSV);

    // Green range for the Volvic cap (tolerant for lighting conditions)
    cv::Mat mask;
    cv::inRange(hsv, cv::Scalar(30, 50, 40), cv::Scalar(90, 255, 255), mask);

    // Clean up noise
    cv::Mat kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(5, 5));
    cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, kernel);
    cv::morphologyEx(mask, mask, cv::MORPH_OPEN, kernel);

    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(mask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);

    double bestScore = 0;
    int bestIdx = -1;

    // Cap should be: small-to-medium, roughly square (aspect 0.5–2.0),
    // and located in the upper half of the frame
    double minCapArea = frame.cols * frame.rows * 0.001; // at least 0.1%
    double maxCapArea = frame.cols * frame.rows * 0.05;  // at most 5%

    for (size_t i = 0; i < contours.size(); i++)
    {
        double area = cv::contourArea(contours[i]);
        if (area < minCapArea || area > maxCapArea) continue;

        cv::Rect r = cv::boundingRect(contours[i]);
        double aspect = static_cast<double>(r.width) / std::max(r.height, 1);

        // Cap is roughly circular/square (aspect 0.5 to 2.5)
        if (aspect < 0.4 || aspect > 3.0) continue;

        // Prefer caps in the upper 60% of the frame
        double yBonus = (r.y < frame.rows * 0.6) ? 1.5 : 0.8;

        double score = area * yBonus;
        if (score > bestScore)
        {
            bestScore = score;
            bestIdx = static_cast<int>(i);
        }
    }

    if (bestIdx < 0) return false;

    outCapRect = cv::boundingRect(contours[bestIdx]);
    // Confidence based on how green and how well-shaped the cap is
    double area = cv::contourArea(contours[bestIdx]);
    double rectArea = outCapRect.area();
    outCapConfidence = std::min((area / std::max(rectArea, 1.0)) * 1.3, 1.0);
    return true;
}

// --- Helper: detect the white label with green elements ---
// The Volvic label is a white rectangle with green mountain graphics.
// We look for a large white+green region below the cap.
static bool FindLabel(const cv::Mat& frame, const cv::Rect& capRect,
    cv::Rect& outLabelRect)
{
    // Search area: below the cap, extending down 60% of frame height
    int searchY = capRect.y + capRect.height;
    int searchH = std::min(static_cast<int>(frame.rows * 0.65), frame.rows - searchY);
    // Horizontally: cap center ± generous width
    int capCenterX = capRect.x + capRect.width / 2;
    int searchX = std::max(0, capCenterX - frame.cols / 4);
    int searchW = std::min(frame.cols / 2, frame.cols - searchX);

    if (searchH < 20 || searchW < 20) return false;

    cv::Rect searchRoi(searchX, searchY, searchW, searchH);
    searchRoi &= cv::Rect(0, 0, frame.cols, frame.rows);
    cv::Mat roi = frame(searchRoi);

    // Detect white/light-gray pixels (the label background)
    cv::Mat gray;
    cv::cvtColor(roi, gray, cv::COLOR_BGR2GRAY);
    cv::Mat whiteMask;
    cv::threshold(gray, whiteMask, 160, 255, cv::THRESH_BINARY);

    // Clean up
    cv::Mat kernel = cv::getStructuringElement(cv::MORPH_RECT, cv::Size(7, 7));
    cv::morphologyEx(whiteMask, whiteMask, cv::MORPH_CLOSE, kernel);

    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(whiteMask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);

    double bestArea = 0;
    int bestIdx = -1;

    for (size_t i = 0; i < contours.size(); i++)
    {
        double area = cv::contourArea(contours[i]);
        cv::Rect r = cv::boundingRect(contours[i]);
        double aspect = static_cast<double>(r.height) / std::max(r.width, 1);

        // Label: taller than wide (aspect > 0.5), decent size
        if (area > roi.cols * roi.rows * 0.05 && aspect > 0.3 && area > bestArea)
        {
            bestArea = area;
            bestIdx = static_cast<int>(i);
        }
    }

    if (bestIdx < 0) return false;

    cv::Rect labelLocal = cv::boundingRect(contours[bestIdx]);
    outLabelRect = cv::Rect(
        searchRoi.x + labelLocal.x,
        searchRoi.y + labelLocal.y,
        labelLocal.width, labelLocal.height);
    return true;
}

// --- Helper: infer bottle bounding box from cap + label + edges ---
static void InferBottleRect(const cv::Mat& frame,
    const cv::Rect& capRect, bool hasLabel, const cv::Rect& labelRect,
    cv::Rect& outBottleRect, double& outConfidence)
{
    int capCenterX = capRect.x + capRect.width / 2;

    // Estimate bottle width: ~2.5x cap width for a 1.5L PET
    int bottleWidth = static_cast<int>(capRect.width * 2.8);
    int bottleX = capCenterX - bottleWidth / 2;

    // Bottle top = cap top
    int bottleY = capRect.y;

    // Bottle height: use label bottom if available, otherwise estimate
    int bottleHeight;
    if (hasLabel)
    {
        // Bottle extends ~30% below the label bottom
        int labelBottom = labelRect.y + labelRect.height;
        bottleHeight = static_cast<int>((labelBottom - bottleY) * 1.35);
        // Also use label width to refine bottle width
        bottleWidth = std::max(bottleWidth, static_cast<int>(labelRect.width * 1.2));
        bottleX = std::min(bottleX, labelRect.x - labelRect.width / 10);
    }
    else
    {
        // No label — estimate height as ~3.5x cap-to-neck distance
        bottleHeight = static_cast<int>(capRect.height * 12);
    }

    // Clamp to frame
    bottleX = std::max(0, bottleX);
    bottleY = std::max(0, bottleY);
    if (bottleX + bottleWidth > frame.cols) bottleWidth = frame.cols - bottleX;
    if (bottleY + bottleHeight > frame.rows) bottleHeight = frame.rows - bottleY;

    outBottleRect = cv::Rect(bottleX, bottleY, bottleWidth, bottleHeight);

    // Confidence based on available signals
    outConfidence = 0.50; // cap alone
    if (hasLabel) outConfidence += 0.35;

    // Bonus: check aspect ratio is bottle-like (2.0-5.0)
    double aspect = static_cast<double>(bottleHeight) / std::max(bottleWidth, 1);
    if (aspect > 1.8 && aspect < 5.5) outConfidence += 0.15;

    outConfidence = std::min(outConfidence, 1.0);
}

// --- Main inspection function ---
bool InspectBottle(BottleInspectionResult* result)
{
    std::lock_guard<std::mutex> lock(frameMutex);
    if (currentFrame.empty())
        return false;

    memset(result, 0, sizeof(BottleInspectionResult));

    // === Signal 1: Detect green cap (primary anchor) ===
    cv::Rect capRect;
    double capConf = 0;
    bool capFound = FindGreenCap(currentFrame, capRect, capConf);

    if (!capFound)
    {
        // No green cap = no Volvic bottle detected
        result->bottleDetected = false;
        result->bottleStatus = BOTTLE_NONE;
        return true;
    }

    result->capDetected = true;
    result->capX = capRect.x;
    result->capY = capRect.y;
    result->capWidth = capRect.width;
    result->capHeight = capRect.height;

    // === Signal 2: Detect white label below cap ===
    cv::Rect labelRect;
    bool labelFound = FindLabel(currentFrame, capRect, labelRect);

    // === Signal 3: Infer bottle bounding box from cap + label ===
    cv::Rect bottleRect;
    double bottleConf = 0;
    InferBottleRect(currentFrame, capRect, labelFound, labelRect,
        bottleRect, bottleConf);

    result->bottleDetected = true;
    result->bottleX = bottleRect.x;
    result->bottleY = bottleRect.y;
    result->bottleWidth = bottleRect.width;
    result->bottleHeight = bottleRect.height;
    result->bottleConfidence = bottleConf;

    // === Signal 5: QR / Barcode detection ===
    try
    {
        cv::QRCodeDetector qrDetector;
        std::vector<cv::Point> points;
        std::string decoded = qrDetector.detectAndDecode(currentFrame, points);
        if (!decoded.empty())
        {
            result->qrDetected = true;
            result->barcodeDetected = true;
            strncpy_s(result->decodedValue, 256, decoded.c_str(), 255);
        }
    }
    catch (...) { /* QR detection is optional */ }

    // === Defect assessment ===
    result->defectCount = 0;

    // Cap confidence too low is suspicious
    if (capConf < 0.3) result->defectCount++;

    result->bottleStatus = (result->defectCount == 0) ? BOTTLE_OK : BOTTLE_DEFECT;

    return true;
}
