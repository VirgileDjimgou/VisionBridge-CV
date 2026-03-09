# VisionBridge

C++ / OpenCV vision engine bridged to C# via P/Invoke, with REST, OPC-UA, and industrial bottle inspection on top.

I built this as a small technical playground for my portfolio. I originally started it while preparing
for an interview at NeuroCheck (industrial image processing, Stuttgart) and it just kind of kept going
because the problem space is genuinely fun to work with.

The idea is pretty simple: a C++ DLL grabs frames from the laptop camera, runs OpenCV detection on them,
and exports everything through a flat C interface. Then on the C# side I have multiple consumers that talk
to this DLL through different channels. It simulates, on a small scale, the architecture you'd find
in real industrial vision systems: fast native core, managed layer on top, multiple protocols.


## What it does

The C++ DLL does the heavy lifting: camera capture on a background thread, color filtering (HSV for red objects),
face detection (Haar cascade), edge detection (Canny), circle detection (Hough transform), and a
**multi-signal Volvic bottle inspection** pipeline. Everything mutex-protected, results exposed via
`extern "C"` functions.

On the C# side there's a single ASP.NET Core process that I call the **VisionBridge Runtime**. It owns
the camera through P/Invoke and exposes the data through two protocols at once:

1. A **REST API** with Swagger for web clients and manual testing
2. A **bidirectional OPC-UA Server** for industrial consumers (think PLCs, SCADA, robot controllers)

Both protocols read from the same `VisionService` singleton in memory, so there's no serialization
overhead between them. One process, one camera, two interfaces.

Every detection now carries an **Inspection ID**, a **UTC timestamp**, and a **confidence score** (0–1),
providing the traceability that industrial QC systems require.

The Runtime also exposes **plant control** nodes (conveyor speed, inspection toggle, reject gate)
that OPC-UA clients can **write** to, plus OPC-UA **Methods** to start/stop the camera remotely.
A **diagnostics** endpoint (REST + OPC-UA) reports uptime, FPS, backend mode, and inspection counters.

There's a **unified WPF client** that merges the vision lab and the PLC/sorting simulator into a
single two-column interface. The left panel shows live video with bounding-box overlays (when the source
supports it), while the right panel displays detection values, plant controls, and sorting decisions.
You pick one of three data sources at runtime — direct P/Invoke to the DLL (fastest, ~30 fps), HTTP
to the REST API (~5 fps with Base64 frames), or OPC-UA (no video, just scalar values). All three are
abstracted behind an `IVisionSource` interface. An optional `IPlantControl` interface lets the OPC-UA
and REST sources write back to the server (conveyor speed, inspection toggle, reject gate, camera
start/stop), turning the demo into a full bidirectional industrial loop from a single window.


## Detection modes

The system supports five detection modes, each accessible from the WPF client mode selector,
the REST API, and OPC-UA nodes:

### 1. Color detection (Red objects)

HSV filtering in the C++ engine. Detects red objects and returns a bounding box, used as a
simple defect signal in the sorting logic. If a red object is detected with confidence > 30%,
the sorting logic automatically triggers the reject gate.

### 2. Face detection (Haar cascade)

Classical Haar cascade (`haarcascade_frontalface_default.xml`) loaded at startup. Detects up to
32 faces per frame. In the sorting logic, any detected face triggers an immediate **safety halt** —
simulating a real industrial scenario where a person in the machine zone requires an emergency stop.

### 3. Edge detection (Canny)

Gaussian blur + Canny edge detection. Returns a single-channel grayscale image that can be
displayed directly in the WPF client. Useful for surface inspection and contour analysis.

### 4. Circle detection (Hough transform)

`HoughCircles` on a blurred grayscale frame. Results are packed as bounding boxes around each
detected circle. The sorting logic uses circle count as a quality metric (≥ 3 circles = quality OK).

### 5. Volvic bottle inspection 🍾

A multi-signal inspection pipeline specifically tuned for **Volvic 1.5L PET bottles**. This is not
a generic bottle detector — it leverages the specific visual characteristics of Volvic bottles
(green cap, white label with mountain graphics) for robust identification.

**Detection signals:**

| Signal | Method | Purpose |
|--------|--------|---------|
| Green cap | Dual HSV range: cool/daylight (H 30–90) **+** warm/incandescent (H 22–50), morphological cleanup, contour scoring | Primary anchor — dual range tolerates indoor lighting shifts toward yellow-green |
| White label | Adaptive thresholding in the region below the detected cap | Confirms bottle body position and refines the bounding box |
| Bottle body | Geometric inference from cap + label positions | Estimates the full bottle bounding box even on a transparent body |
| QR / Barcode | OpenCV `QRCodeDetector` | Optional — decodes any QR code visible on the bottle |

**Inspection verdict:**

The system determines if the bottle is **OK** or **DEFECT** based on cap detection confidence.
If the cap confidence is too low (< 0.3), a defect is counted. The overall status is
`BOTTLE_OK` (no defects) or `BOTTLE_DEFECT` (one or more defects).

**Why Volvic specifically?** The green cap provides an extremely strong color anchor in HSV space
that is rarely confused with background elements. Combined with the white label below it, the system
can reliably detect and localize the bottle even when the body is transparent and hard to segment
with traditional edge-based methods.


## Architecture

```mermaid
graph LR
    CAM["Camera\n(or Simulation)"]

    subgraph RUNTIME ["VisionBridge Runtime — single process"]
        direction TB
        BACKEND["IVisionBackend\nNative (C++ DLL) or Simulated"]
        SVC["VisionService\nSingleton\n+ Metrics + PlantControl"]
        REST["REST API\n/api/camera\n/api/detection\n/api/frame\n/api/bottleinspection\n/api/diagnostics\n/api/plant"]
        OPC["OPC-UA Server\nopc.tcp://:4840/visionbridge\nMethods + Writable Nodes"]
        SWAGGER["Swagger UI"]

        BACKEND --> SVC
        SVC --> REST
        SVC --> OPC
        REST -.- SWAGGER
    end

    subgraph CLIENTS ["Consumers"]
        direction TB
        UNIFIED["Unified WPF Client\nVision Lab + SPS-Sortierlogik\n3 modes: Local / REST / OPC-UA\nIPlantControl + IVisionSource"]
        UAEXP["UaExpert\nor any OPC-UA client"]
        WEB["Web / Cloud"]
    end

    CAM --> BACKEND
    REST -- "HTTP/JSON" --> UNIFIED
    REST -- "HTTP/JSON" --> WEB
    OPC -- "OPC-UA\nread + write + call" --> UNIFIED
    OPC -- "OPC-UA" --> UAEXP
    BACKEND -. "P/Invoke direct\nstandalone mode" .-> UNIFIED

    style RUNTIME fill:#1a1a2e,stroke:#e74c3c,color:#fff
    style CLIENTS fill:#2d2d3d,stroke:#6c5ce7,color:#fff
```

One thing worth noting: the DLL uses global state (`static cv::VideoCapture cap`), so only one
process can hold the camera. That's why everything goes through the Runtime. If you try to run the
WPF client in local mode while the Runtime is also running, one of them won't get the camera.


## The projects

### NeuroC_ComVision (C++ DLL)

Camera capture on a dedicated thread with `std::mutex` for frame access. OpenCV headers are
included in `pch.h` (precompiled header) wrapped with `#pragma warning` to suppress Code Analysis
warnings from OpenCV's own internal headers. The exported functions:

| Function | Description |
|----------|-------------|
| `StartCamera` / `StopCamera` | Opens/releases the webcam, starts/stops the capture thread |
| `GetFrame` | HSV filtering for red objects — covers **both** red HSV ranges (0–10 and 170–180), picks the largest contour |
| `DetectFaces` | Haar cascade (`haarcascade_frontalface_default.xml`), up to 32 faces |
| `DetectEdges` | Gaussian blur + Canny, outputs single-channel grayscale |
| `DetectCircles` | Hough transform, results packed as bounding boxes |
| `GetFrameInfo` / `GetFrameBytesRgb` | Raw frame data with stride info, BGR or RGB |
| `InspectBottle` | Multi-signal Volvic inspection — clones frame under lock then releases mutex before the pipeline |


### VisionBridge Runtime (REST_API_NeuroC_Prep)

This is the central process. ASP.NET Core 8, owns the camera via P/Invoke.

`VisionService` is the singleton that owns the camera and all detection state. Noteworthy implementation details:
- `InspectBottle` results are **cached for 100 ms** — the OPC-UA poll (250 ms) and the WPF timer (33–200 ms) would otherwise call the pipeline redundantly on every tick
- `ConveyorSpeed` is clamped to [0, 5 m/s] regardless of the write source (REST or OPC-UA)

The REST API has six controller groups:

| Controller | Endpoints |
|------------|-----------|
| `CameraController` | `POST start`, `stop`, `GET status`, `POST cascade` |
| `DetectionController` | `GET color`, `faces`, `circles`, `edges` (all with InspectionId, Timestamp, Confidence) |
| `FrameController` | `GET info`, `rgb` (Base64), `image` (BMP download) |
| `BottleInspectionController` | `GET` full bottle inspection (detection, cap, barcode/QR, verdict) |
| `PlantController` | `GET` state, `POST conveyor-speed`, `inspection`, `reject-gate` |
| `DiagnosticsController` | `GET` full diagnostics, `GET health` |

The OPC-UA server runs as an `IHostedService` in the same process. It polls the `VisionService`
every 250ms and exposes the results as OPC-UA nodes:

```
opc.tcp://localhost:4840/visionbridge

Objects/Vision
├── Camera/
│   ├── Running              (Boolean, read)
│   ├── Start()              (Method — starts the camera)
│   └── Stop()               (Method — stops the camera)
├── Color/
│   ├── Detected             (Boolean)
│   ├── X / Y / Width / Height (Int32)
│   ├── Confidence           (Double)
│   └── Timestamp            (DateTime)
├── Faces/
│   ├── Count                (Int32)
│   └── Confidence           (Double)
├── Circles/
│   ├── Count                (Int32)
│   └── Confidence           (Double)
├── Bottle/
│   ├── Detected             (Boolean)
│   ├── Confidence           (Double)
│   ├── CapDetected          (Boolean)
│   ├── Status               (Int32 — 0=None, 1=OK, 2=Defect)
│   └── DefectCount          (Int32)
├── Control/                          ← WRITABLE by OPC-UA clients
│   ├── ConveyorSpeed        (Double, r/w — 0–5 m/s)
│   ├── InspectionEnabled    (Boolean, r/w)
│   └── RejectGateOpen       (Boolean, r/w)
└── Diagnostics/
    ├── Uptime               (String)
    ├── BackendMode           (String — "Native" or "Simulated")
    ├── TotalInspections     (Int64)
    └── CurrentFps           (Double)
```


### Unified WPF Client (OPC-UA_ClientSimulator)

This project merges the former **VisionClientWPF** (vision lab / video rendering) and the
**OPC-UA Client Simulator** (PLC sorting logic) into a single two-column WPF application.

**Layout:**

| Area | Content |
|------|--------|
| **Left panel** — Vision Lab | Live video + overlay canvas (bounding boxes, ellipses), detection mode selector (Color / Face / Edge / Circle / Bottle Inspection), FPS counter |
| **Right panel** — SPS / Steuerung | Detection values (camera status, color, faces, circles, bottle status with confidence), plant control (conveyor speed, inspection, reject gate), current sorting decision + statistics |
| **Bottom bar** | Runtime diagnostics (uptime, backend, server FPS, inspections) + sorting log |

**Data sources** — pick one from the dropdown and hit Start:

| Source | Video | Detection | Plant Control | Diagnostics | Latency |
|--------|:-----:|:---------:|:-------------:|:-----------:|:-------:|
| Lokal (P/Invoke) | 30 FPS | All 5 modes | — | — | ~1ms |
| REST API (HTTP) | ~5 FPS | All 5 modes | ✅ via REST | ✅ | ~10ms |
| OPC-UA | — | Scalar values | ✅ via Write + Methods | ✅ | ~250ms |

All three sources implement `IVisionSource`. Sources that support bidirectional control also
implement `IPlantControl` — the plant control panel and camera start/stop buttons appear
automatically when the active source supports it.

**`IPlantControl` capabilities (OPC-UA + REST):**

| Feature | OPC-UA | REST |
|---------|--------|------|
| Start/Stop camera | Method call (`Camera.Start()` / `Camera.Stop()`) | `POST /api/camera/start\|stop` |
| Set conveyor speed | Write to `Control/ConveyorSpeed` | `POST /api/plant/conveyor-speed` |
| Toggle inspection | Write to `Control/InspectionEnabled` | `POST /api/plant/inspection` |
| Reject gate control | Write to `Control/RejectGateOpen` | `POST /api/plant/reject-gate` |

**Sorting logic** — runs on every source (not just OPC-UA), throttled to ~2 Hz for fast sources:

| Priority | Condition | Action |
|:---:|---|---|
| 1 | `Faces.Count > 0` | 🛑 **HALT** — safety stop |
| 2 | `Color.Detected` + `Confidence > 30%` | ⚠ **REJECT** — sort out + auto-open reject gate |
| 3 | `Circles.Count ≥ 3` | ✅ **QUALITY OK** — pass through |
| 4 | `BottleDetected` + `!CapDetected` + `Confidence > 40%` | ❌ **DEFECT** — cap missing + auto-open reject gate |

When a defect is detected (Priority 2 or 4), the reject gate opens automatically via `IPlantControl`
and closes again when the defect clears.

The bottle inspection display uses a **last-valid-result hold** (8 frames): if the bottle is momentarily
lost between frames, the overlay stays visible instead of flickering to "not detected".

**Bottle inspection overlay:**

When the bottle inspection mode is selected, the WPF client renders:
- A **bounding box** around the detected bottle (green if OK, red if defect)
- A **cyan bounding box** around the detected cap
- Status text showing confidence and cap presence


### OPC-UA_Server (deprecated)

Was an early standalone console app for the OPC-UA server. Everything got folded into the Runtime
as a hosted service, so this project is basically dead code at this point.


### VisionBridge.Tests (xUnit)

25 unit tests covering `VisionService` using `SimulatedVisionBackend` — no camera or DLL required.

| Test group | What is covered |
|---|---|
| Camera lifecycle | `Start`, `Stop`, double `Start` |
| Plant control | `ConveyorSpeed` clamping, `GetPlantControl` round-trip |
| Color / Face / Circle detection | Null when stopped, valid result when started, InspectionId + Timestamp |
| Bottle inspection | Status enum range, CapBoundingBox present when cap detected, DEFECT when cap missing |
| Result cache | Two rapid calls return the same `InspectionId` (within 100 ms window) |
| Diagnostics | `BackendMode = "Simulated"`, uptime format, inspection counter increment |

Run with:
```
dotnet test VisionBridge.Tests
```


## Tech stack

| Layer | What |
|-------|------|
| Vision engine | C++17, OpenCV 4.x, Windows DLL (`__declspec(dllexport)`), `std::thread` / `std::mutex` |
| Runtime | ASP.NET Core 8, OPC Foundation .NET Standard SDK, Swagger |
| Unified WPF client | .NET 8, WPF, P/Invoke, OPC-UA Client SDK, `DispatcherTimer`, `IVisionSource` / `IPlantControl` |
| Tests | .NET 8, xUnit, `SimulatedVisionBackend` (no hardware required) |
| Interop | `extern "C"`, `DllImport` with `CallingConvention.Cdecl`, manual struct marshalling |
| Protocols | HTTP/JSON, OPC-UA binary over TCP (read + write + method calls) |


## Project structure

```
NeuroC_ComVision/
├── NeuroC_ComVision/              # C++ DLL
│   ├── pch.h                      # Precompiled header — OpenCV includes with Code Analysis suppressed
│   ├── NeuroC_ComVision.h         # C export interface
│   └── NeuroC_ComVision.cpp       # Capture thread + detection algorithms + bottle inspection
│
├── REST_API_NeuroC_Prep/          # VisionBridge Runtime
│   ├── Program.cs                 # Registers VisionService + OpcUaHostedService
│   ├── Interop/
│   │   ├── IVisionBackend.cs      # Abstraction over native/simulated engine
│   │   ├── NativeVisionBackend.cs # Wraps P/Invoke calls to the C++ DLL
│   │   ├── SimulatedVisionBackend.cs # Synthetic data (no DLL/camera needed)
│   │   └── NativeInterop.cs      # P/Invoke declarations + BottleInspectionResult struct
│   ├── Services/VisionService.cs  # Singleton — vision + metrics + plant control + InspectBottle cache
│   ├── Controllers/
│   │   ├── CameraController.cs    # Start, Stop, Status, Cascade
│   │   ├── DetectionController.cs # Color, Faces, Circles, Edges
│   │   ├── FrameController.cs     # Frame info, RGB, BMP image
│   │   ├── BottleInspectionController.cs # Volvic bottle inspection
│   │   ├── PlantController.cs     # Conveyor speed, Inspection, Reject gate
│   │   └── DiagnosticsController.cs # Health check + runtime metrics
│   ├── Models/VisionDtos.cs       # DTOs (with Timestamp, Confidence, PlantControl, Diagnostics, BottleInspection)
│   └── OpcUa/
│       ├── VisionNodeManager.cs   # OPC-UA node tree + Methods + Write callbacks + polling
│       ├── VisionOpcUaServer.cs
│       └── OpcUaHostedService.cs
│
├── OPC-UA_ClientSimulator/        # Unified WPF Client (Vision Lab + SPS-Sortierlogik)
│   ├── MainWindow.xaml            # Two-column layout: video + controls
│   ├── MainWindow.xaml.cs         # Rendering + detection + sorting (4 priorities incl. bottle)
│   ├── VisionInterop.cs           # P/Invoke for local mode
│   └── Sources/
│       ├── IVisionSource.cs       # IVisionSource + IPlantControl interfaces + result types
│       ├── LocalVisionSource.cs   # P/Invoke (video + detection, no plant control)
│       ├── RestVisionSource.cs    # HTTP + IPlantControl via REST
│       └── OpcUaVisionSource.cs   # OPC-UA + IPlantControl via Write/Methods
│
├── VisionBridge.Tests/            # xUnit test project (no camera/DLL needed)
│   └── VisionServiceTests.cs      # 25 tests covering VisionService with SimulatedVisionBackend
│
└── OPC-UA_Server/                 # Deprecated
```


## Running it

**Simulation mode (no camera or DLL needed):**
Set `"VisionBridge:Simulation": true` in `appsettings.json` (or pass `--VisionBridge:Simulation=true`
on the command line), then start `REST_API_NeuroC_Prep`. The API generates synthetic detection data —
a red object moving on a simulated conveyor belt, cycling face/circle counts, and simulated bottle
inspections alternating between cap-present (OK) and cap-missing (DEFECT) scenarios. All endpoints work
identically to the real camera mode. This is the easiest way to explore the project without any hardware.

**Run the unit tests (no hardware needed):**
```
dotnet test VisionBridge.Tests
```
25 tests, ~400 ms. Covers `VisionService` lifecycle, plant control, all detection types, bottle
inspection cache, and diagnostics — all against `SimulatedVisionBackend`.

**Just the local camera (no server needed):**
Start `OPC-UA_ClientSimulator`, pick "Lokal (P/Invoke)" and click Start.
You need the DLL in the output directory. Video + detection + sorting logic work standalone.
Switch to "🍾 Flascheninspektion" mode to run live bottle inspection with bounding box overlays.

**The full industrial loop:**
1. Start `REST_API_NeuroC_Prep` (launches REST API + OPC-UA server in one process)
2. Start `OPC-UA_ClientSimulator`
3. Pick "OPC-UA" (or "REST API") from the source dropdown and click **▶ Start**
4. The plant control panel appears automatically — adjust conveyor speed, toggle inspection
5. Use the **▶ Kamera / ■ Kamera** buttons to start/stop the camera remotely
6. Watch sorting decisions + confidence scores in real time on the right panel
7. Observe the reject gate auto-opening when a red defect is detected
8. Switch to "🍾 Flascheninspektion" mode to see bottle detection + cap status
9. Switch to "REST API" to see live video *and* plant control at the same time
10. Or connect UaExpert to `opc.tcp://localhost:4840/visionbridge` to browse all nodes
11. Try `GET /api/bottleinspection` or `GET /api/diagnostics` in Swagger

**REST endpoints:**

| Endpoint | Description |
|----------|-------------|
| `GET /api/bottleinspection` | Full bottle inspection (detection, cap, barcode/QR, verdict) |
| `GET /api/detection/color` | Red object detection with bounding box |
| `GET /api/detection/faces` | Face detection (count + bounding boxes) |
| `GET /api/detection/circles` | Circle detection (Hough) |
| `GET /api/detection/edges` | Canny edge detection (grayscale image) |
| `GET /api/frame/rgb` | Current frame as Base64-encoded RGB |
| `GET /api/frame/image` | Current frame as BMP download |
| `GET /api/diagnostics` | Uptime, backend mode, FPS, inspection count, plant state |
| `GET /api/diagnostics/health` | Simple health check |
| `GET /api/plant` | Current conveyor speed, inspection toggle, reject gate |
| `POST /api/plant/conveyor-speed?speed=2.5` | Set conveyor speed |
| `POST /api/plant/inspection?enabled=false` | Disable inspection |
| `POST /api/plant/reject-gate?open=true` | Open reject gate |


## What I got out of this

This project gave me a chance to actually deal with stuff that's hard to learn from docs alone.
Debugging struct alignment mismatches between C++ and C# at runtime is a very different experience
than reading about `StructLayout` on MSDN. Same with threading across a DLL boundary, or figuring
out how to render 30fps video in WPF without starving the dispatcher.

Some of the things I worked through:

* Struct layout and memory alignment across native/managed boundaries
* Mutex-protected global state in a DLL consumed by multiple managed threads
* **Releasing a mutex as early as possible** — cloning the camera frame under lock then running the entire detection pipeline on the copy, so the capture thread is never blocked by expensive processing
* WPF rendering pipeline and how `DispatcherTimer` + `BitmapSource.Freeze()` interact
* Embedding OPC-UA inside an ASP.NET Core process as an `IHostedService`
* Why industrial systems typically run OPC-UA and REST side by side (not one or the other)
* Abstracting over three completely different data sources behind one interface
* Separating read-only data (`IVisionSource`) from actuator commands (`IPlantControl`) with
  optional interface implementation — the UI adapts automatically to the source's capabilities
* Dependency injection of hardware backends (`IVisionBackend`) for testability without physical devices
* Merging two WPF apps into one coherent HMI that works in all three modes, with the plant
  control panel appearing only when the active source supports it
* Simulating a PLC sorting loop that consumes detection data and makes real-time decisions,
  regardless of whether the data comes from P/Invoke, REST, or OPC-UA
* **Bidirectional OPC-UA**: writable nodes (`OnSimpleWriteValue` callbacks), OPC-UA Methods, and
  how a PLC client can both read detection data and write control commands back to the server
* **Industrial traceability**: adding inspection IDs, timestamps, and confidence scores to every detection
* **Runtime observability**: health checks, FPS tracking, uptime, inspection counters — the kind of
  diagnostics you'd expect in any production vision system
* **Multi-signal vision pipeline**: combining dual-range HSV filtering, contour analysis, geometric inference,
  and QR detection into a single inspection function tuned for a specific product (Volvic bottle)
* **Result caching at the service layer**: throttling expensive native calls with a short TTL to avoid
  redundant DLL invocations from concurrent consumers (OPC-UA poll + WPF timer)
* **Last-valid-result hold in HMI**: keeping the previous detection overlay visible for N frames to
  avoid flickering on momentary occlusions — a standard pattern in industrial HMI
* **Unit testing a native interop layer**: using `IVisionBackend` + `SimulatedVisionBackend` to drive
  25 xUnit tests with no camera, no DLL, and no hardware in the loop

Nothing groundbreaking. Just the kind of practice that sticks.


## Disclaimer

This is a portfolio project, not production code. I kept things straightforward on purpose.
Don't expect industrial-grade anything here.


## Author

Patrick Djimgou
