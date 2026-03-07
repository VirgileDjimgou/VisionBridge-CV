using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisionClientWPF.Sources;

namespace VisionClientWPF
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private int _currentMode;
        private int _frameCount;
        private readonly Stopwatch _fpsWatch = new();
        private IVisionSource? _source;

        public MainWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Tick += Timer_Tick;
        }

        // ===== Quelle erstellen =====

        private IVisionSource CreateSource()
        {
            return SourceComboBox.SelectedIndex switch
            {
                1 => new RestVisionSource(),
                2 => new OpcUaVisionSource(),
                _ => new LocalVisionSource()
            };
        }

        // ===== Steuerung =====

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _source?.Dispose();
            _source = CreateSource();

            StatusText.Text = $"Verbinde ({_source.Name})...";

            if (!_source.Start())
            {
                MessageBox.Show($"Verbindung fehlgeschlagen.\n\n" +
                    $"Quelle: {_source.Name}\n" +
                    $"Prüfen Sie, ob der Dienst läuft.");
                StatusText.Text = "Fehler";
                return;
            }

            if (_source.SupportsVideo)
            {
                NoSignalText.Visibility = Visibility.Collapsed;
                _timer.Interval = _source is LocalVisionSource
                    ? TimeSpan.FromMilliseconds(33)   // ~30 FPS
                    : TimeSpan.FromMilliseconds(200);  // ~5 FPS (HTTP)
            }
            else
            {
                NoSignalText.Text = $"{_source.Name}\nKein Video — nur Werte";
                NoSignalText.Visibility = Visibility.Visible;
                VideoImage.Source = null;
                _timer.Interval = TimeSpan.FromMilliseconds(250);
            }

            StatusText.Text = $"{_source.Name} aktiv";
            _fpsWatch.Restart();
            _frameCount = 0;
            _timer.Start();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _source?.Stop();
            _source?.Dispose();
            _source = null;
            StatusText.Text = "Gestoppt";
            PositionText.Text = "";
            ConfidenceText.Text = "";
            FpsText.Text = "";
            NoSignalText.Text = "Kein Signal";
            NoSignalText.Visibility = Visibility.Visible;
            OverlayCanvas.Children.Clear();
            VideoImage.Source = null;
            DiagUptimeText.Text = "Uptime: —";
            DiagBackendText.Text = "Backend: —";
            DiagServerFpsText.Text = "Server FPS: —";
            DiagInspectionsText.Text = "Inspektionen: —";
        }

        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentMode = ModeComboBox.SelectedIndex;
            OverlayCanvas.Children.Clear();
        }

        // ===== Hauptschleife =====

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_source == null) return;

            if (_source.SupportsVideo)
                RenderLiveFrame();

            switch (_currentMode)
            {
                case 0: RunColorDetection(); break;
                case 1: RunFaceDetection(); break;
                case 2: RunEdgeDetection(); break;
                case 3: RunCircleDetection(); break;
            }

            UpdateFps();
            UpdateDiagnostics();
        }

        // ===== Live-Video rendern =====

        private void RenderLiveFrame()
        {
            var frame = _source!.GetFrameRgb();
            if (frame == null) return;

            int stride = frame.Width * 3;
            var bitmap = BitmapSource.Create(
                frame.Width, frame.Height,
                96, 96,
                PixelFormats.Rgb24,
                null,
                frame.RgbData,
                stride);

            bitmap.Freeze();
            VideoImage.Source = bitmap;
        }

        // ===== Farberkennung =====

        private void RunColorDetection()
        {
            OverlayCanvas.Children.Clear();

            var result = _source!.DetectColor();
            if (result == null) return;

            if (result.Detected)
            {
                PositionText.Text = $"Rot: X={result.Box.X} Y={result.Box.Y} " +
                                    $"({result.Box.Width}×{result.Box.Height})";
                ConfidenceText.Text = $"Confidence: {result.Confidence:P0}";
                if (_source.SupportsVideo)
                    DrawOverlayRect(result.Box, Brushes.Red, "Rot");
            }
            else
            {
                PositionText.Text = "Kein rotes Objekt";
                ConfidenceText.Text = "";
            }
        }

        // ===== Gesichtserkennung =====

        private void RunFaceDetection()
        {
            OverlayCanvas.Children.Clear();

            var result = _source!.DetectFaces();
            if (result == null)
            {
                PositionText.Text = "Gesichtserkennung nicht verfügbar";
                ConfidenceText.Text = "";
                return;
            }

            PositionText.Text = $"{result.Count} Gesicht(er) erkannt";
            ConfidenceText.Text = result.Count > 0 ? $"Confidence: {result.Confidence:P0}" : "";

            if (_source.SupportsVideo)
            {
                for (int i = 0; i < result.Count && i < result.Items.Length; i++)
                    DrawOverlayRect(result.Items[i], Brushes.LimeGreen, $"Gesicht {i + 1}");
            }
        }

        // ===== Kantenerkennung =====

        private void RunEdgeDetection()
        {
            OverlayCanvas.Children.Clear();

            if (!_source!.SupportsEdgeDetection)
            {
                PositionText.Text = "Kantenerkennung nicht verfügbar";
                return;
            }

            var result = _source.DetectEdges();
            if (result == null) return;

            var edgeBitmap = BitmapSource.Create(
                result.Width, result.Height, 96, 96,
                PixelFormats.Gray8,
                null,
                result.Data,
                result.Width);

            edgeBitmap.Freeze();
            VideoImage.Source = edgeBitmap;
            PositionText.Text = $"Kanten: {result.Width}×{result.Height}";
        }

        // ===== Kreiserkennung =====

        private void RunCircleDetection()
        {
            OverlayCanvas.Children.Clear();

            var result = _source!.DetectCircles();
            if (result == null) return;

            PositionText.Text = $"{result.Count} Kreis(e) erkannt";
            ConfidenceText.Text = result.Count > 0 ? $"Confidence: {result.Confidence:P0}" : "";

            if (_source.SupportsVideo)
            {
                for (int i = 0; i < result.Count && i < result.Items.Length; i++)
                    DrawOverlayEllipse(result.Items[i], Brushes.Cyan, $"⌀{result.Items[i].Width}");
            }
        }

        // ===== Overlay-Zeichnung =====

        private void DrawOverlayRect(DetectionBox det, Brush color, string label)
        {
            if (VideoImage.Source is not BitmapSource bmp)
                return;

            double scaleX = VideoImage.ActualWidth / bmp.PixelWidth;
            double scaleY = VideoImage.ActualHeight / bmp.PixelHeight;

            double offsetX = (VideoContainer.ActualWidth - 230 - VideoImage.ActualWidth) / 2;
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
            if (VideoImage.Source is not BitmapSource bmp)
                return;

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

        // ===== FPS-Anzeige =====

        private void UpdateFps()
        {
            _frameCount++;
            if (_fpsWatch.ElapsedMilliseconds > 1000)
            {
                double fps = _frameCount / (_fpsWatch.ElapsedMilliseconds / 1000.0);
                FpsText.Text = $"{fps:F1} FPS ({_source?.Name})";
                _frameCount = 0;
                _fpsWatch.Restart();
            }
        }

        // ===== Runtime-Diagnose =====

        private void UpdateDiagnostics()
        {
            if (_source == null || !_source.SupportsDiagnostics)
            {
                DiagUptimeText.Text = "Uptime: n/a";
                DiagBackendText.Text = "Backend: n/a";
                DiagServerFpsText.Text = "Server FPS: n/a";
                DiagInspectionsText.Text = "Inspektionen: n/a";
                return;
            }

            var diag = _source.GetDiagnostics();
            if (diag == null) return;

            DiagUptimeText.Text = $"Uptime: {diag.Uptime}";
            DiagBackendText.Text = $"Backend: {diag.BackendMode}";
            DiagServerFpsText.Text = $"Server FPS: {diag.CurrentFps:F1}";
            DiagInspectionsText.Text = $"Inspektionen: {diag.TotalInspections}";
        }
    }
}