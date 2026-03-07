using System.IO;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace VisionClientWPF.Sources;

/// <summary>
/// Vision-Daten über OPC-UA Subscription.
/// Kein Video — nur skalare Erkennungswerte (X, Y, Count, Detected, Confidence).
/// Liest auch Diagnostik-Knoten (Uptime, FPS, Backend, Inspektionszähler).
/// Setzt voraus, dass der VisionBridge Runtime (OPC-UA Server) läuft.
/// </summary>
public class OpcUaVisionSource : IVisionSource
{
    public string Name => "OPC-UA";
    public bool SupportsVideo => false;
    public bool SupportsEdgeDetection => false;
    public bool SupportsDiagnostics => true;

    private readonly string _endpointUrl;
    private Session? _session;
    private ushort _nsIndex = 2;

    // Gecachte Werte
    private bool _cameraRunning;
    private bool _colorDetected;
    private int _colorX, _colorY, _colorW, _colorH;
    private double _colorConfidence;
    private int _faceCount;
    private double _faceConfidence;
    private int _circleCount;
    private double _circleConfidence;
    private string _diagUptime = "";
    private string _diagBackend = "";
    private long _diagInspections;
    private double _diagFps;
    private DateTime _lastRead = DateTime.MinValue;

    public OpcUaVisionSource(string endpointUrl = "opc.tcp://localhost:4840/visionbridge")
    {
        _endpointUrl = endpointUrl;
    }

    public bool Start()
    {
        try
        {
            return Task.Run(async () =>
            {
                var config = new ApplicationConfiguration
                {
                    ApplicationName = "VisionBridge WPF Client",
                    ApplicationUri = Utils.Format("urn:{0}:visionbridge:wpfclient",
                        System.Net.Dns.GetHostName()),
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "own"),
                            SubjectName = "CN=VisionBridge WPF Client"
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
                config.CertificateValidator.CertificateValidation += (_, e) => { e.Accept = true; };

                var application = new ApplicationInstance(config);
                await application.CheckApplicationInstanceCertificate(false, 2048);

                var endpoint = CoreClientUtils.SelectEndpoint(_endpointUrl, false);
                var endpointConfig = EndpointConfiguration.Create(config);
                var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);

                _session = await Session.Create(
                    config, configuredEndpoint, false,
                    "VisionWPFClient", 60000,
                    new UserIdentity(new AnonymousIdentityToken()), null);

                int idx = _session.NamespaceUris.GetIndex("urn:visionbridge:opcua");
                if (idx >= 0) _nsIndex = (ushort)idx;

                return true;
            }).Result;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _session?.Close();
            _session?.Dispose();
        }
        catch { }
        _session = null;
    }

    private void ReadAllValues()
    {
        if (_session == null || !_session.Connected) return;
        if ((DateTime.UtcNow - _lastRead).TotalMilliseconds < 200) return;

        try
        {
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
                new() { NodeId = new NodeId("Diagnostics.TotalInspections", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.CurrentFps", _nsIndex), AttributeId = Attributes.Value },
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
                _diagInspections = GetLong(results, 13);
                _diagFps = GetDouble(results, 14);
            }

            _lastRead = DateTime.UtcNow;
        }
        catch { }
    }

    public FrameResult? GetFrameRgb() => null;

    public ColorResult? DetectColor()
    {
        ReadAllValues();
        if (!_cameraRunning) return null;
        return new ColorResult(_colorDetected,
            new DetectionBox(_colorX, _colorY, _colorW, _colorH),
            _colorConfidence);
    }

    public MultiResult? DetectFaces()
    {
        ReadAllValues();
        if (!_cameraRunning) return null;
        return new MultiResult(_faceCount, [], _faceConfidence);
    }

    public MultiResult? DetectCircles()
    {
        ReadAllValues();
        if (!_cameraRunning) return null;
        return new MultiResult(_circleCount, [], _circleConfidence);
    }

    public EdgeResult? DetectEdges() => null;

    public RuntimeDiagnostics? GetDiagnostics()
    {
        ReadAllValues();
        return new RuntimeDiagnostics(_diagUptime, _diagBackend, _cameraRunning,
            _diagInspections, _diagFps);
    }

    public void Dispose() => Stop();

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
        if (i >= r.Count || StatusCode.IsBad(r[i].StatusCode)) return "";
        return r[i].Value?.ToString() ?? "";
    }
}
