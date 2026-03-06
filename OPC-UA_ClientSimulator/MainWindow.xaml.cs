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
    /// Subscribed die Vision-Knoten des VisionBridge Runtime und
    /// trifft Sortierentscheidungen basierend auf den Erkennungsdaten.
    /// </summary>
    public partial class MainWindow : Window
    {
        private Session? _session;
        private ushort _nsIndex = 2;
        private DispatcherTimer? _pollTimer;

        // Gecachte OPC-UA Werte
        private bool _cameraRunning;
        private bool _colorDetected;
        private int _colorX, _colorY, _colorW, _colorH;
        private int _faceCount;
        private int _circleCount;

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

                // UI aktualisieren
                BtnDisconnect.IsEnabled = true;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                TxtStatus.Text = "Verbunden";
                AddLog("✅ Verbunden mit " + TxtEndpoint.Text);

                // Polling starten
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

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

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
                new() { NodeId = new NodeId("Camera.Running", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Detected", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.X", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Y", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Width", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Color.Height", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Faces.Count", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Circles.Count", _nsIndex), AttributeId = Attributes.Value },
            };

            _session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out var results, out _);

            if (results.Count >= 8)
            {
                _cameraRunning = GetBool(results, 0);
                _colorDetected = GetBool(results, 1);
                _colorX = GetInt(results, 2);
                _colorY = GetInt(results, 3);
                _colorW = GetInt(results, 4);
                _colorH = GetInt(results, 5);
                _faceCount = GetInt(results, 6);
                _circleCount = GetInt(results, 7);
            }
        }

        private void UpdateDisplay()
        {
            TxtCameraRunning.Text = $"Running: {(_cameraRunning ? "✅ Ja" : "❌ Nein")}";
            TxtColorDetected.Text = $"Detected: {(_colorDetected ? "✅ Ja" : "—")}";
            TxtColorBox.Text = _colorDetected
                ? $"Box: ({_colorX}, {_colorY}) {_colorW}×{_colorH}"
                : "Box: —";
            TxtFaceCount.Text = $"Count: {_faceCount}";
            TxtCircleCount.Text = $"Count: {_circleCount}";
        }

        /// <summary>
        /// Industrielle Sortierlogik — simuliert SPS-Entscheidungen:
        ///   - Rotes Objekt erkannt → Aussortieren (Farb-Fehlprüfung)
        ///   - Gesicht im Bild → Prüfstation anhalten (Sicherheit)
        ///   - Kreise ≥ 3 → Qualität OK (genug Bohrlöcher)
        ///   - Sonst → Warte auf Daten
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
                AddLog($"[{DateTime.Now:HH:mm:ss}] HALT: {_faceCount} Gesicht(er) erkannt → Prüfstation angehalten");
                return;
            }

            // Priorität 2: Farbfehler — rotes Objekt → Aussortieren
            if (_colorDetected)
            {
                _sortedOut++;
                _totalDecisions++;
                SetDecision("⚠ AUSSORTIEREN — Rot erkannt", "#e67e22");
                AddLog($"[{DateTime.Now:HH:mm:ss}] AUSSORTIEREN: Rotes Objekt bei ({_colorX},{_colorY}) {_colorW}×{_colorH}");
                return;
            }

            // Priorität 3: Qualitätsprüfung — genug Bohrlöcher?
            if (_circleCount >= 3)
            {
                _qualityOk++;
                _totalDecisions++;
                SetDecision("✅ QUALITÄT OK — " + _circleCount + " Kreise", "#27ae60");
                return;
            }

            // Kein eindeutiges Ergebnis
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

            // Max 200 Einträge behalten
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(0);

            // Automatisch nach unten scrollen
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }

        private static bool GetBool(DataValueCollection results, int index)
        {
            if (index >= results.Count) return false;
            var dv = results[index];
            if (StatusCode.IsBad(dv.StatusCode)) return false;
            return Convert.ToBoolean(dv.Value);
        }

        private static int GetInt(DataValueCollection results, int index)
        {
            if (index >= results.Count) return 0;
            var dv = results[index];
            if (StatusCode.IsBad(dv.StatusCode)) return 0;
            return Convert.ToInt32(dv.Value);
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            base.OnClosed(e);
        }
    }
}