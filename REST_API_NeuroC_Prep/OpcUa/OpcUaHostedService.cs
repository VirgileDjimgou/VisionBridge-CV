using Opc.Ua;
using Opc.Ua.Configuration;
using REST_API_NeuroC_Prep.Services;

namespace REST_API_NeuroC_Prep.OpcUa;

/// <summary>
/// IHostedService — startet den OPC-UA Server im gleichen Prozess wie die REST API.
/// Beide Protokolle teilen sich den VisionService (ein Prozess, eine Kamera).
/// </summary>
public class OpcUaHostedService : IHostedService
{
    private readonly VisionService _vision;
    private readonly ILogger<OpcUaHostedService> _logger;
    private VisionOpcUaServer? _server;
    private readonly int _port;

    public OpcUaHostedService(
        VisionService vision,
        ILogger<OpcUaHostedService> logger,
        IConfiguration config)
    {
        _vision = vision;
        _logger = logger;
        _port = config.GetValue("OpcUa:Port", 4840);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var appConfig = new ApplicationConfiguration
            {
                ApplicationName = "VisionBridge Runtime",
                ApplicationUri = Utils.Format("urn:{0}:visionbridge:runtime", System.Net.Dns.GetHostName()),
                ApplicationType = ApplicationType.Server,
                ProductUri = "urn:visionbridge:runtime",

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(".", "pki", "own"),
                        SubjectName = "CN=VisionBridge Runtime, O=VisionBridge, DC=localhost"
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

                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { $"opc.tcp://localhost:{_port}/visionbridge" },
                    SecurityPolicies = new ServerSecurityPolicyCollection
                    {
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None
                        }
                    },
                    UserTokenPolicies = new UserTokenPolicyCollection
                    {
                        new UserTokenPolicy(UserTokenType.Anonymous)
                    }
                }
            };

            await appConfig.Validate(ApplicationType.Server);

            var application = new ApplicationInstance(appConfig);
            await application.CheckApplicationInstanceCertificate(false, 2048);

            _server = new VisionOpcUaServer(_vision);
            await application.Start(_server);

            _logger.LogInformation(
                "OPC-UA Server gestartet auf opc.tcp://localhost:{Port}/visionbridge", _port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC-UA Server konnte nicht gestartet werden");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server != null)
        {
            _server.Stop();
            _logger.LogInformation("OPC-UA Server gestoppt");
        }
        return Task.CompletedTask;
    }
}
