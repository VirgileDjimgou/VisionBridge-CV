using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OPC_UA_ClientSimulator.Sources;

namespace OPC_UA_ClientSimulator
{
    /// <summary>
    /// VisionBridge Unified Client — vereint Vision-Lab (Video, Erkennung)
    /// und SPS/Sortierlogik (OPC-UA Write, Methoden, Entscheidungen).
    /// Drei Quellen: Lokal (P/Invoke), REST API, OPC-UA.
    /// </summary>
    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private IVisionSource? _source;
        private int _currentMode;
        private int _frameCount;
        private readonly Stopwatch _fpsWatch = new();

        // Gecachte Erkennungswerte (für Sortierlogik)
        private bool _cameraRunning;
        private bool _colorDetected;
        private int _colorX, _colorY, _colorW, _colorH;
        private double _colorConfidence;
        private int _faceCount;
        private double _faceConfidence;
        private int _circleCount;
        private double _circleConfidence;

        // Gecachte Flaschendaten (für Sortierlogik + last-valid-hold)
        private bool _bottleDetected;
        private bool _bottleCapDetected;
        private double _bottleConfidence;
        private int _bottleStatus;
        private BottleInspection? _lastValidBottle;  // letztes gültiges Ergebnis (anti-flicker)
        private int _bottleHoldFrames;               // countdown für hold
        private const int BottleHoldDuration = 8;    // Frames bevor "Nicht erkannt" angezeigt wird

        // Sortier-Statistik
        private int _totalDecisions, _sortedOut, _qualityOk, _halted;

        // Sortierlogik-Drosselung
        private int _sortingTickCounter;
        private string _lastDecision = "";

        public MainWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer();
            _timer.Tick += Timer_Tick;
        }

        // ===== Quellenauswahl =====

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            switch (SourceComboBox.SelectedIndex)
            {
                case 0: // Local
                    LblEndpoint.Visibility = Visibility.Collapsed;
                    TxtEndpoint.Visibility = Visibility.Collapsed;
                    break;
                case 1: // REST
                    LblEndpoint.Visibility = Visibility.Visible;
                    TxtEndpoint.Visibility = Visibility.Visible;
                    TxtEndpoint.Text = "https://localhost:7158";
                    break;
                case 2: // OPC-UA
                    LblEndpoint.Visibility = Visibility.Visible;
                    TxtEndpoint.Visibility = Visibility.Visible;
                    TxtEndpoint.Text = "opc.tcp://localhost:4840/visionbridge";
                    break;
            }
        }

        private IVisionSource CreateSource()
        {
            return SourceComboBox.SelectedIndex switch
            {
                1 => new RestVisionSource(TxtEndpoint.Text),
                2 => new OpcUaVisionSource(TxtEndpoint.Text),
                _ => new LocalVisionSource()
            };
        }

        // ===== Start / Stop =====

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _source?.Dispose();

            _source = CreateSource();
            TxtStatus.Text = $"Verbinde ({_source.Name})...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(241, 196, 15));

            try
            {
                if (!_source.Start())
                {
                    SetStartError($"{_source.Name}: Verbindung fehlgeschlagen",
                        $"Verbindung fehlgeschlagen.\n\nQuelle: {_source.Name}\nPrüfen Sie, ob der Dienst läuft.");
                    return;
                }
            }
            catch (FileNotFoundException ex)
            {
                SetStartError("DLL nicht gefunden", ex.Message, "DLL nicht gefunden");
                return;
            }
            catch (InvalidOperationException ex)
            {
                SetStartError("Kamera nicht verfügbar", ex.Message, "Kamera nicht verfügbar");
                return;
            }
            catch (Exception ex)
            {
                SetStartError("Startfehler", ex.Message, "Fehler");
                return;
            }

            // Timer konfigurieren
            if (_source.SupportsVideo)
            {
                NoSignalText.Visibility = Visibility.Collapsed;
                _timer.Interval = _source is LocalVisionSource
                    ? TimeSpan.FromMilliseconds(33)
                    : TimeSpan.FromMilliseconds(200);
            }
            else
            {
                NoSignalText.Text = $"{_source.Name}\nKein Video — nur Werte";
                NoSignalText.Visibility = Visibility.Visible;
                VideoImage.Source = null;
                _timer.Interval = TimeSpan.FromMilliseconds(500);
            }

            // Anlagen-Steuerung je nach Quelle
            bool hasPlant = _source is IPlantControl;
            PlantControlPanel.Visibility = hasPlant ? Visibility.Visible : Visibility.Collapsed;
            CameraMethodPanel.Visibility = hasPlant ? Visibility.Visible : Visibility.Collapsed;

            TxtStatus.Text = $"{_source.Name} aktiv";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(39, 174, 96));
            AddLog($"✅ {_source.Name} gestartet");

            _fpsWatch.Restart();
            _frameCount = 0;
            _totalDecisions = _sortedOut = _qualityOk = _halted = 0;
            _sortingTickCounter = 0;
            _lastDecision = "";
            _timer.Start();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _source?.Stop();
            _source?.Dispose();
            _source = null;

            TxtStatus.Text = "Gestoppt";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(192, 57, 43));
            NoSignalText.Text = "Kein Signal";
            NoSignalText.Visibility = Visibility.Visible;
            OverlayCanvas.Children.Clear();
            VideoImage.Source = null;
            PositionText.Text = "";
            ConfidenceText.Text = "";
            FpsText.Text = "";
            PlantControlPanel.Visibility = Visibility.Collapsed;
            CameraMethodPanel.Visibility = Visibility.Collapsed;

            ResetDiagnostics();
            SetDecision("— Gestoppt —", "#555");
            AddLog("🔌 Gestoppt");
        }

        // ===== Erkennungsmodus =====

        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _currentMode = ModeComboBox.SelectedIndex;
            OverlayCanvas.Children.Clear();
        }

        // ===== Hauptschleife =====

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_source == null) return;

            try
            {
                // Video rendern
                if (_source.SupportsVideo)
                    RenderLiveFrame();

                // Alle Erkennungen lesen (für Sortierlogik)
                var color = _source.DetectColor();
                var faces = _source.DetectFaces();
                var circles = _source.DetectCircles();
                var bottle = _source.InspectBottle();

                UpdateCachedValues(color, faces, circles, bottle);

                // Overlay nur für gewählten Modus
                OverlayCanvas.Children.Clear();
                switch (_currentMode)
                {
                    case 0: DisplayColorResult(color); break;
                    case 1: DisplayFaceResult(faces); break;
                    case 2: RunEdgeDetection(); break;
                    case 3: DisplayCircleResult(circles); break;
                    case 4: DisplayBottleInspection(bottle); break;
                }

                UpdateDetectionDisplay();

                // Sortierlogik gedrosselt (~2 Hz für schnelle Quellen)
                _sortingTickCounter++;
                int interval = _source is LocalVisionSource ? 15 : 1;
                if (_sortingTickCounter >= interval)
                {
                    _sortingTickCounter = 0;
                    EvaluateSortingLogic();
                }

                UpdateFps();
                UpdateDiagnostics();
            }
            catch (Exception ex)
            {
                AddLog($"⚠ Fehler: {ex.Message}");
            }
        }

        // ===== Gecachte Werte aktualisieren =====

        private void UpdateCachedValues(ColorResult? color, MultiResult? faces, MultiResult? circles,
            BottleInspection? bottle)
        {
            _cameraRunning = color != null || faces != null || circles != null;

            if (color != null)
            {
                _colorDetected = color.Detected;
                _colorConfidence = color.Confidence;
                _colorX = color.Box.X; _colorY = color.Box.Y;
                _colorW = color.Box.Width; _colorH = color.Box.Height;
            }
            if (faces != null)
            {
                _faceCount = faces.Count;
                _faceConfidence = faces.Confidence;
            }
            if (circles != null)
            {
                _circleCount = circles.Count;
                _circleConfidence = circles.Confidence;
            }
            if (bottle != null)
            {
                _bottleDetected = bottle.BottleDetected;
                _bottleCapDetected = bottle.CapDetected;
                _bottleConfidence = bottle.BottleConfidence;
                _bottleStatus = bottle.BottleStatus;

                if (bottle.BottleDetected)
                {
                    _lastValidBottle = bottle;
                    _bottleHoldFrames = BottleHoldDuration;
                }
                else if (_bottleHoldFrames > 0)
                {
                    _bottleHoldFrames--;
                }
                else
                {
                    _lastValidBottle = null;
                }
            }
        }

        private void UpdateDetectionDisplay()
        {
            TxtCameraRunning.Text = _cameraRunning ? "✅ Aktiv" : "❌ Inaktiv";

            TxtColorInfo.Text = _colorDetected
                ? $"Detected ✅  ({_colorX},{_colorY}) {_colorW}×{_colorH}  Conf: {_colorConfidence:P0}"
                : "Kein rotes Objekt";

            TxtFaceInfo.Text = _faceCount > 0
                ? $"Count: {_faceCount}  Conf: {_faceConfidence:P0}"
                : "Keine Gesichter";

            TxtCircleInfo.Text = _circleCount > 0
                ? $"Count: {_circleCount}  Conf: {_circleConfidence:P0}"
                : "Keine Kreise";

            // Bottle info is updated in DisplayBottleInspection()
        }

        // ===== Erkennung-Overlay =====

        private void DisplayColorResult(ColorResult? result)
        {
            if (result == null) { PositionText.Text = ""; ConfidenceText.Text = ""; return; }

            if (result.Detected)
            {
                PositionText.Text = $"Rot: ({result.Box.X},{result.Box.Y}) {result.Box.Width}×{result.Box.Height}";
                ConfidenceText.Text = $"Confidence: {result.Confidence:P0}";
                if (_source!.SupportsVideo)
                    DrawOverlayRect(result.Box, Brushes.Red, "Rot");
            }
            else
            {
                PositionText.Text = "Kein rotes Objekt";
                ConfidenceText.Text = "";
            }
        }

        private void DisplayFaceResult(MultiResult? result)
        {
            if (result == null) { PositionText.Text = "Nicht verfügbar"; ConfidenceText.Text = ""; return; }

            PositionText.Text = $"{result.Count} Gesicht(er)";
            ConfidenceText.Text = result.Count > 0 ? $"Confidence: {result.Confidence:P0}" : "";

            if (_source!.SupportsVideo)
            {
                for (int i = 0; i < result.Count && i < result.Items.Length; i++)
                    DrawOverlayRect(result.Items[i], Brushes.LimeGreen, $"Gesicht {i + 1}");
            }
        }

        private void RunEdgeDetection()
        {
            if (_source == null || !_source.SupportsEdgeDetection)
            {
                PositionText.Text = "Kantenerkennung nicht verfügbar";
                ConfidenceText.Text = "";
                return;
            }

            var result = _source.DetectEdges();
            if (result == null) return;

            var bitmap = BitmapSource.Create(result.Width, result.Height, 96, 96,
                PixelFormats.Gray8, null, result.Data, result.Width);
            bitmap.Freeze();
            VideoImage.Source = bitmap;
            PositionText.Text = $"Kanten: {result.Width}×{result.Height}";
            ConfidenceText.Text = "";
        }

        private void DisplayCircleResult(MultiResult? result)
        {
            if (result == null) { PositionText.Text = ""; ConfidenceText.Text = ""; return; }

            PositionText.Text = $"{result.Count} Kreis(e)";
            ConfidenceText.Text = result.Count > 0 ? $"Confidence: {result.Confidence:P0}" : "";

            if (_source!.SupportsVideo)
            {
                for (int i = 0; i < result.Count && i < result.Items.Length; i++)
                    DrawOverlayEllipse(result.Items[i], Brushes.Cyan, $"⌀{result.Items[i].Width}");
            }
        }

        // ===== Flascheninspektion =====

        private void DisplayBottleInspection(BottleInspection? fresh)
        {
            // Use fresh result if available, otherwise hold the last valid one
            var result = (fresh?.BottleDetected == true) ? fresh : _lastValidBottle;

            if (result == null)
            {
                PositionText.Text = "Keine Inspektionsdaten";
                ConfidenceText.Text = "";
                TxtBottleInfo.Text = "—";
                return;
            }

            if (!result.BottleDetected)
            {
                PositionText.Text = "Keine Flasche erkannt";
                ConfidenceText.Text = "";
                TxtBottleInfo.Text = "Nicht erkannt";
                return;
            }

            string statusEmoji = result.BottleStatus switch
            {
                1 => "✅", 2 => "❌", _ => "—"
            };

            PositionText.Text = $"Flasche: ({result.BottleBox.X},{result.BottleBox.Y}) " +
                $"{result.BottleBox.Width}×{result.BottleBox.Height}";
            ConfidenceText.Text = $"Conf: {result.BottleConfidence:P0}  " +
                $"Status: {statusEmoji} {result.StatusLabel}";

            TxtBottleInfo.Text =
                $"{statusEmoji} {result.StatusLabel}  Conf: {result.BottleConfidence:P0}\n" +
                $"Deckel: {(result.CapDetected ? "✅ Vorhanden" : "❌ Fehlt")}" +
                (result.DefectCount > 0 ? $"  Defekte: {result.DefectCount}" : "");

            // Overlays
            if (_source!.SupportsVideo)
            {
                // Bottle bounding box (green if OK, red if defect)
                var bottleColor = result.BottleStatus == 1 ? Brushes.LimeGreen : Brushes.OrangeRed;
                DrawOverlayRect(result.BottleBox, bottleColor,
                    result.StatusLabel);

                // Cap box (cyan)
                if (result.CapDetected)
                    DrawOverlayRect(result.CapBox, Brushes.Cyan, "Deckel");
            }
        }

        // ===== Live-Video =====

        private void RenderLiveFrame()
        {
            var frame = _source!.GetFrameRgb();
            if (frame == null) return;

            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
                PixelFormats.Rgb24, null, frame.RgbData, frame.Width * 3);
            bitmap.Freeze();
            VideoImage.Source = bitmap;
        }

        // ===== Overlay-Zeichnung =====

        private void DrawOverlayRect(DetectionBox det, Brush color, string label)
        {
            if (VideoImage.Source is not BitmapSource bmp) return;

            double scaleX = VideoImage.ActualWidth / bmp.PixelWidth;
            double scaleY = VideoImage.ActualHeight / bmp.PixelHeight;

            double offsetX = (VideoContainer.ActualWidth - VideoImage.ActualWidth) / 2;
            double offsetY = (VideoContainer.ActualHeight - VideoImage.ActualHeight) / 2;
            if (offsetX < 0) offsetX = 0;
            if (offsetY < 0) offsetY = 0;

            var rect = new Rectangle
            {
                Width = det.Width * scaleX,
                Height = det.Height * scaleY,
                Stroke = color,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, det.X * scaleX + offsetX);
            Canvas.SetTop(rect, det.Y * scaleY + offsetY);
            OverlayCanvas.Children.Add(rect);

            var text = new TextBlock
            {
                Text = label,
                Foreground = color,
                FontSize = 12,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(text, det.X * scaleX + offsetX);
            Canvas.SetTop(text, det.Y * scaleY + offsetY - 16);
            OverlayCanvas.Children.Add(text);
        }

        private void DrawOverlayEllipse(DetectionBox det, Brush color, string label)
        {
            if (VideoImage.Source is not BitmapSource bmp) return;

            double scaleX = VideoImage.ActualWidth / bmp.PixelWidth;
            double scaleY = VideoImage.ActualHeight / bmp.PixelHeight;

            var ellipse = new Ellipse
            {
                Width = det.Width * scaleX,
                Height = det.Height * scaleY,
                Stroke = color,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(ellipse, det.X * scaleX);
            Canvas.SetTop(ellipse, det.Y * scaleY);
            OverlayCanvas.Children.Add(ellipse);

            var text = new TextBlock
            {
                Text = label,
                Foreground = color,
                FontSize = 11
            };
            Canvas.SetLeft(text, det.X * scaleX);
            Canvas.SetTop(text, det.Y * scaleY - 16);
            OverlayCanvas.Children.Add(text);
        }

        // ===== Anlagen-Steuerung (IPlantControl) =====

        private void BtnCameraStart_Click(object sender, RoutedEventArgs e)
        {
            if (_source is IPlantControl ctrl)
            {
                ctrl.CameraStart();
                AddLog("📷 Camera.Start() aufgerufen");
            }
        }

        private void BtnCameraStop_Click(object sender, RoutedEventArgs e)
        {
            if (_source is IPlantControl ctrl)
            {
                ctrl.CameraStop();
                AddLog("📷 Camera.Stop() aufgerufen");
            }
        }

        private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            double speed = Math.Round(SliderSpeed.Value, 1);
            TxtSpeedValue.Text = speed.ToString("F1");
            if (_source is IPlantControl ctrl)
                ctrl.SetConveyorSpeed(speed);
        }

        private void ChkInspection_Click(object sender, RoutedEventArgs e)
        {
            if (_source is IPlantControl ctrl)
                ctrl.SetInspectionEnabled(ChkInspection.IsChecked == true);
        }

        private void ChkRejectGate_Click(object sender, RoutedEventArgs e)
        {
            if (_source is IPlantControl ctrl)
                ctrl.SetRejectGateOpen(ChkRejectGate.IsChecked == true);
        }

        // ===== Sortierlogik =====

        private void EvaluateSortingLogic()
        {
            if (!_cameraRunning)
            {
                SetDecision("⏸ Keine Daten", "#555");
                return;
            }

            // Priorität 1: Sicherheit — Gesicht erkannt → Halt
            if (_faceCount > 0)
            {
                _halted++; _totalDecisions++;
                string decision = "🛑 HALT — Gesicht erkannt";
                SetDecision(decision, "#c0392b");
                if (decision != _lastDecision)
                {
                    AddLog($"[{DateTime.Now:HH:mm:ss}] HALT: {_faceCount} Gesicht(er) Conf: {_faceConfidence:P0} → Station angehalten");
                    _lastDecision = decision;
                }
                return;
            }

            // Priorität 2: Farbfehler → Aussortieren + Weiche öffnen
            if (_colorDetected && _colorConfidence > 0.3)
            {
                _sortedOut++; _totalDecisions++;
                string decision = "⚠ AUSSORTIEREN — Rot erkannt";
                SetDecision(decision, "#e67e22");
                if (decision != _lastDecision)
                {
                    AddLog($"[{DateTime.Now:HH:mm:ss}] AUSSORTIEREN: Rot ({_colorX},{_colorY}) Conf: {_colorConfidence:P0} → Weiche öffnen");
                    _lastDecision = decision;
                }

                if (ChkRejectGate.IsChecked != true && _source is IPlantControl ctrl1)
                {
                    ChkRejectGate.IsChecked = true;
                    ctrl1.SetRejectGateOpen(true);
                }
                return;
            }

            // Weiche schließen wenn kein Defekt
            if (ChkRejectGate.IsChecked == true && _source is IPlantControl ctrl2)
            {
                ChkRejectGate.IsChecked = false;
                ctrl2.SetRejectGateOpen(false);
            }

            // Priorität 3: Qualität — genug Bohrlöcher?
            if (_circleCount >= 3)
            {
                _qualityOk++; _totalDecisions++;
                string decision = $"✅ QUALITÄT OK — {_circleCount} Kreise";
                SetDecision(decision, "#27ae60");
                if (decision != _lastDecision)
                {
                    AddLog($"[{DateTime.Now:HH:mm:ss}] QUALITÄT OK: {_circleCount} Kreise → Durchlassen");
                    _lastDecision = decision;
                }
                return;
            }

            // Priorität 4: Flascheninspektion — Deckel fehlt → Aussortieren
            if (_bottleDetected && !_bottleCapDetected && _bottleConfidence > 0.4)
            {
                _sortedOut++; _totalDecisions++;
                string decision = "❌ DEFEKT — Deckel fehlt";
                SetDecision(decision, "#8e44ad");
                if (decision != _lastDecision)
                {
                    AddLog($"[{DateTime.Now:HH:mm:ss}] FLASCHE DEFEKT: Deckel fehlt Conf: {_bottleConfidence:P0} → Weiche öffnen");
                    _lastDecision = decision;
                }
                if (ChkRejectGate.IsChecked != true && _source is IPlantControl ctrl3)
                {
                    ChkRejectGate.IsChecked = true;
                    ctrl3.SetRejectGateOpen(true);
                }
                return;
            }

            SetDecision("🔍 Prüfung läuft...", "#2c3e50");
            _lastDecision = "";
        }

        private void SetDecision(string text, string hexColor)
        {
            TxtDecision.Text = text;
            DecisionBorder.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hexColor));
            TxtDecisionCount.Text = $"OK: {_qualityOk} | Aussortiert: {_sortedOut} | Halt: {_halted} | Gesamt: {_totalDecisions}";
        }

        // ===== FPS =====

        private void UpdateFps()
        {
            _frameCount++;
            if (_fpsWatch.ElapsedMilliseconds > 1000)
            {
                double fps = _frameCount / (_fpsWatch.ElapsedMilliseconds / 1000.0);
                FpsText.Text = $"{fps:F1} FPS";
                _frameCount = 0;
                _fpsWatch.Restart();
            }
        }

        // ===== Diagnostik =====

        private void UpdateDiagnostics()
        {
            if (_source == null || !_source.SupportsDiagnostics)
            {
                ResetDiagnostics();
                return;
            }

            var diag = _source.GetDiagnostics();
            if (diag == null) return;

            TxtDiagUptime.Text = $"Uptime: {diag.Uptime}";
            TxtDiagBackend.Text = $"Backend: {diag.BackendMode}";
            TxtDiagFps.Text = $"Server FPS: {diag.CurrentFps:F1}";
            TxtDiagInspections.Text = $"Inspektionen: {diag.TotalInspections}";
        }

        private void ResetDiagnostics()
        {
            TxtDiagUptime.Text = "Uptime: —";
            TxtDiagBackend.Text = "Backend: —";
            TxtDiagFps.Text = "Server FPS: —";
            TxtDiagInspections.Text = "Inspektionen: —";
        }

        // ===== Log =====

        private void SetStartError(string logMessage, string userMessage, string title = "Fehler")
        {
            TxtStatus.Text = "Fehler";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(192, 57, 43));
            AddLog($"❌ {logMessage}");
            _source?.Dispose();
            _source = null;
            MessageBox.Show(userMessage, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void AddLog(string message)
        {
            LogList.Items.Add(message);
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(0);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        // ===== Cleanup =====

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _source?.Stop();
            _source?.Dispose();
            base.OnClosed(e);
        }
    }
}