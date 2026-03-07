using System.IO;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace OPC_UA_ClientSimulator.Sources;

/// <summary>
/// Vision-Daten über OPC-UA.
/// Kein Video — nur skalare Erkennungswerte (Detected, Count, Confidence).
/// Implementiert IPlantControl über OPC-UA Write + Method Calls.
/// </summary>
public class OpcUaVisionSource : IVisionSource, IPlantControl
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
                    ApplicationName = "VisionBridge Unified Client",
                    ApplicationUri = Utils.Format("urn:{0}:visionbridge:unified",
                        System.Net.Dns.GetHostName()),
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(".", "pki", "own"),
                            SubjectName = "CN=VisionBridge Unified Client"
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
                    "VisionUnifiedClient", 60000,
                    new UserIdentity(new AnonymousIdentityToken()), null);

                int idx = _session.NamespaceUris.GetIndex("urn:visionbridge:opcua");
                if (idx >= 0) _nsIndex = (ushort)idx;

                return true;
            }).Result;
        }
        catch { return false; }
    }

    public void Stop()
    {
        try { _session?.Close(); _session?.Dispose(); }
        catch { }
        _session = null;
    }

    // ===== IVisionSource — liest alle Knoten gebatcht =====

    private void ReadAllValues()
    {
        if (_session == null || !_session.Connected) return;
        if ((DateTime.UtcNow - _lastRead).TotalMilliseconds < 200) return;

        try
        {
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
                new() { NodeId = new NodeId("Color.Confidence", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Faces.Confidence", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Circles.Confidence", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.Uptime", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.BackendMode", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.TotalInspections", _nsIndex), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId("Diagnostics.CurrentFps", _nsIndex), AttributeId = Attributes.Value },
            };

            _session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out var r, out _);
            if (r.Count < 15) return;

            _cameraRunning = GetBool(r, 0);
            _colorDetected = GetBool(r, 1);
            _colorX = GetInt(r, 2); _colorY = GetInt(r, 3);
            _colorW = GetInt(r, 4); _colorH = GetInt(r, 5);
            _faceCount = GetInt(r, 6); _circleCount = GetInt(r, 7);
            _colorConfidence = GetDouble(r, 8);
            _faceConfidence = GetDouble(r, 9);
            _circleConfidence = GetDouble(r, 10);
            _diagUptime = GetString(r, 11); _diagBackend = GetString(r, 12);
            _diagInspections = GetLong(r, 13); _diagFps = GetDouble(r, 14);

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
            new DetectionBox(_colorX, _colorY, _colorW, _colorH), _colorConfidence);
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

    // ===== IPlantControl — OPC-UA Write + Methods =====

    public void CameraStart() => CallMethod("Camera", "Camera.Start");
    public void CameraStop() => CallMethod("Camera", "Camera.Stop");
    public void SetConveyorSpeed(double speed) => WriteValue("Control.ConveyorSpeed", speed);
    public void SetInspectionEnabled(bool enabled) => WriteValue("Control.InspectionEnabled", enabled);
    public void SetRejectGateOpen(bool open) => WriteValue("Control.RejectGateOpen", open);

    private void WriteValue(string nodeIdentifier, object value)
    {
        if (_session == null || !_session.Connected) return;
        try
        {
            var wv = new WriteValue
            {
                NodeId = new NodeId(nodeIdentifier, _nsIndex),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            };
            _session.Write(null, new WriteValueCollection { wv }, out _, out _);
        }
        catch { }
    }

    private void CallMethod(string objectId, string methodId)
    {
        if (_session == null || !_session.Connected) return;
        try
        {
            _session.Call(
                new NodeId(objectId, _nsIndex),
                new NodeId(methodId, _nsIndex),
                Array.Empty<object>());
        }
        catch { }
    }

    public void Dispose() => Stop();

    // ===== Helfer =====

    private static bool GetBool(DataValueCollection r, int i)
        => i < r.Count && !StatusCode.IsBad(r[i].StatusCode) && Convert.ToBoolean(r[i].Value);
    private static int GetInt(DataValueCollection r, int i)
        => i < r.Count && !StatusCode.IsBad(r[i].StatusCode) ? Convert.ToInt32(r[i].Value) : 0;
    private static double GetDouble(DataValueCollection r, int i)
        => i < r.Count && !StatusCode.IsBad(r[i].StatusCode) ? Convert.ToDouble(r[i].Value) : 0;
    private static long GetLong(DataValueCollection r, int i)
        => i < r.Count && !StatusCode.IsBad(r[i].StatusCode) ? Convert.ToInt64(r[i].Value) : 0;
    private static string GetString(DataValueCollection r, int i)
        => i < r.Count && !StatusCode.IsBad(r[i].StatusCode) ? r[i].Value?.ToString() ?? "—" : "—";
}
