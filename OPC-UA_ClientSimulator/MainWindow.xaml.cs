using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace OPC_UA_ClientSimulator
{
    /// <summary>
    /// OPC-UA Client Simulator — simuliert eine SPS/Sortierlogik.
    /// Bidirektional: liest Erkennungsdaten + Diagnostik, schreibt Steuerungswerte,
    /// ruft OPC-UA Methoden (Camera.Start / Camera.Stop) auf.
    /// </summary>
    public partial class MainWindow : Window
    {
        private Session? _session;
        private ushort _nsIndex = 2;
        private DispatcherTimer? _pollTimer;
        private bool _suppressSlider;

        // Gecachte OPC-UA Werte
        private bool _cameraRunning;
        private bool _colorDetected;
        private int _colorX, _colorY, _colorW, _colorH;
        private double _colorConfidence;
        private int _faceCount;
        private double _faceConfidence;
        private int _circleCount;
        private double _circleConfidence;

        // Diagnostik
        private string _diagUptime = "";
        private string _diagBackend = "";
        private double _diagFps;
        private long _diagInspections;

        // Statistik
        private int _totalDecisions;
        private int _sortedOut;
        private int _qualityOk;
        private int _halted;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnConnect.IsEnabled = false;
                TxtStatus.Text = "Verbinde...";

                var config = new ApplicationConfiguration
                {
                    ApplicationName = "VisionBridge PLC Simulator",
                    ApplicationUri = Utils.Format("urn:{0}:visionbridge:plcsim",
                        System.Net.Dns.GetHostName()),
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "own"),
                            SubjectName = "CN=VisionBridge PLC Simulator"
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "issuer")
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "trusted")
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "rejected")
                        },
                        AutoAcceptUntrustedCertificates = true
                    },
                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
                };

                await config.Validate(ApplicationType.Client);
                config.CertificateValidator.CertificateValidation += (_, ev) => { ev.Accept = true; };

                var application = new ApplicationInstance(config);
                await application.CheckApplicationInstanceCertificate(false, 2048);

                var endpoint = CoreClientUtils.SelectEndpoint(TxtEndpoint.Text, false);
                var endpointConfig = EndpointConfiguration.Create(config);
                var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);

                _session = await Session.Create(
                    config, configuredEndpoint, false,
                    "PLCSimulator", 60000,
                    new UserIdentity(new AnonymousIdentityToken()), null);

                int idx = _session.NamespaceUris.GetIndex("urn:visionbridge:opcua");
                if (idx >= 0) _nsIndex = (ushort)idx;

                BtnDisconnect.IsEnabled = true;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                TxtStatus.Text = "Verbunden";
                AddLog("✅ Verbunden mit " + TxtEndpoint.Text);

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _pollTimer.Tick += PollTimer_Tick;
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                BtnConnect.IsEnabled = true;
                TxtStatus.Text = "Fehler";
                AddLog("❌ Verbindungsfehler: " + ex.Message);
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

        private void Disconnect()
        {
            _pollTimer?.Stop();
            _pollTimer = null;

            try
            {
                _session?.Close();
                _session?.Dispose();
            }
            catch { }
            _session = null;

            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(192, 57, 43));
            TxtStatus.Text = "Getrennt";
            AddLog("🔌 Verbindung getrennt");
        }

        // ===== OPC-UA Method Calls (Camera Start/Stop) =====

        private void BtnCameraStart_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || !_session.Connected) return;
            try
            {
                _session.Call(
                    new NodeId("Camera", _nsIndex),
                    new NodeId("Camera.Start", _nsIndex),
                    Array.Empty<object>());
                AddLog("📷 OPC-UA Method: Camera.Start() aufgerufen");
            }
            catch (Exception ex)
            {
                AddLog("❌ Camera.Start fehlgeschlagen: " + ex.Message);
            }
        }

        private void BtnCameraStop_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || !_session.Connected) return;
            try
            {
                _session.Call(
                    new NodeId("Camera", _nsIndex),
                    new NodeId("Camera.Stop", _nsIndex),
                    Array.Empty<object>());
                AddLog("📷 OPC-UA Method: Camera.Stop() aufgerufen");
            }
            catch (Exception ex)
            {
                AddLog("❌ Camera.Stop fehlgeschlagen: " + ex.Message);
            }
        }

        // ===== OPC-UA Write (Plant Control) =====

        private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider || !IsLoaded) return;
            double speed = Math.Round(SliderSpeed.Value, 1);
            TxtSpeedValue.Text = speed.ToString("F1");
            WriteOpcUaValue("Control.ConveyorSpeed", speed);
        }

        private void ChkInspection_Click(object sender, RoutedEventArgs e)
        {
            WriteOpcUaValue("Control.InspectionEnabled", ChkInspection.IsChecked == true);
        }

        private void ChkRejectGate_Click(object sender, RoutedEventArgs e)
        {
            WriteOpcUaValue("Control.RejectGateOpen", ChkRejectGate.IsChecked == true);
        }

        private void WriteOpcUaValue(string nodeIdentifier, object value)
        {
            if (_session == null || !_session.Connected) return;
            try
            {
                var writeValue = new WriteValue
                {
                    NodeId = new NodeId(nodeIdentifier, _nsIndex),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                };

                _session.Write(null, new WriteValueCollection { writeValue }, out var results, out _);

                if (results.Count > 0 && StatusCode.IsBad(results[0]))
                    AddLog($"⚠ Write {nodeIdentifier} fehlgeschlagen: {results[0]}");
            }
            catch (Exception ex)
            {
                AddLog($"⚠ Write {nodeIdentifier}: {ex.Message}");
            }
        }

        // ===== Polling =====

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_session == null || !_session.Connected)
            {
                Disconnect();
                return;
            }

            try
            {
                ReadAllNodes();
                UpdateDisplay();
                EvaluateSortingLogic();
            }
            catch (Exception ex)
            {
                AddLog("⚠ Lesefehler: " + ex.Message);
            }
        }

        private void ReadAllNodes()
        {
            if (_session == null) return;

            var nodesToRead = new ReadValueIdCollection
            {
                // Detection (0-7)
                new() { NodeId = new NodeId("Camera.Running", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Detected", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.X", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Y", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Width", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Height", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Faces.Count", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Circles.Count", _nsIndex), AttributeId = Attributes.Value },
                // Confidence (8-10)
                new() { NodeId = new NodeId("Color.Confidence", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Faces.Confidence", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Circles.Confidence", _nsIndex), AttributeId = Attributes.Value },
                // Diagnostics (11-14)
                new() { NodeId = new NodeId("Diagnostics.Uptime", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.BackendMode", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.CurrentFps", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.TotalInspections", _nsIndex), AttributeId = Attributes.Value },
            };

            _session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out var results, out _);

            if (results.Count >= 15)
            {
                _cameraRunning = GetBool(results, 0);
                _colorDetected = GetBool(results, 1);
                _colorX = GetInt(results, 2);
                _colorY = GetInt(results, 3);
                _colorW = GetInt(results, 4);
                _colorH = GetInt(results, 5);
                _faceCount = GetInt(results, 6);
                _circleCount = GetInt(results, 7);
                _colorConfidence = GetDouble(results, 8);
                _faceConfidence = GetDouble(results, 9);
                _circleConfidence = GetDouble(results, 10);
                _diagUptime = GetString(results, 11);
                _diagBackend = GetString(results, 12);
                _diagFps = GetDouble(results, 13);
                _diagInspections = GetLong(results, 14);
            }
        }

        private void UpdateDisplay()
        {
            TxtCameraRunning.Text = $"Running: {(_cameraRunning ? "✅ Ja" : "❌ Nein")}";
            TxtColorDetected.Text = $"Detected: {(_colorDetected ? "✅ Ja" : "—")}";
            TxtColorBox.Text = _colorDetected
                ? $"Box: ({_colorX}, {_colorY}) {_colorW}×{_colorH}"
                : "Box: —";
            TxtColorConf.Text = $"Confidence: {_colorConfidence:P0}";
            TxtFaceCount.Text = $"Count: {_faceCount}";
            TxtFaceConf.Text = $"Confidence: {_faceConfidence:P0}";
            TxtCircleCount.Text = $"Count: {_circleCount}";
            TxtCircleConf.Text = $"Confidence: {_circleConfidence:P0}";

            // Diagnostics
            TxtDiagUptime.Text = $"Uptime: {_diagUptime}";
            TxtDiagBackend.Text = $"Backend: {_diagBackend}";
            TxtDiagFps.Text = $"FPS: {_diagFps:F1}";
            TxtDiagInspections.Text = $"Inspektionen: {_diagInspections}";
        }

        /// <summary>
        /// Industrielle Sortierlogik — simuliert SPS-Entscheidungen.
        /// Nutzt jetzt auch Confidence für differenzierte Entscheidungen.
        /// </summary>
        private void EvaluateSortingLogic()
        {
            if (!_cameraRunning)
            {
                SetDecision("⏸ Kamera gestoppt", "#555");
                return;
            }

            // Priorität 1: Sicherheit — Gesicht erkannt → Halt
            if (_faceCount > 0)
            {
                _halted++;
                _totalDecisions++;
                SetDecision("🛑 HALT — Gesicht erkannt", "#c0392b");
                AddLog($"[{DateTime.Now:HH:mm:ss}] HALT: {_faceCount} Gesicht(er) (Conf: {_faceConfidence:P0}) → Prüfstation angehalten");
                return;
            }

            // Priorität 2: Farbfehler — rotes Objekt → Aussortieren + Weiche öffnen
            if (_colorDetected && _colorConfidence > 0.3)
            {
                _sortedOut++;
                _totalDecisions++;
                SetDecision("⚠ AUSSORTIEREN — Rot erkannt", "#e67e22");
                AddLog($"[{DateTime.Now:HH:mm:ss}] AUSSORTIEREN: Rot bei ({_colorX},{_colorY}) Conf: {_colorConfidence:P0} → Weiche öffnen");

                // Automatisch Ausschleusweiche öffnen
                if (ChkRejectGate.IsChecked != true)
                {
                    ChkRejectGate.IsChecked = true;
                    WriteOpcUaValue("Control.RejectGateOpen", true);
                }
                return;
            }

            // Weiche schließen wenn kein Defekt
            if (ChkRejectGate.IsChecked == true)
            {
                ChkRejectGate.IsChecked = false;
                WriteOpcUaValue("Control.RejectGateOpen", false);
            }

            // Priorität 3: Qualitätsprüfung — genug Bohrlöcher?
            if (_circleCount >= 3)
            {
                _qualityOk++;
                _totalDecisions++;
                SetDecision("✅ QUALITÄT OK — " + _circleCount + " Kreise", "#27ae60");
                return;
            }

            SetDecision("🔍 Prüfung läuft...", "#2c3e50");
        }

        private void SetDecision(string text, string hexColor)
        {
            TxtDecision.Text = text;
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            DecisionBorder.Background = new SolidColorBrush(color);
            TxtDecisionCount.Text = $"Gesamt: {_totalDecisions}  |  OK: {_qualityOk}  |  Aussortiert: {_sortedOut}  |  Halt: {_halted}";
        }

        private void AddLog(string message)
        {
            LogList.Items.Add(message);
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(0);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        // ===== OPC-UA Helfer =====

        private static bool GetBool(DataValueCollection r, int i)
        {
            if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return false;
            return Convert.ToBoolean(r[i].Value);
        }

        private static int GetInt(DataValueCollection r, int i)
        {
            if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return 0;
            return Convert.ToInt32(r[i].Value);
        }

        private static double GetDouble(DataValueCollection r, int i)
        {
            if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return 0;
            return Convert.ToDouble(r[i].Value);
        }

        private static long GetLong(DataValueCollection r, int i)
        {
            if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return 0;
            return Convert.ToInt64(r[i].Value);
        }

        private static string GetString(DataValueCollection r, int i)
        {
            if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return "—";
            return r[i].Value?.ToString() ?? "—";
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            base.OnClosed(e);
        }
    }
}