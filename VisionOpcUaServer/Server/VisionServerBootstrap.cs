using Opc.Ua;
using Opc.Ua.Configuration;
using VisionOpcUaServer.Server;

namespace VisionOpcUaServer;

/// <summary>
/// Bootstrap: Konfiguriert und startet den OPC-UA-Server.
/// </summary>
public static class VisionServerBootstrap
{
    public static async Task<VisionStandardServer> StartAsync(int port = 4840, bool manageCamera = true)
    {
        // Applikationskonfiguration programmatisch erstellen
        var config = new ApplicationConfiguration
        {
            ApplicationName = "VisionBridge OPC-UA Server",
            ApplicationUri = Utils.Format("urn:{0}:visionbridge:server", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Server,
            ProductUri = "urn:visionbridge:opcua:server",

            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(".", "pki", "own"),
                    SubjectName = "CN=VisionBridge OPC-UA Server, O=VisionBridge, DC=localhost"
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
                AutoAcceptUntrustedCertificates = true // Dev-Modus
            },

            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },

            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://localhost:{port}/visionbridge" },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,

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
            },

            TraceConfiguration = new TraceConfiguration
            {
                OutputFilePath = Path.Combine(".", "logs", "opcua-server.log"),
                DeleteOnLoad = true,
                TraceMasks = Utils.TraceMasks.Error | Utils.TraceMasks.Information
            }
        };

        await config.Validate(ApplicationType.Server);

        // Zertifikat prüfen / erstellen
        var application = new ApplicationInstance(config);
        bool certOk = await application.CheckApplicationInstanceCertificate(false, 2048);
        if (!certOk)
        {
            Console.WriteLine("[WARN] Zertifikat konnte nicht erstellt werden — Server startet trotzdem.");
        }

        // Server starten
        var server = new VisionStandardServer(manageCamera);
        await application.Start(server);

        return server;
    }
}
